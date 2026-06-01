using System.Text;
using System.Web;
using Api.Data.Entities;
using Api.Extensions;
using Api.Interfaces;
using EfCoreRepository.Interfaces;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Shared.Models;

namespace Api.Controllers;

[ApiController]
[Route("raw")]
public sealed class RawBrowseController(
    IEfRepository repository,
    IFileStorageService fileStorage,
    UserManager<User> userManager) : ControllerBase
{
    private IBasicCrud<DataSource> DataSourceDal => repository.For<DataSource>();
    private IBasicCrud<EncryptedFile> FileDal => repository.For<EncryptedFile>();
    private IBasicCrud<AccessTicket> TicketDal => repository.For<AccessTicket>();

    [HttpGet]
    public async Task<IActionResult> ListDataSources()
    {
        var userId = await ResolveUserId();
        if (userId is null)
        {
            Response.Headers["WWW-Authenticate"] = "Basic realm=\"File Server\"";
            return Unauthorized();
        }

        var dataSources = (await DataSourceDal.GetAll(
            filterExprs: [d => d.UserId == userId],
            project: d => d)).Where(d => d.HasFrontend(FrontendType.Http)).OrderBy(d => d.Name).ToList();

        return Content(RenderPage("/ - Raw Storage", "/raw",
            dataSources.Select(ds => (ds.Name, $"/raw/{ds.Id}/", (long?)null, (DateTimeOffset?)ds.CreatedAt))), "text/html");
    }

    [HttpGet("{dataSourceId:guid}/{**path}")]
    public async Task<IActionResult> ListOrDownload(Guid dataSourceId, string? path = null)
    {
        var (ds, error) = await AuthorizeDataSource(dataSourceId);
        if (error is not null) return error;

        path = NormalizePath(path);

        // If path ends with a GUID, serve the raw file
        if (TryParseFileId(path, out var fileId))
        {
            var matchFiles = (await FileDal.GetAll(
                filterExprs: [f => f.Id == fileId && f.DataSourceId == dataSourceId && f.UserId == ds!.UserId],
                project: f => f,
                maxResults: 1)).ToList();

            if (matchFiles.Count > 0)
            {
                var file = matchFiles.First();
                var rawStream = await fileStorage.OpenRawStreamAsync(file);
                var rawFileName = System.IO.Path.GetFileName(file.StoragePath);
                return File(rawStream, "application/octet-stream", rawFileName);
            }
        }

        // Directory listing — show raw storage paths
        var allFiles = (await FileDal.GetAll(
            filterExprs: [f => f.DataSourceId == dataSourceId && f.UserId == ds!.UserId],
            project: f => f)).ToList();

        var entries = new List<(string Name, string Href, long? Size, DateTimeOffset? Modified)>();

        foreach (var f in allFiles)
        {
            var storageName = System.IO.Path.GetFileName(f.StoragePath);
            if (string.IsNullOrEmpty(storageName))
                storageName = f.Id.ToString();

            var displayName = $"{storageName}  (id: {f.Id})";
            var href = $"/raw/{dataSourceId}/{f.Id}";
            entries.Add((displayName, href, f.OriginalFileSize, f.CreatedAt));
        }

        entries = entries.OrderBy(e => e.Name).ToList();

        var displayPath = $"/{ds!.Name}/ (raw)";
        return Content(RenderPage($"{displayPath} - Raw Storage",
            $"/raw/{dataSourceId}/", entries, "/raw/"), "text/html");
    }

    private async Task<Guid?> ResolveUserId()
    {
        if (User.Identity?.IsAuthenticated == true)
            return User.GetUserId();

        return await TryBasicAuthUserId();
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

        if (tickets.Count == 0) return null;

        var ticket = tickets.First();
        var owner = await userManager.FindByIdAsync(ticket.UserId.ToString());
        if (owner is null || !owner.IsActive) return null;

        return ticket.UserId;
    }

    private async Task<(DataSource? ds, IActionResult? error)> AuthorizeDataSource(Guid dataSourceId)
    {
        var dataSources = (await DataSourceDal.GetAll(
            filterExprs: [d => d.Id == dataSourceId],
            project: d => d,
            maxResults: 1)).ToList();

        if (dataSources.Count == 0) return (null, NotFound());

        var ds = dataSources.First();
        var httpFrontend = ds.GetFrontend(FrontendType.Http);
        if (httpFrontend is null) return (null, NotFound());

        if (httpFrontend.AllowAnonymous) return (ds, null);

        if (User.Identity?.IsAuthenticated == true)
        {
            var currentUserId = User.GetUserId();
            if (currentUserId != ds.UserId) return (null, Forbid());
            return (ds, null);
        }

        // HTTP Basic auth
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
                var owner = await userManager.FindByIdAsync(tickets.First().UserId.ToString());
                if (owner is not null && owner.IsActive) return (ds, null);
            }
        }

        Response.Headers["WWW-Authenticate"] = $"Basic realm=\"{ds.Name}\"";
        return (null, Unauthorized());
    }

    private static string RenderPage(string title, string currentPath,
        IEnumerable<(string Name, string Href, long? Size, DateTimeOffset? Modified)> entries,
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
        sb.AppendLine(":root { --bg: #1a1a2e; --bg-hover: #16213e; --text: #e0e0e0; --heading: #ff6b6b; --link: #ff6b6b; --muted: #888; --dim: #666; --border: #333; }");
        sb.AppendLine("[data-theme='light'] { --bg: #f5f5f5; --bg-hover: #e8e8e8; --text: #1a1a1a; --heading: #cc3333; --link: #cc3333; --muted: #666; --dim: #999; --border: #ddd; }");
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
        sb.AppendLine(".badge { background: var(--heading); color: #fff; padding: 2px 8px; border-radius: 4px; font-size: 0.75em; margin-left: 8px; }");
        sb.AppendLine(".theme-btn { background: none; border: 1px solid var(--border); color: var(--text); cursor: pointer; padding: 4px 10px; border-radius: 4px; font-family: inherit; font-size: 0.85em; }");
        sb.AppendLine(".theme-btn:hover { background: var(--bg-hover); }");
        sb.AppendLine("</style>");
        sb.AppendLine("</head><body>");
        sb.AppendLine("<div class=\"header\">");
        sb.AppendLine($"<h1>Index of {HttpUtility.HtmlEncode(currentPath)} <span class=\"badge\">RAW</span></h1>");
        sb.AppendLine("<button class=\"theme-btn\" onclick=\"toggleTheme()\" id=\"themeBtn\">☀️ Light</button>");
        sb.AppendLine("</div>");
        sb.AppendLine("<p style=\"color: var(--muted); font-size: 0.85em; margin-bottom: 1em;\">Showing raw storage files. Downloads are served as-is without decryption.</p>");
        sb.AppendLine("<table>");
        sb.AppendLine("<tr><th>Storage Name</th><th class=\"size\">Size</th><th class=\"date\">Created</th></tr>");

        if (parentHref is not null)
            sb.AppendLine($"<tr><td><a href=\"{parentHref}\">../</a></td><td class=\"size\">-</td><td class=\"date\">-</td></tr>");

        foreach (var (name, href, size, modified) in entries)
        {
            var isDir = href.EndsWith('/');
            var displayName = isDir ? $"{HttpUtility.HtmlEncode(name)}/" : HttpUtility.HtmlEncode(name);
            var sizeStr = size.HasValue ? FormatSize(size.Value) : "-";
            var dateStr = modified.HasValue ? modified.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm") : "-";
            sb.AppendLine($"<tr><td><a href=\"{href}\">{displayName}</a></td><td class=\"size\">{sizeStr}</td><td class=\"date\">{dateStr}</td></tr>");
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

    private static bool TryParseFileId(string path, out Guid fileId)
    {
        fileId = Guid.Empty;
        var trimmed = path.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        var segment = lastSlash < 0 ? trimmed : trimmed[(lastSlash + 1)..];
        return Guid.TryParse(segment, out fileId);
    }
}
