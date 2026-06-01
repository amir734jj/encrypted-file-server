using System.Text;
using System.Web;
using Api.Data.Entities;
using Api.Extensions;
using Api.Interfaces;
using EfCoreRepository.Interfaces;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Shared.Interfaces;
using Shared.Models;

namespace Api.Controllers;

[ApiController]
[Route("browse")]
public sealed class BrowseController(
    IEfRepository repository,
    IFileStorageService fileStorage,
    IEncryptionProviderFactory encryptionFactory,
    UserManager<User> userManager) : ControllerBase
{
    private IBasicCrud<DataSource> DataSourceDal => repository.For<DataSource>();
    private IBasicCrud<EncryptedFile> FileDal => repository.For<EncryptedFile>();
    private IBasicCrud<AccessTicket> TicketDal => repository.For<AccessTicket>();

    [HttpGet]
    public async Task<IActionResult> ListUsers()
    {
        var userId = GetAuthenticatedUserId();

        // Try HTTP Basic auth via access tickets
        if (userId is null)
        {
            userId = await TryBasicAuthUserId();
        }

        if (userId is null)
        {
            Response.Headers["WWW-Authenticate"] = "Basic realm=\"File Server\"";
            return Unauthorized();
        }

        var dataSources = (await DataSourceDal.GetAll(
            filterExprs: [d => d.UserId == userId],
            project: d => d)).Where(d => d.HasFrontend(FrontendType.Http)).OrderBy(d => d.Name).ToList();

        return Content(RenderDirectoryPage("/ - File Server", "/browse",
            dataSources.Select(ds => (ds.Name, $"/browse/{ds.Id}/", (string?)null, (long?)null, (DateTimeOffset?)ds.CreatedAt))), "text/html");
    }

    [HttpGet("{dataSourceId:guid}/{**path}")]
    public async Task<IActionResult> ListOrDownload(Guid dataSourceId, string? path = null, [FromQuery] bool raw = false)
    {
        var (ds, masterKey, error) = await AuthorizeDataSource(dataSourceId);
        if (error is not null) return error;

        var encryption = encryptionFactory.GetProvider(ds!.Backend.EncryptionMethod);
        var isEncrypted = ds.Backend.EncryptionMethod != EncryptionMethod.None;
        path = NormalizePath(path);

        // If path ends with a GUID, try to serve it as a file download
        if (TryParseFileId(path, out var fileId))
        {
            var matchFiles = (await FileDal.GetAll(
                filterExprs: [f => f.Id == fileId && f.DataSourceId == dataSourceId && f.UserId == ds!.UserId],
                project: f => f,
                maxResults: 1)).ToList();
            if (matchFiles.Count > 0)
            {
                var file = matchFiles.First();

                if (raw && isEncrypted)
                {
                    var rawStream = await fileStorage.OpenRawStreamAsync(file);
                    var rawFileName = System.IO.Path.GetFileName(file.StoragePath);
                    return File(rawStream, "application/octet-stream", rawFileName);
                }

                var iv = Convert.FromBase64String(file.IvBase64);
                var fullPath = encryption.DecryptString(file.OriginalFileName, masterKey!, iv);
                var fileName = System.IO.Path.GetFileName(fullPath);
                var contentType = file.ContentType is not null
                    ? encryption.DecryptString(file.ContentType, masterKey!, iv)
                    : "application/octet-stream";
                var stream = await fileStorage.OpenDecryptedStreamAsync(file);
                return File(stream, contentType, fileName);
            }
        }

        // Directory listing
        var allFiles = (await FileDal.GetAll(
            filterExprs: [f => f.DataSourceId == dataSourceId && f.UserId == ds!.UserId],
            project: f => f)).ToList();

        var entries = new List<(string Name, string Href, string? RawHref, long? Size, DateTimeOffset? Modified)>();
        var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in allFiles)
        {
            var iv = Convert.FromBase64String(f.IvBase64);
            var fullPath = encryption.DecryptString(f.OriginalFileName, masterKey!, iv);

            if (!fullPath.StartsWith(path, StringComparison.OrdinalIgnoreCase))
                continue;

            var relativePath = fullPath[path.Length..];
            var slashIndex = relativePath.IndexOf('/');

            if (slashIndex < 0)
            {
                var href = $"/browse/{dataSourceId}/{path}{f.Id}";
                var rawHref = isEncrypted ? $"{href}?raw=true" : null;
                entries.Add((relativePath, href, rawHref, f.OriginalFileSize, f.CreatedAt));
            }
            else
            {
                var folderName = relativePath[..slashIndex];
                if (seenFolders.Add(folderName))
                    entries.Add((folderName, $"/browse/{dataSourceId}/{path}{folderName}/", null, null, null));
            }
        }

        entries = entries
            .OrderBy(e => e.Href.EndsWith('/') ? 0 : 1)
            .ThenBy(e => e.Name)
            .ToList();

        var displayPath = string.IsNullOrEmpty(path) ? $"/{ds!.Name}/" : $"/{ds!.Name}/{path}";
        var parentHref = string.IsNullOrEmpty(path)
            ? "/browse/"
            : $"/browse/{dataSourceId}/{GetParentPath(path)}";

        return Content(RenderDirectoryPage($"{displayPath} - File Server",
            $"/browse/{dataSourceId}/{path}", entries, parentHref), "text/html");
    }

    private Guid? GetAuthenticatedUserId()
    {
        if (User.Identity?.IsAuthenticated == true)
            return User.GetUserId();
        return null;
    }

    private async Task<Guid?> TryBasicAuthUserId()
    {
        var authHeader = Request.Headers.Authorization.ToString();
        if (!authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return null;

        var encoded = authHeader["Basic ".Length..].Trim();
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        var colonIdx = decoded.IndexOf(':');
        var username = colonIdx >= 0 ? decoded[..colonIdx] : decoded;
        var password = colonIdx >= 0 ? decoded[(colonIdx + 1)..] : string.Empty;

        var tickets = (await TicketDal.GetAll(
            filterExprs: [t => t.Username == username
                && t.Password == password
                && t.ExpiresAt > DateTimeOffset.UtcNow],
            project: t => t,
            maxResults: 1)).ToList();

        if (tickets.Count == 0)
            return null;

        var ticket = tickets.First();

        // Reject if the ticket owner's account is disabled
        var ticketOwner = await userManager.FindByIdAsync(ticket.UserId.ToString());
        if (ticketOwner is null || !ticketOwner.IsActive)
            return null;

        return ticket.UserId;
    }

    private async Task<(DataSource? ds, byte[]? masterKey, IActionResult? error)> AuthorizeDataSource(Guid dataSourceId)
    {
        var dataSources = (await DataSourceDal.GetAll(
            filterExprs: [d => d.Id == dataSourceId],
            project: d => d,
            maxResults: 1)).ToList();

        if (dataSources.Count == 0)
            return (null, null, NotFound());

        var ds = dataSources.First();
        var httpFrontend = ds.GetFrontend(FrontendType.Http);

        if (httpFrontend is null)
            return (null, null, NotFound());

        var masterKey = KeyDerivation.DeriveKey(ds.Backend.MasterPassword);

        if (httpFrontend.AllowAnonymous)
            return (ds, masterKey, null);

        if (User.Identity?.IsAuthenticated == true)
        {
            var currentUserId = User.GetUserId();
            if (currentUserId != ds.UserId)
                return (null, null, Forbid());

            return (ds, masterKey, null);
        }

        // HTTP Basic auth — validate against access tickets
        var authHeader = Request.Headers.Authorization.ToString();
        if (authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            var encoded = authHeader["Basic ".Length..].Trim();
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var colonIdx = decoded.IndexOf(':');
            var username = colonIdx >= 0 ? decoded[..colonIdx] : decoded;
            var password = colonIdx >= 0 ? decoded[(colonIdx + 1)..] : string.Empty;

            var tickets = (await TicketDal.GetAll(
                filterExprs: [t => t.Username == username
                    && t.Password == password
                    && t.ExpiresAt > DateTimeOffset.UtcNow
                    && t.UserId == ds.UserId],
                project: t => t,
                maxResults: 1)).ToList();

            if (tickets.Count > 0)
            {
                // Reject if the ticket owner's account is disabled
                var ticketOwner = await userManager.FindByIdAsync(tickets.First().UserId.ToString());
                if (ticketOwner is not null && ticketOwner.IsActive)
                    return (ds, masterKey, null);
            }
        }

        Response.Headers["WWW-Authenticate"] = $"Basic realm=\"{ds.Name}\"";
        return (null, null, Unauthorized());
    }

    private static string RenderDirectoryPage(string title, string currentPath,
        IEnumerable<(string Name, string Href, string? RawHref, long? Size, DateTimeOffset? Modified)> entries,
        string? parentHref = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html><head>");
        sb.AppendLine($"<title>{HttpUtility.HtmlEncode(title)}</title>");
        sb.AppendLine("<meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.AppendLine("<link rel=\"preconnect\" href=\"https://fonts.googleapis.com\">");
        sb.AppendLine("<link rel=\"preconnect\" href=\"https://fonts.gstatic.com\" crossorigin>");
        sb.AppendLine("<link href=\"https://fonts.googleapis.com/css2?family=JetBrains+Mono:wght@400;600&display=swap\" rel=\"stylesheet\">");
        sb.AppendLine("<style>");
        sb.AppendLine(":root { --bg: #1a1a2e; --bg-hover: #16213e; --text: #e0e0e0; --heading: #00d4ff; --link: #00d4ff; --muted: #888; --dim: #666; --border: #333; }");
        sb.AppendLine("[data-theme='light'] { --bg: #f5f5f5; --bg-hover: #e8e8e8; --text: #1a1a1a; --heading: #0066cc; --link: #0066cc; --muted: #666; --dim: #999; --border: #ddd; }");
        sb.AppendLine("body { font-family: 'JetBrains Mono', monospace; margin: 2em; background: var(--bg); color: var(--text); transition: background 0.2s, color 0.2s; }");
        sb.AppendLine("h1 { font-size: 1.4em; color: var(--heading); border-bottom: 1px solid var(--border); padding-bottom: 0.5em; }");
        sb.AppendLine("table { border-collapse: collapse; width: 100%; }");
        sb.AppendLine("th, td { text-align: left; padding: 4px 12px; }");
        sb.AppendLine("th { color: var(--muted); font-weight: normal; border-bottom: 1px solid var(--border); }");
        sb.AppendLine("tr:hover { background: var(--bg-hover); }");
        sb.AppendLine("a { color: var(--link); text-decoration: none; }");
        sb.AppendLine("a:hover { text-decoration: underline; }");
        sb.AppendLine(".size { color: var(--muted); text-align: right; }");
        sb.AppendLine(".date { color: var(--dim); }");
        sb.AppendLine(".header { display: flex; justify-content: space-between; align-items: center; }");
        sb.AppendLine(".theme-btn { background: none; border: 1px solid var(--border); color: var(--text); cursor: pointer; padding: 4px 10px; border-radius: 4px; font-family: inherit; font-size: 0.85em; }");
        sb.AppendLine(".theme-btn:hover { background: var(--bg-hover); }");
        sb.AppendLine(".raw { color: var(--muted); font-size: 0.85em; }");
        sb.AppendLine(".raw a { color: var(--muted); }");
        sb.AppendLine(".raw a:hover { color: var(--link); }");
        sb.AppendLine("</style>");
        sb.AppendLine("</head><body>");
        sb.AppendLine("<div class=\"header\">");
        sb.AppendLine($"<h1>Index of {HttpUtility.HtmlEncode(currentPath)}</h1>");
        sb.AppendLine("<button class=\"theme-btn\" onclick=\"toggleTheme()\" id=\"themeBtn\">☀️ Light</button>");
        sb.AppendLine("</div>");
        sb.AppendLine("<table>");
        var hasAnyRaw = entries.Any(e => e.RawHref is not null);
        var rawHeader = hasAnyRaw ? "<th class=\"raw\">Raw</th>" : "";
        sb.AppendLine($"<tr><th>Name</th><th class=\"size\">Size</th><th class=\"date\">Modified</th>{rawHeader}</tr>");

        if (parentHref is not null)
        {
            var rawCell = hasAnyRaw ? "<td class=\"raw\"></td>" : "";
            sb.AppendLine($"<tr><td><a href=\"{parentHref}\">../</a></td><td class=\"size\">-</td><td class=\"date\">-</td>{rawCell}</tr>");
        }

        foreach (var (name, href, rawHref, size, modified) in entries)
        {
            var isDir = href.EndsWith('/');
            var displayName = isDir ? $"{HttpUtility.HtmlEncode(name)}/" : HttpUtility.HtmlEncode(name);
            var sizeStr = size.HasValue ? FormatSize(size.Value) : "-";
            var dateStr = modified.HasValue ? modified.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm") : "-";
            var rawCell = hasAnyRaw
                ? $"<td class=\"raw\">{(rawHref is not null ? $"<a href=\"{rawHref}\" title=\"Download raw encrypted file\">raw</a>" : "")}</td>"
                : "";
            sb.AppendLine($"<tr><td><a href=\"{href}\">{displayName}</a></td><td class=\"size\">{sizeStr}</td><td class=\"date\">{dateStr}</td>{rawCell}</tr>");
        }

        sb.AppendLine("</table>");
        sb.AppendLine("<script>");
        sb.AppendLine("function toggleTheme() {");
        sb.AppendLine("  var html = document.documentElement;");
        sb.AppendLine("  var current = html.getAttribute('data-theme');");
        sb.AppendLine("  var next = current === 'light' ? 'dark' : 'light';");
        sb.AppendLine("  html.setAttribute('data-theme', next);");
        sb.AppendLine("  localStorage.setItem('browse-theme', next);");
        sb.AppendLine("  updateBtn(next);");
        sb.AppendLine("}");
        sb.AppendLine("function updateBtn(t) {");
        sb.AppendLine("  document.getElementById('themeBtn').textContent = t === 'light' ? '\\u{1F319} Dark' : '\\u{2600}\\u{FE0F} Light';");
        sb.AppendLine("}");
        sb.AppendLine("(function() {");
        sb.AppendLine("  var saved = localStorage.getItem('browse-theme') || 'dark';");
        sb.AppendLine("  document.documentElement.setAttribute('data-theme', saved);");
        sb.AppendLine("  updateBtn(saved);");
        sb.AppendLine("})();");
        sb.AppendLine("</script>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F1} GB"
    };

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        path = path.Replace('\\', '/').Trim('/');
        return path.Length > 0 ? path + "/" : string.Empty;
    }

    private static string GetParentPath(string path)
    {
        var trimmed = path.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        return lastSlash < 0 ? "" : trimmed[..(lastSlash + 1)];
    }

    private static bool TryParseFileId(string path, out Guid fileId)
    {
        fileId = Guid.Empty;
        var trimmed = path.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        var segment = lastSlash < 0 ? trimmed : trimmed[(lastSlash + 1)..];
        return Guid.TryParse(segment, out fileId);
    }
}
