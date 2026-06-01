using System.Collections.Concurrent;
using Api.Data.Entities;
using EfCoreRepository.Interfaces;
using FxSsh;
using FxSsh.Services;
using Microsoft.AspNetCore.Identity;
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
    private readonly ConcurrentDictionary<Guid, SftpSubsystem> _activeSubsystems = new();

    public EncryptedSftpServer(IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<EncryptedSftpServer> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        var port = config.GetValue("Sftp:Port", 2222);
        var hostKey = LoadOrGenerateHostKey();

        _server = new SshServer(new StartingInfo(System.Net.IPAddress.IPv6Any, port, "SSH-2.0-EncryptedFileServer"));
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

    public void Dispose()
    {
        _server.Stop();
        foreach (var sub in _activeSubsystems.Values)
            sub.Dispose();
        _activeSubsystems.Clear();
    }

    private void OnConnectionAccepted(object? sender, Session session)
    {
        _logger.LogDebug("SSH connection accepted");

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
                    {
                        _logger.LogInformation("SFTP auth success: {User} (anonymous={Anon})", e.Username, isAnonymous);
                    }
                    else
                    {
                        _logger.LogWarning("SFTP auth failed: {User}", e.Username);
                    }
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
                        var subsystemId = Guid.NewGuid();
                        var subsystem = new SftpSubsystem(e.Channel, scope, authenticatedUserId, _logger,
                            () => { _activeSubsystems.TryRemove(subsystemId, out SftpSubsystem? _); });
                        _activeSubsystems[subsystemId] = subsystem;
                        subsystem.Start();
                    }
                };
            }
        };
    }

    private (bool accepted, Guid? userId) ValidateCredentials(string username, string password)
    {
        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IEfRepository>();

        if (string.Equals(username, "anonymous", StringComparison.OrdinalIgnoreCase))
        {
            var hasAnon = repository.For<DataSource>()
                .Any(filterExprs: [d => d.Frontends.Any(f => f.Type == FrontendType.Sftp && f.AllowAnonymous)])
                .GetAwaiter().GetResult();
            return (hasAnon, null);
        }

        var tickets = repository.For<AccessTicket>()
            .GetAll(
                filterExprs: [t => t.Username == username && t.Password == password && t.ExpiresAt > DateTimeOffset.UtcNow],
                project: t => t,
                maxResults: 1)
            .GetAwaiter().GetResult().ToList();

        if (tickets.Count == 0)
        {
            return (false, null);
        }

        var ticket = tickets.First();

        // Reject if the ticket owner's account is disabled
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var ticketOwner = userManager.FindByIdAsync(ticket.UserId.ToString()).GetAwaiter().GetResult();
        if (ticketOwner is null || !ticketOwner.IsActive)
        {
            return (false, null);
        }

        return (true, ticket.UserId);
    }

    private static string LoadOrGenerateHostKey()
    {
        var keyDir = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(keyDir);
        var keyPath = Path.Combine(keyDir, "sftp_host_key.pem");

        if (File.Exists(keyPath))
        {
            return File.ReadAllText(keyPath);
        }

        var pem = KeyGenerator.GenerateRsaKeyPem(4096);
        File.WriteAllText(keyPath, pem);
        return pem;
    }
}
