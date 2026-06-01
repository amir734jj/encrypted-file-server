using System.Net.Http.Headers;

namespace UI.Services;

public sealed class BearerTokenHandler(AuthService auth) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        if (auth.Token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        }

        return base.SendAsync(request, ct);
    }
}
