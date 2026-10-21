using ContosoDashboard.Services;

namespace ContosoDashboard.Tests;

public class LocalFileStorageServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "contoso-storage-" + Guid.NewGuid().ToString("N"));
    private readonly LocalFileStorageService _storage;

    public LocalFileStorageServiceTests()
    {
        Directory.CreateDirectory(_root);
        _storage = new LocalFileStorageService(_root);
    }

    private static MemoryStream Bytes(string text) => new(System.Text.Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task Upload_Download_Delete_RoundTrip()
    {
        var key = $"1/personal/{Guid.NewGuid()}.pdf";

        var stored = await _storage.UploadAsync(Bytes("hello"), key, "application/pdf");
        Assert.Equal(key, stored);

        await using (var stream = await _storage.DownloadAsync(key))
        using (var reader = new StreamReader(stream))
        {
            Assert.Equal("hello", await reader.ReadToEndAsync());
        }

        await _storage.DeleteAsync(key);
        await Assert.ThrowsAsync<FileNotFoundException>(() => _storage.DownloadAsync(key));
    }

    [Theory]
    [InlineData("../escape.pdf")]
    [InlineData("1/../../escape.pdf")]
    [InlineData("/rooted/file.pdf")]
    [InlineData("C:/windows/file.pdf")]
    [InlineData("1\\2\\file.pdf")]
    [InlineData("1//file.pdf")]
    [InlineData("./file.pdf")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task InvalidKeys_AreRejected(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.UploadAsync(Bytes("x"), key, "text/plain"));
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.DownloadAsync(key));
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.DeleteAsync(key));
    }

    [Fact]
    public async Task Upload_RefusesToOverwriteExistingKey()
    {
        var key = $"1/personal/{Guid.NewGuid()}.txt";
        await _storage.UploadAsync(Bytes("first"), key, "text/plain");

        await Assert.ThrowsAsync<IOException>(() => _storage.UploadAsync(Bytes("second"), key, "text/plain"));

        await using var stream = await _storage.DownloadAsync(key);
        using var reader = new StreamReader(stream);
        Assert.Equal("first", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Delete_IsIdempotent()
    {
        await _storage.DeleteAsync($"1/personal/{Guid.NewGuid()}.pdf");
    }

    [Fact]
    public async Task FailedWrite_LeavesNoPartialFile()
    {
        var key = $"1/personal/{Guid.NewGuid()}.pdf";

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _storage.UploadAsync(new FailingStream(), key, "application/pdf"));

        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task GetUrl_IsNotSupported()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() => _storage.GetUrlAsync("1/personal/a.pdf", TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void ResolveRoot_RejectsLocationsInsideWwwroot()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "site");
        var options = new DocumentStorageOptions { RootPath = "wwwroot/uploads" };

        Assert.Throws<InvalidOperationException>(() => LocalFileStorageService.ResolveRoot(options, contentRoot));
        Assert.EndsWith(Path.Combine("AppData", "uploads"),
            LocalFileStorageService.ResolveRoot(new DocumentStorageOptions(), contentRoot));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FailingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("boom");
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => throw new InvalidOperationException("boom");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("boom");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
