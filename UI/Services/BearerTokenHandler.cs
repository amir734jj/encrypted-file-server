using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Components;

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

        var response = await base.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized && auth.IsAuthenticated)
        {
            await auth.ClearTokenAsync();
            nav.NavigateTo("/login", forceLoad: true);
        }

        return response;
    }
}
