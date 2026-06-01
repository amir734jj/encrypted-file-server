using Api.Data;
using Api.Data.Entities;
using FxSsh;
using FxSsh.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Shared.Models;

namespace Api.Sftp;

/// <summary>
/// SSH server that hosts the SFTP subsystem. Generates a self-signed RSA host key on first run
/// and persists it to disk. Authenticates users via ASP.NET Identity (password auth)
/// and supports anonymous access for data sources that allow it.
/// </summary>
public sealed class EncryptedSftpServer : IDisposable
{
    private readonly SshServer _server;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EncryptedSftpServer> _logger;

    public EncryptedSftpServer(IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<EncryptedSftpServer> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        var port = config.GetValue("Sftp:Port", 2222);
        var hostKey = LoadOrGenerateHostKey();

        _server = new SshServer(new StartingInfo(System.Net.IPAddress.IPv6Any, port, "EncryptedFileServer"));
        _server.AddHostKey("rsa-sha2-256", hostKey);
        _server.AddHostKey("rsa-sha2-512", hostKey);
        _server.ConnectionAccepted += OnConnectionAccepted;
        _server.ExceptionRasied += (_, ex) => _logger.LogError(ex, "SSH server error");

        _logger.LogInformation("SFTP server configured on port {Port}", port);
    }

    public void Start()
    {
        _server.Start();
        _logger.LogInformation("SFTP server started");
    }

    public void Stop()
    {
        _server.Stop();
        _logger.LogInformation("SFTP server stopped");
    }

    public void Dispose() => _server.Stop();

    private void OnConnectionAccepted(object? sender, Session session)
    {
        _logger.LogDebug("SSH connection accepted: {SessionId}", BitConverter.ToString(session.SessionId));

        Guid? authenticatedUserId = null;
        bool isAnonymous = false;

        session.ServiceRegistered += (_, service) =>
        {
            if (service is UserauthService userauth)
            {
                userauth.Userauth += (_, e) =>
                {
                    var (accepted, userId) = ValidateCredentials(e.Username, e.Password);
                    authenticatedUserId = userId;
                    isAnonymous = accepted && !userId.HasValue;
                    e.Result = accepted;

                    if (accepted)
                        _logger.LogInformation("SFTP auth success: {User} (anonymous={Anon})", e.Username, isAnonymous);
                    else
                        _logger.LogWarning("SFTP auth failed: {User}", e.Username);
                };
            }
            else if (service is ConnectionService conn)
            {
                conn.CommandOpened += (_, e) =>
                {
                    if (e.ShellType == "subsystem" && e.CommandText == "sftp")
                    {
                        _logger.LogDebug("SFTP subsystem requested");
                        var scope = _scopeFactory.CreateScope();
                        var subsystem = new SftpSubsystem(e.Channel, scope, authenticatedUserId, _logger);
                        subsystem.Start();
                    }
                };
            }
        };
    }

    private (bool accepted, Guid? userId) ValidateCredentials(string username, string password)
    {
        using var scope = _scopeFactory.CreateScope();

        if (string.Equals(username, "anonymous", StringComparison.OrdinalIgnoreCase))
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var hasAnon = db.DataSources
                .Include(d => d.Frontends)
                .Any(d => d.Frontends.Any(f => f.Type == FrontendType.Sftp && f.AllowAnonymous));
            return (hasAnon, null);
        }

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = userManager.FindByEmailAsync(username).GetAwaiter().GetResult();
        if (user is null || !user.IsActive)
            return (false, null);

        var valid = userManager.CheckPasswordAsync(user, password).GetAwaiter().GetResult();
        return (valid, valid ? user.Id : (Guid?)null);
    }

    private static string LoadOrGenerateHostKey()
    {
        var keyDir = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(keyDir);
        var keyPath = Path.Combine(keyDir, "sftp_host_key.pem");

        if (File.Exists(keyPath))
            return File.ReadAllText(keyPath);

        var pem = KeyGenerator.GenerateRsaKeyPem(4096);
        File.WriteAllText(keyPath, pem);
        return pem;
    }
}
