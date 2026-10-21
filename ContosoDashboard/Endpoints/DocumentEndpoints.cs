using Microsoft.Net.Http.Headers;
using ContosoDashboard.Models;
using ContosoDashboard.Services;

namespace ContosoDashboard.Endpoints;

// Files live outside wwwroot, so they are only reachable through these authorized endpoints.
// Missing, unauthorized, and not-yet-available documents all produce the same 404 (no existence leak).
public static class DocumentEndpoints
{
    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/documents/{id:int}/download", DownloadAsync).RequireAuthorization();
        app.MapGet("/documents/{id:int}/preview", PreviewAsync).RequireAuthorization();
        return app;
    }

    private static async Task<IResult> DownloadAsync(int id, HttpContext http, IDocumentService documents)
    {
        var user = UserContext.FromPrincipal(http.User);
        var file = await documents.OpenForDownloadAsync(id, user);
        if (file == null)
        {
            return Results.NotFound();
        }

        ApplyCommonHeaders(http);

        // Results.File sets Content-Disposition: attachment with the (encoded) original file name
        return Results.File(file.Content, file.ContentType, DocumentFileValidator.SanitizeFileName(file.FileName));
    }

    private static async Task<IResult> PreviewAsync(int id, HttpContext http, IDocumentService documents)
    {
        var user = UserContext.FromPrincipal(http.User);

        var details = await documents.GetAsync(id, user);
        if (details == null || details.ScanStatus != ScanStatuses.Available)
        {
            return Results.NotFound();
        }

        if (!DocumentFileValidator.IsPreviewable(details.FileType))
        {
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        var file = await documents.OpenForPreviewAsync(id, user);
        if (file == null)
        {
            return Results.NotFound();
        }

        ApplyCommonHeaders(http);
        http.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("inline")
        {
            FileNameStar = DocumentFileValidator.SanitizeFileName(file.FileName)
        }.ToString();

        // The app-wide policy is X-Frame-Options: DENY; only this route may be framed, and only by this site
        http.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";

        return Results.File(file.Content, file.ContentType);
    }

    private static void ApplyCommonHeaders(HttpContext http)
    {
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        http.Response.Headers.CacheControl = "private, no-store";
    }
}
