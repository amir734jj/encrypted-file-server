using System.Security.Claims;
using System.Text;
using System.Xml.Linq;
using Api.Data.Entities;
using Api.Extensions;
using Api.Interfaces;
using EfCoreRepository.Interfaces;
using Microsoft.AspNetCore.Identity;
using Shared.Interfaces;
using Shared.Models;

namespace Api.WebDav;

/// <summary>
/// Lightweight WebDAV middleware that exposes encrypted files via standard WebDAV verbs.
/// Mounted at /webdav/{dataSourceId}/. Supports PROPFIND, GET, PUT, DELETE, MKCOL, OPTIONS.
/// Authentication: JWT bearer, Basic auth (per-DataSource password), or anonymous.
/// </summary>
public sealed class WebDavMiddleware(RequestDelegate next)
{
    private const string BasePath = "/webdav/";

    private static readonly XNamespace DavNs = "DAV:";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/webdav", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        using var scope = context.RequestServices.CreateScope();
        var handler = new WebDavHandler(
            context,
            scope.ServiceProvider.GetRequiredService<IEfRepository>(),
            scope.ServiceProvider.GetRequiredService<IFileStorageService>(),
            scope.ServiceProvider.GetRequiredService<IEncryptionProviderFactory>(),
            scope.ServiceProvider.GetRequiredService<UserManager<User>>());

        await handler.HandleAsync();
    }

    private sealed class WebDavHandler(
        HttpContext context,
        IEfRepository repository,
        IFileStorageService fileStorage,
        IEncryptionProviderFactory encryptionFactory,
        UserManager<User> users)
    {
        private IBasicCrud<DataSource> DataSourceDal => repository.For<DataSource>();
        private IBasicCrud<EncryptedFile> FileDal => repository.For<EncryptedFile>();

        public async Task HandleAsync()
        {
            var method = context.Request.Method.ToUpperInvariant();

            // Parse path: /webdav/ or /webdav/{dsId}/ or /webdav/{dsId}/path...
            var relativePath = context.Request.Path.Value?[BasePath.Length..] ?? "";
            var slashIndex = relativePath.IndexOf('/');

            // Root listing: /webdav/
            if (string.IsNullOrEmpty(relativePath) || relativePath == "/")
            {
                if (method == "PROPFIND")
                    await HandleRootPropfind();
                else if (method == "OPTIONS")
                    HandleOptions();
                else
                    context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
            }

            // Parse data source ID
            var dsIdStr = slashIndex >= 0 ? relativePath[..slashIndex] : relativePath;
            if (!Guid.TryParse(dsIdStr, out var dataSourceId))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var subPath = slashIndex >= 0 ? relativePath[(slashIndex + 1)..] : "";
            subPath = NormalizePath(subPath);

            // Authorize
            var (ds, masterKey, statusCode) = await Authorize(dataSourceId);
            if (ds is null)
            {
                context.Response.StatusCode = statusCode;
                if (statusCode == StatusCodes.Status401Unauthorized)
                    context.Response.Headers["WWW-Authenticate"] = $"Basic realm=\"{dataSourceId}\"";
                return;
            }

            switch (method)
            {
                case "OPTIONS":
                    HandleOptions();
                    break;
                case "PROPFIND":
                    await HandlePropfind(ds, masterKey!, subPath);
                    break;
                case "GET":
                case "HEAD":
                    await HandleGet(ds, masterKey!, subPath, method == "HEAD");
                    break;
                case "PUT":
                    await HandlePut(ds, masterKey!, subPath);
                    break;
                case "DELETE":
                    await HandleDelete(ds, masterKey!, subPath);
                    break;
                case "MKCOL":
                    // Directories are virtual — MKCOL always succeeds
                    context.Response.StatusCode = StatusCodes.Status201Created;
                    break;
                case "LOCK":
                    await HandleLock(subPath, ds);
                    break;
                case "UNLOCK":
                    context.Response.StatusCode = StatusCodes.Status204NoContent;
                    break;
                default:
                    context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                    break;
            }
        }

        private void HandleOptions()
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.Headers["DAV"] = "1, 2";
            context.Response.Headers.Allow = "OPTIONS, PROPFIND, GET, HEAD, PUT, DELETE, MKCOL, LOCK, UNLOCK";
        }

        private async Task HandleRootPropfind()
        {
            var userId = GetAuthenticatedUserId();
            if (userId is null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var dataSources = (await DataSourceDal.GetAll(
                filterExprs: [d => d.UserId == userId],
                project: d => d)).Where(d => d.HasFrontend(FrontendType.WebDav)).OrderBy(d => d.Name).ToList();

            var responses = new List<XElement>
            {
                CreateCollectionResponse(BasePath, "/")
            };

            foreach (var ds in dataSources)
            {
                responses.Add(CreateCollectionResponse($"{BasePath}{ds.Id}/", ds.Name));
            }

            await WriteMultiStatus(responses);
        }

        private async Task HandlePropfind(DataSource ds, byte[] masterKey, string path)
        {
            var depthHeader = context.Request.Headers["Depth"].FirstOrDefault() ?? "1";
            var encryption = encryptionFactory.GetProvider(ds.Backend.EncryptionMethod);

            var allFiles = (await FileDal.GetAll(
                filterExprs: [f => f.DataSourceId == ds.Id && f.UserId == ds.UserId],
                project: f => f)).ToList();

            var baseDavPath = $"{BasePath}{ds.Id}/";
            var responses = new List<XElement>();

            // Current directory entry
            responses.Add(CreateCollectionResponse($"{baseDavPath}{path}", string.IsNullOrEmpty(path) ? ds.Name : Path.GetFileName(path.TrimEnd('/'))));

            if (depthHeader != "0")
            {
                var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var f in allFiles)
                {
                    var iv = Convert.FromBase64String(f.IvBase64);
                    var fullPath = encryption.DecryptString(f.OriginalFileName, masterKey, iv);

                    if (!string.IsNullOrEmpty(path) && !fullPath.StartsWith(path, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var relativePath = string.IsNullOrEmpty(path) ? fullPath : fullPath[path.Length..];
                    var slashIdx = relativePath.IndexOf('/');

                    if (slashIdx < 0)
                    {
                        // File entry
                        var contentType = f.ContentType is not null
                            ? encryption.DecryptString(f.ContentType, masterKey, iv)
                            : "application/octet-stream";

                        responses.Add(CreateFileResponse(
                            $"{baseDavPath}{path}{Uri.EscapeDataString(relativePath)}",
                            relativePath,
                            f.OriginalFileSize,
                            contentType,
                            f.CreatedAt,
                            f.UpdatedAt ?? f.CreatedAt));
                    }
                    else
                    {
                        var folderName = relativePath[..slashIdx];
                        if (seenFolders.Add(folderName))
                        {
                            responses.Add(CreateCollectionResponse(
                                $"{baseDavPath}{path}{Uri.EscapeDataString(folderName)}/", folderName));
                        }
                    }
                }
            }

            await WriteMultiStatus(responses);
        }

        private async Task HandleGet(DataSource ds, byte[] masterKey, string path, bool headOnly)
        {
            if (string.IsNullOrEmpty(path))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
            }

            var encryption = encryptionFactory.GetProvider(ds.Backend.EncryptionMethod);
            var allFiles = (await FileDal.GetAll(
                filterExprs: [f => f.DataSourceId == ds.Id && f.UserId == ds.UserId],
                project: f => f)).ToList();

            foreach (var f in allFiles)
            {
                var iv = Convert.FromBase64String(f.IvBase64);
                var fullPath = encryption.DecryptString(f.OriginalFileName, masterKey, iv);

                if (!string.Equals(fullPath, path, StringComparison.OrdinalIgnoreCase))
                    continue;

                var contentType = f.ContentType is not null
                    ? encryption.DecryptString(f.ContentType, masterKey, iv)
                    : "application/octet-stream";

                context.Response.ContentType = contentType;
                context.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{Path.GetFileName(fullPath)}\"";

                if (!headOnly)
                {
                    var stream = await fileStorage.OpenDecryptedStreamAsync(f, masterKey);
                    await using (stream)
                    {
                        await stream.CopyToAsync(context.Response.Body);
                    }
                }

                return;
            }

            context.Response.StatusCode = StatusCodes.Status404NotFound;
        }

        private async Task HandlePut(DataSource ds, byte[] masterKey, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            // Check if file already exists — replace it
            var encryption = encryptionFactory.GetProvider(ds.Backend.EncryptionMethod);
            var allFiles = (await FileDal.GetAll(
                filterExprs: [f => f.DataSourceId == ds.Id && f.UserId == ds.UserId],
                project: f => f)).ToList();

            foreach (var f in allFiles)
            {
                var iv = Convert.FromBase64String(f.IvBase64);
                var fullPath = encryption.DecryptString(f.OriginalFileName, masterKey, iv);

                if (string.Equals(fullPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    await fileStorage.DeleteFileAsync(f);
                    break;
                }
            }

            var contentType = context.Request.ContentType ?? "application/octet-stream";
            await fileStorage.StoreFileAsync(ds.UserId, ds.Id, path, contentType, context.Request.Body);

            context.Response.StatusCode = StatusCodes.Status201Created;
        }

        private async Task HandleDelete(DataSource ds, byte[] masterKey, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            var encryption = encryptionFactory.GetProvider(ds.Backend.EncryptionMethod);
            var allFiles = (await FileDal.GetAll(
                filterExprs: [f => f.DataSourceId == ds.Id && f.UserId == ds.UserId],
                project: f => f)).ToList();

            // Check if it's a file
            foreach (var f in allFiles)
            {
                var iv = Convert.FromBase64String(f.IvBase64);
                var fullPath = encryption.DecryptString(f.OriginalFileName, masterKey, iv);

                if (string.Equals(fullPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    await fileStorage.DeleteFileAsync(f);
                    context.Response.StatusCode = StatusCodes.Status204NoContent;
                    return;
                }
            }

            // Check if it's a directory prefix
            var dirPath = path.EndsWith('/') ? path : path + "/";
            var toDelete = allFiles.Where(f =>
            {
                var iv = Convert.FromBase64String(f.IvBase64);
                var fullPath = encryption.DecryptString(f.OriginalFileName, masterKey, iv);
                return fullPath.StartsWith(dirPath, StringComparison.OrdinalIgnoreCase);
            }).ToList();

            if (toDelete.Count > 0)
            {
                foreach (var f in toDelete)
                    await fileStorage.DeleteFileAsync(f);
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            context.Response.StatusCode = StatusCodes.Status404NotFound;
        }

        private async Task HandleLock(string path, DataSource ds)
        {
            // Minimal lock response to satisfy WebDAV clients
            var lockToken = $"urn:uuid:{Guid.NewGuid()}";
            var href = $"{BasePath}{ds.Id}/{path}";

            var prop = new XElement(DavNs + "prop",
                new XElement(DavNs + "lockdiscovery",
                    new XElement(DavNs + "activelock",
                        new XElement(DavNs + "locktype", new XElement(DavNs + "write")),
                        new XElement(DavNs + "lockscope", new XElement(DavNs + "exclusive")),
                        new XElement(DavNs + "depth", "infinity"),
                        new XElement(DavNs + "timeout", "Second-3600"),
                        new XElement(DavNs + "locktoken",
                            new XElement(DavNs + "href", lockToken)),
                        new XElement(DavNs + "lockroot",
                            new XElement(DavNs + "href", href)))));

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/xml; charset=utf-8";
            context.Response.Headers["Lock-Token"] = $"<{lockToken}>";
            await context.Response.WriteAsync(new XDocument(prop).ToString());
        }

        private async Task<(DataSource? ds, byte[]? masterKey, int statusCode)> Authorize(Guid dataSourceId)
        {
            var dataSources = (await DataSourceDal.GetAll(
                filterExprs: [d => d.Id == dataSourceId],
                project: d => d,
                maxResults: 1)).ToList();

            if (dataSources.Count == 0)
                return (null, null, StatusCodes.Status404NotFound);

            var ds = dataSources.First();
            var webDavFrontend = ds.GetFrontend(FrontendType.WebDav);

            if (webDavFrontend is null)
                return (null, null, StatusCodes.Status404NotFound);

            if (webDavFrontend.AllowAnonymous)
            {
                var user = await users.FindByIdAsync(ds.UserId.ToString());
                if (user is null) return (null, null, StatusCodes.Status404NotFound);
                return (ds, Convert.FromBase64String(user.MasterKeyBase64), 0);
            }

            // JWT auth
            if (context.User.Identity?.IsAuthenticated == true)
            {
                var currentUserId = context.User.GetUserId();
                if (currentUserId != ds.UserId)
                    return (null, null, StatusCodes.Status403Forbidden);

                var user = await users.FindByIdAsync(currentUserId.ToString());
                return (ds, Convert.FromBase64String(user!.MasterKeyBase64), 0);
            }

            // Basic auth
            var authHeader = context.Request.Headers.Authorization.ToString();
            if (authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                var encoded = authHeader["Basic ".Length..].Trim();
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                var password = decoded.Contains(':') ? decoded[(decoded.IndexOf(':') + 1)..] : decoded;

                if (webDavFrontend.Password is not null && password == webDavFrontend.Password)
                {
                    var user = await users.FindByIdAsync(ds.UserId.ToString());
                    if (user is null) return (null, null, StatusCodes.Status404NotFound);
                    return (ds, Convert.FromBase64String(user.MasterKeyBase64), 0);
                }
            }

            return (null, null, StatusCodes.Status401Unauthorized);
        }

        private Guid? GetAuthenticatedUserId()
        {
            if (context.User.Identity?.IsAuthenticated == true)
                return context.User.GetUserId();
            return null;
        }

        private static XElement CreateCollectionResponse(string href, string displayName)
        {
            return new XElement(DavNs + "response",
                new XElement(DavNs + "href", href),
                new XElement(DavNs + "propstat",
                    new XElement(DavNs + "prop",
                        new XElement(DavNs + "displayname", displayName),
                        new XElement(DavNs + "resourcetype", new XElement(DavNs + "collection")),
                        new XElement(DavNs + "getlastmodified", DateTimeOffset.UtcNow.ToString("R"))),
                    new XElement(DavNs + "status", "HTTP/1.1 200 OK")));
        }

        private static XElement CreateFileResponse(string href, string displayName, long size, string contentType,
            DateTimeOffset created, DateTimeOffset modified)
        {
            return new XElement(DavNs + "response",
                new XElement(DavNs + "href", href),
                new XElement(DavNs + "propstat",
                    new XElement(DavNs + "prop",
                        new XElement(DavNs + "displayname", displayName),
                        new XElement(DavNs + "resourcetype"),
                        new XElement(DavNs + "getcontentlength", size),
                        new XElement(DavNs + "getcontenttype", contentType),
                        new XElement(DavNs + "creationdate", created.ToString("o")),
                        new XElement(DavNs + "getlastmodified", modified.ToString("R"))),
                    new XElement(DavNs + "status", "HTTP/1.1 200 OK")));
        }

        private async Task WriteMultiStatus(List<XElement> responses)
        {
            var doc = new XElement(DavNs + "multistatus", responses);
            context.Response.StatusCode = StatusCodes.Status207MultiStatus;
            context.Response.ContentType = "application/xml; charset=utf-8";
            await context.Response.WriteAsync(new XDocument(doc).ToString());
        }

        private static string NormalizePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            path = Uri.UnescapeDataString(path);
            path = path.Replace('\\', '/').TrimStart('/');
            return path;
        }
    }
}
