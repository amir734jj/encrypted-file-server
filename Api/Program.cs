using System.Reflection;
using System.Text;
using System.Threading.RateLimiting;
using Api.Data;
using Api.Data.Entities;
using EfCoreRepository.Extensions;
using Api.Extensions;
using Api.Ftp;
using Api.Middleware;
using Api.Services;
using Api.Services.Backend;
using Api.Services.Encryption;
using Api.Services.Frontend;
using Api.Utilities;
using FubarDev.FtpServer;
using FubarDev.FtpServer.AccountManagement;
using FubarDev.FtpServer.FileSystem;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Serilog;
using Serilog.Events;
using Shared.Interfaces;

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("System", LogEventLevel.Error)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .Enrich.WithProperty("Application", "encrypted-file-server")
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        path: "logs/api-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();
builder.Host.UseSerilog();

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    // Only configure port manually if ASPNETCORE_URLS is not set (e.g. by Coolify)
    if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
    {
        var portConfig = builder.Configuration.GetValue<string>("PORT");
        var port = !string.IsNullOrEmpty(portConfig) && int.TryParse(portConfig, out var p) ? p : 5000;
        serverOptions.ListenAnyIP(port);
    }

    serverOptions.Limits.MaxRequestBodySize = null; // per-endpoint limits via [RequestSizeLimit]
});

var connectionString = ConnectionStringUtility.ConnectionStringUrlToPgResource(
    builder.Configuration.GetValue<string>("DATABASE_URL")
    ?? builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("DATABASE_URL or ConnectionStrings:DefaultConnection is required."));

builder.Services.AddDbContext<AppDbContext>(opt => opt.UseNpgsql(connectionString));

builder.Services.AddEfRepository<AppDbContext>(x =>
{
    x.Profile(Assembly.GetAssembly(typeof(AppDbContext)));
});

builder.Services
    .AddIdentity<User, Role>(opt =>
    {
        opt.Password.RequiredLength = 8;
        opt.Password.RequireNonAlphanumeric = false;
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

builder.Services
    .AddAuthentication(opt =>
    {
        opt.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        opt.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(opt =>
    {
        opt.MapInboundClaims = false;
        opt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!))
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(opt =>
{
    opt.AddFixedWindowLimiter("login", w =>
    {
        w.Window = TimeSpan.FromMinutes(1);
        w.PermitLimit = 10;
        w.QueueLimit = 0;
        w.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });
    opt.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

builder.Services.AddMemoryCache();
builder.Services.AddHealthChecks();

builder.Services.AddControllers().AddNewtonsoftJson();

builder.Services.Scan(scan => scan
    .FromAssemblies(Assembly.Load("Api"))
    .AddClasses()
    .UsingRegistrationStrategy(Scrutor.RegistrationStrategy.Skip)
    .AsMatchingInterface()
    .WithScopedLifetime());

builder.Services.AddSingleton<IEncryptionProvider, AesCtrEncryptionProvider>();
builder.Services.AddSingleton<IEncryptionProvider, AesGcmEncryptionProvider>();
builder.Services.AddSingleton<IEncryptionProvider, ChaCha20EncryptionProvider>();
builder.Services.AddSingleton<IEncryptionProvider, NoneEncryptionProvider>();
builder.Services.AddSingleton<Api.Interfaces.IEncryptionProviderFactory, EncryptionProviderFactory>();
builder.Services.AddSingleton<IBackendStorageProvider, FtpBackendStorageProvider>();
builder.Services.AddSingleton<IFrontendDataSource, HttpFrontendDataSource>();
builder.Services.AddSingleton<IFrontendDataSource, FtpFrontendDataSource>();
builder.Services.AddSingleton<Api.Sftp.EncryptedSftpServer>();
builder.Services.AddSingleton<IFrontendDataSource, SftpFrontendDataSource>();

builder.Services.AddHostedService<Api.Services.TicketCleanupService>();

builder.Services.AddFtpServer(opt => { });
builder.Services.Configure<FubarDev.FtpServer.FtpServerOptions>(opt =>
{
    opt.ServerAddress = "0.0.0.0";
    opt.Port = builder.Configuration.GetValue("Ftp:Port", 2121);
});
builder.Services.AddSingleton<IFileSystemClassFactory, EncryptedFileSystemProvider>();
builder.Services.AddSingleton<IMembershipProvider, EncryptedMembershipProvider>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(opt =>
{
    opt.SwaggerDoc("v1", new OpenApiInfo { Title = "Encrypted File Server API", Version = "v1" });
    opt.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header
    });
    opt.AddSecurityRequirement(_ => new OpenApiSecurityRequirement
    {
        { new OpenApiSecuritySchemeReference("Bearer"), [] }
    });
});

builder.Services.AddCors(opt =>
    opt.AddDefaultPolicy(p => p
        .WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:5001"])
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
}

using (var configScope = app.Services.CreateScope())
{
    await configScope.ServiceProvider.GetRequiredService<Api.Interfaces.IGlobalConfigService>().InitAsync();
}

var frontends = app.Services.GetServices<IFrontendDataSource>();
foreach (var frontend in frontends)
{
    await frontend.StartAsync(CancellationToken.None);
    Log.Information("Frontend data source started: {Source}", frontend.DisplayName);
}

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Encrypted File Server API v1"));
}

app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<ActiveUserMiddleware>();
app.UseAuthorization();
app.UseSerilogRequestLogging(opts =>
{
    opts.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
    {
        var user = httpContext.User.Identity is { IsAuthenticated: true }
            ? httpContext.User.TryGetEmail() ?? "authenticated"
            : "anonymous";
        diagnosticContext.Set("user", user);
    };
});

app.MapControllers();
app.MapHealthChecks("/api/health").AllowAnonymous();

app.MapFallback("api/{**rest}", async context =>
{
    context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
    await context.Response.WriteAsync(
        $"Failed to find the endpoint for {context.Request.Method}:{context.Request.GetDisplayUrl()}");
});

if (app.Environment.IsDevelopment())
    app.MapFallback(() => Results.Text("Encrypted File Server API is running."));
else
    app.MapFallbackToFile("index.html");

await app.RunAsync();

foreach (var frontend in app.Services.GetServices<IFrontendDataSource>())
{
    await frontend.StopAsync(CancellationToken.None);
}
