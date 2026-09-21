using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace UI.Services;

public sealed class BearerTokenHandler(AuthService auth, NavigationManager nav) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        if (auth.Token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        }

        if (request.RequestUri?.AbsolutePath.EndsWith(
                "/api/files/upload",
                StringComparison.OrdinalIgnoreCase) == true)
        {
            // BrowserHttpHandler otherwise buffers StreamContent before starting fetch.
            request.SetBrowserRequestStreamingEnabled(true);
        }

        var response = await base.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized && auth.IsAuthenticated)
        {
            await auth.ClearTokenAsync();
            nav.NavigateTo("/login", forceLoad: true);
        }

        return response;
    }
}
