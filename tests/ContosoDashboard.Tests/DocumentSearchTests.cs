using ContosoDashboard.Models;
using ContosoDashboard.Services;

namespace ContosoDashboard.Tests;

public class DocumentSearchTests : IDisposable
{
    private readonly ServiceHarness _h = new();
    private readonly UserContext _emp = TestDb.Employee;

    public void Dispose() => _h.Dispose();

    private static readonly DateTime Day1 = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    private (int a, int b, int c) SeedThree()
    {
        var a = _h.Db.AddDocument(_emp, DocumentCategories.Reports, projectId: 1, title: "Alpha budget",
            fileSize: 3000, uploadDate: Day1, description: "Yearly numbers", tags: "finance,yearly");
        var b = _h.Db.AddDocument(_emp, DocumentCategories.Presentations, title: "Beta deck",
            fileSize: 1000, uploadDate: Day1.AddDays(5), tags: "slides");
        var c = _h.Db.AddDocument(_emp, DocumentCategories.PersonalFiles, title: "Charlie notes",
            fileSize: 2000, uploadDate: Day1.AddDays(10));
        return (a, b, c);
    }

    private async Task<List<int>> MyIdsAsync(DocumentQuery query, UserContext? user = null)
        => (await _h.Service.GetMyDocumentsAsync(query, user ?? _emp)).Items.Select(d => d.DocumentId).ToList();

    [Fact]
    public async Task MyDocuments_ShowRequiredColumns()
    {
        var (a, _, _) = SeedThree();

        var item = (await _h.Service.GetMyDocumentsAsync(new DocumentQuery(), _emp)).Items.Single(d => d.DocumentId == a);

        Assert.Equal("Alpha budget", item.Title);
        Assert.Equal(DocumentCategories.Reports, item.Category);
        Assert.Equal(Day1, item.UploadDate);
        Assert.Equal(3000, item.FileSize);
        Assert.Equal("ContosoDashboard Development", item.ProjectName);
        Assert.Equal("Ni Kang", item.UploaderName);
    }

    [Theory]
    [InlineData(DocumentQuery.SortTitle, false, "Alpha budget,Beta deck,Charlie notes")]
    [InlineData(DocumentQuery.SortTitle, true, "Charlie notes,Beta deck,Alpha budget")]
    [InlineData(DocumentQuery.SortUploadDate, false, "Alpha budget,Beta deck,Charlie notes")]
    [InlineData(DocumentQuery.SortUploadDate, true, "Charlie notes,Beta deck,Alpha budget")]
    [InlineData(DocumentQuery.SortCategory, false, "Charlie notes,Beta deck,Alpha budget")]
    [InlineData(DocumentQuery.SortCategory, true, "Alpha budget,Beta deck,Charlie notes")]
    [InlineData(DocumentQuery.SortFileSize, false, "Beta deck,Charlie notes,Alpha budget")]
    [InlineData(DocumentQuery.SortFileSize, true, "Alpha budget,Charlie notes,Beta deck")]
    public async Task Sorting_ByEachColumn_BothDirections(string sortBy, bool descending, string expectedTitles)
    {
        SeedThree();

        var result = await _h.Service.GetMyDocumentsAsync(
            new DocumentQuery { SortBy = sortBy, Descending = descending }, _emp);

        Assert.Equal(expectedTitles, string.Join(",", result.Items.Select(d => d.Title)));
    }

    [Fact]
    public async Task Filters_ByCategory_Project_AndDateRange()
    {
        var (a, b, c) = SeedThree();

        Assert.Equal(new[] { b }, await MyIdsAsync(new DocumentQuery { Category = DocumentCategories.Presentations }));
        Assert.Equal(new[] { a }, await MyIdsAsync(new DocumentQuery { ProjectId = 1 }));

        // Inclusive bounds: Day1 .. Day1+5 includes a and b, even with a time-of-day on the end date
        var range = await MyIdsAsync(new DocumentQuery
        {
            FromDate = Day1.Date, ToDate = Day1.AddDays(5).Date, SortBy = DocumentQuery.SortUploadDate, Descending = false
        });
        Assert.Equal(new[] { a, b }, range);

        Assert.Equal(new[] { c }, await MyIdsAsync(new DocumentQuery { FromDate = Day1.AddDays(6) }));
        Assert.Empty(await MyIdsAsync(new DocumentQuery { Category = DocumentCategories.Presentations, ProjectId = 1 }));
    }

    [Fact]
    public async Task Search_MatchesTitleDescriptionTagsUploaderAndProject_CaseInsensitively()
    {
        var (a, b, c) = SeedThree();

        async Task<List<int>> Find(string term)
            => (await _h.Service.SearchAsync(term, new DocumentQuery(), _emp)).Items.Select(d => d.DocumentId).OrderBy(x => x).ToList();

        Assert.Equal(new[] { a }, await Find("budget"));            // title
        Assert.Equal(new[] { a }, await Find("YEARLY NUMBERS"));    // description, case-insensitive
        Assert.Equal(new[] { b }, await Find("SLIDES"));            // tag, case-insensitive
        Assert.Equal(new[] { a, b, c }, await Find("ni kang"));     // uploader name
        Assert.Equal(new[] { a }, await Find("contosodashboard dev")); // project name
        Assert.Empty(await Find("nothing matches this"));
    }

    [Fact]
    public async Task Search_WithEmptyTerm_ReturnsAllAccessibleDocuments()
    {
        var (a, b, c) = SeedThree();

        var all = await _h.Service.SearchAsync("  ", new DocumentQuery(), _emp);

        Assert.Equal(new[] { a, b, c }, all.Items.Select(d => d.DocumentId).OrderBy(x => x).ToArray());
        Assert.Equal(3, all.TotalCount);
    }

    [Fact]
    public async Task Search_TreatsWildcardCharactersLiterally()
    {
        _h.Db.AddDocument(_emp, title: "Growth 100% target");
        _h.Db.AddDocument(_emp, title: "Plain document");
        _h.Db.AddDocument(_emp, title: "snake_case name");

        Assert.Single((await _h.Service.SearchAsync("%", new DocumentQuery(), _emp)).Items);
        Assert.Single((await _h.Service.SearchAsync("_", new DocumentQuery(), _emp)).Items);
    }

    [Fact]
    public async Task Search_ExcludesDocumentsTheCallerCannotAccess()
    {
        var mine = _h.Db.AddDocument(_emp, DocumentCategories.PersonalFiles, title: "Salary plan");
        var shared = _h.Db.AddDocument(_emp, DocumentCategories.PersonalFiles, title: "Salary shared");
        _h.Db.ShareWithUser(shared, TestDb.OtherEngineer, _emp);

        var otherView = (await _h.Service.SearchAsync("salary", new DocumentQuery(), TestDb.OtherEngineer)).Items;
        Assert.Equal(new[] { shared }, otherView.Select(d => d.DocumentId).ToArray());

        Assert.Empty((await _h.Service.SearchAsync("salary", new DocumentQuery(), TestDb.SalesEmployee)).Items);
        Assert.Equal(2, (await _h.Service.SearchAsync("salary", new DocumentQuery(), TestDb.Admin)).Items.Count);
        Assert.DoesNotContain(otherView, d => d.DocumentId == mine);
    }

    [Fact]
    public async Task Search_ExcludesNonAvailableDocuments_OfOtherUsers()
    {
        _h.Db.AddDocument(_emp, DocumentCategories.ProjectDocuments, projectId: 1, title: "Hidden pending", scanStatus: ScanStatuses.Pending);
        var visible = _h.Db.AddDocument(_emp, DocumentCategories.ProjectDocuments, projectId: 1, title: "Visible one");

        var asManager = (await _h.Service.SearchAsync("", new DocumentQuery(), TestDb.ProjectManager)).Items;

        Assert.Equal(new[] { visible }, asManager.Select(d => d.DocumentId).ToArray());
    }

    [Fact]
    public async Task MyDocuments_IncludesOwnNonAvailable_WithStatus()
    {
        _h.Db.AddDocument(_emp, title: "Scanning", scanStatus: ScanStatuses.Pending);
        _h.Db.AddDocument(_emp, title: "Rejected one", scanStatus: ScanStatuses.Rejected);

        var items = (await _h.Service.GetMyDocumentsAsync(new DocumentQuery(), _emp)).Items;

        Assert.Equal(2, items.Count);
        Assert.Contains(items, d => d.ScanStatus == ScanStatuses.Pending);
        Assert.Contains(items, d => d.ScanStatus == ScanStatuses.Rejected);
    }

    [Fact]
    public async Task MyDocuments_ContainsOnlyTheCallersUploads()
    {
        _h.Db.AddDocument(_emp, title: "Mine");
        _h.Db.AddDocument(TestDb.OtherEngineer, title: "Theirs");

        var items = (await _h.Service.GetMyDocumentsAsync(new DocumentQuery(), _emp)).Items;

        Assert.Equal("Mine", Assert.Single(items).Title);
    }

    [Fact]
    public async Task Paging_UsesDefaultPageSizeOf50()
    {
        for (var i = 0; i < 55; i++)
        {
            _h.Db.AddDocument(_emp, title: $"Doc {i:00}");
        }

        var page1 = await _h.Service.GetMyDocumentsAsync(new DocumentQuery(), _emp);
        var page2 = await _h.Service.GetMyDocumentsAsync(new DocumentQuery { Page = 2 }, _emp);

        Assert.Equal(50, page1.Items.Count);
        Assert.Equal(55, page1.TotalCount);
        Assert.Equal(5, page2.Items.Count);
        Assert.Empty(page1.Items.Select(d => d.DocumentId).Intersect(page2.Items.Select(d => d.DocumentId)));
    }
}
