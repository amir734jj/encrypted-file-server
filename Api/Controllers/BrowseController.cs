using System.Text;
using Api.Data.Entities;
using Api.Extensions;
using Api.Interfaces;
using Api.Services;
using Api.ViewModels;
using EfCoreRepository.Interfaces;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Shared.Interfaces;
using Shared.Models;

namespace Api.Controllers;

[Route("browse")]
public sealed class BrowseController(
    IEfRepository repository,
    IFileStorageService fileStorage,
    IEncryptionProviderFactory encryptionFactory,
    ITemplateService templateService,
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

        var html = await templateService.RenderDirectoryListingAsync(new DirectoryListingViewModel
        {
            Title = "/ - File Server",
            CurrentPath = "/browse",
            Entries = dataSources.Select(ds => new EntryViewModel
            {
                Name = ds.Name,
                Href = $"/browse/{ds.Id}/",
                Size = null,
                Modified = ds.CreatedAt
            }).ToList()
        });
        return Content(html, "text/html");
    }

    [HttpGet("{dataSourceId:guid}/{**path}")]
    public async Task<IActionResult> ListOrDownload(Guid dataSourceId, string? path = null, [FromQuery] bool raw = false)
    {
        var (ds, masterKey, error) = await AuthorizeDataSource(dataSourceId);
        if (error is not null) return error;

        var defaultEncryption = encryptionFactory.GetProvider(ds!.Backend.EncryptionMethod);
        var defaultMethod = ds.Backend.EncryptionMethod;
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
                var fileMethod = file.EncryptionMethod ?? defaultMethod;
                var isFileEncrypted = fileMethod != EncryptionMethod.None;

                if (raw && isFileEncrypted)
                {
                    var rawStream = await fileStorage.OpenRawStreamAsync(file);
                    var rawFileName = System.IO.Path.GetFileName(file.StoragePath);
                    return File(rawStream, "application/octet-stream", rawFileName);
                }

                var fileEncryption = encryptionFactory.GetProvider(fileMethod);
                var iv = Convert.FromBase64String(file.IvBase64);
                var fullPath = fileEncryption.DecryptString(file.OriginalFileName, masterKey!, iv);
                var fileName = System.IO.Path.GetFileName(fullPath);
                var contentType = file.ContentType is not null
                    ? fileEncryption.DecryptString(file.ContentType, masterKey!, iv)
                    : "application/octet-stream";
                var stream = await fileStorage.OpenDecryptedStreamAsync(file);
                Response.Headers.ContentDisposition = $"inline; filename=\"{fileName}\"";
                return File(stream, contentType);
            }
        }

        // Directory listing
        var allFiles = (await FileDal.GetAll(
            filterExprs: [f => f.DataSourceId == dataSourceId && f.UserId == ds!.UserId],
            project: f => f)).ToList();

        var entries = new List<EntryViewModel>();
        var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in allFiles)
        {
            var fileEncryption = encryptionFactory.GetProvider(f.EncryptionMethod ?? defaultMethod);
            var iv = Convert.FromBase64String(f.IvBase64);
            var fullPath = fileEncryption.DecryptString(f.OriginalFileName, masterKey!, iv);

            if (!fullPath.StartsWith(path, StringComparison.OrdinalIgnoreCase))
                continue;

            var relativePath = fullPath[path.Length..];
            var slashIndex = relativePath.IndexOf('/');

            if (slashIndex < 0)
            {
                var isFileEncrypted = (f.EncryptionMethod ?? defaultMethod) != EncryptionMethod.None;
                var href = $"/browse/{dataSourceId}/{path}{f.Id}";
                entries.Add(new EntryViewModel
                {
                    Name = relativePath,
                    Href = href,
                    RawHref = isFileEncrypted ? $"{href}?raw=true" : null,
                    Size = f.OriginalFileSize,
                    Modified = f.CreatedAt
                });
            }
            else
            {
                var folderName = relativePath[..slashIndex];
                if (seenFolders.Add(folderName))
                    entries.Add(new EntryViewModel
                    {
                        Name = folderName,
                        Href = $"/browse/{dataSourceId}/{path}{folderName}/"
                    });
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

        var html = await templateService.RenderDirectoryListingAsync(new DirectoryListingViewModel
        {
            Title = $"{displayPath} - File Server",
            CurrentPath = $"/browse/{dataSourceId}/{path}",
            Entries = entries,
            ParentHref = parentHref
        });
        return Content(html, "text/html");
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
