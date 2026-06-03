using Blazored.LocalStorage;
using Havit.Blazor.Components.Web;
using Havit.Blazor.Components.Web.Bootstrap;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Refit;
using Serilog;
using Serilog.Events;
using Shared.Contracts.Interfaces;
using UI;
using UI.Services;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
    .WriteTo.BrowserConsole(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Logging.AddSerilog(dispose: true);

var apiBaseUrl = builder.Configuration["ApiBaseUrl"];
if (string.IsNullOrEmpty(apiBaseUrl))
{
    apiBaseUrl = builder.HostEnvironment.BaseAddress;
}

builder.Services.AddSingleton<AuthService>();
builder.Services.AddTransient<BearerTokenHandler>();

var refitSettings = new RefitSettings(new NewtonsoftJsonContentSerializer());

foreach (var apiType in new[]
{
    typeof(IAuthApi),
    typeof(IUsersApi),
    typeof(IProfileApi),
    typeof(IDataSourcesApi),
    typeof(IGlobalConfigApi),
    typeof(ITicketsApi),
})
{
    builder.Services
        .AddRefitClient(apiType, refitSettings)
        .ConfigureHttpClient(c =>
        {
            c.BaseAddress = new Uri(apiBaseUrl);
            c.Timeout = TimeSpan.FromSeconds(120);
        })
        .AddHttpMessageHandler<BearerTokenHandler>();
}

// File and remote-import APIs get a longer timeout for large uploads/transfers
foreach (var apiType in new[]
{
    typeof(IFilesApi),
    typeof(IRemoteImportApi)
})
{
    builder.Services
        .AddRefitClient(apiType, refitSettings)
        .ConfigureHttpClient(c =>
        {
            c.BaseAddress = new Uri(apiBaseUrl);
            c.Timeout = TimeSpan.FromMinutes(30);
        })
        .AddHttpMessageHandler<BearerTokenHandler>();
}

builder.Services.AddScoped<ApiService>();
builder.Services.AddBlazoredLocalStorageAsSingleton();

builder.Services.AddHxServices();
builder.Services.AddHxMessenger();
builder.Services.AddHxMessageBoxHost();

var host = builder.Build();

var auth = host.Services.GetRequiredService<AuthService>();
await auth.InitAsync();

if (auth.IsAuthenticated)
{
    try
    {
        var api = host.Services.GetRequiredService<ApiService>();
        var me = await api.GetProfileAsync();
        await auth.SetDisplayNameAsync(me.DisplayName);
    }
    catch { /* token may be expired; ignore */ }
}

await host.RunAsync();
