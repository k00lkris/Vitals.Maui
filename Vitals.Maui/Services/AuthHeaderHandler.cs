namespace Vitals.Maui.Services;

public class AuthHeaderHandler : DelegatingHandler
{
    private readonly AuthService _auth;

    public AuthHeaderHandler(AuthService auth)
    {
        _auth = auth;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Remove("Authorization");
        request.Headers.Remove("X-API-KEY");

        var jwt = _auth.GetAuthHeader();
        if (jwt != null)
            request.Headers.TryAddWithoutValidation("Authorization", jwt);

        // Always send API key for endpoint-level check_key() compatibility
        request.Headers.TryAddWithoutValidation("X-API-KEY", AppConfig.ApiKey);

        return await base.SendAsync(request, cancellationToken);
    }
}