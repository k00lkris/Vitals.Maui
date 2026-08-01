using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;

namespace Vitals.Maui.Services;

public class AuthService
{
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _jsonOptions;

    private string? _jwt;
    private string? _userId;
    private string? _householdId;
    private string? _email;
    private string? _displayName;
    private bool _isNewUser;

    public bool IsAuthenticated => !string.IsNullOrEmpty(_jwt);
    public string? UserId => _userId;
    public string? HouseholdId => _householdId;
    public string? Email => _email;
    public string? DisplayName => _displayName;

    // Only meaningful immediately after a successful SignInWithGoogleAsync()
    // call — reflects what /api/auth/google's is_new_user said about THIS
    // sign-in, not a persisted session flag. Read it right after Sign In or
    // Sign Up completes, and route accordingly (see AppNavigation.RouteAfterGoogleAuth).
    public bool IsNewUser => _isNewUser;



    public AuthService()  // <-- no HttpClient parameter
    {
        // Auth service gets its own plain HttpClient — no auth headers needed
        // since this is the service that PROVIDES auth
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    }

    // -------------------------------------------------------
    // Google Sign-In via WebAuthenticator
    // -------------------------------------------------------
    public async Task<bool> SignInWithGoogleAsync()
    {
        // Remove the outer try/catch so exceptions bubble up
        var state = Guid.NewGuid().ToString("N");
        var nonce = Guid.NewGuid().ToString("N");
        var clientId = AppConfig.GoogleClientId;
        var redirect = Uri.EscapeDataString(AppConfig.OAuthRedirectUri);
        var scope = Uri.EscapeDataString("openid email profile");

        var authUrl = $"https://accounts.google.com/o/oauth2/v2/auth" +
                      $"?client_id={clientId}" +
                      $"&redirect_uri={redirect}" +
                      $"&response_type=code" +
                      $"&scope={scope}" +
                      $"&state={state}" +
                      $"&nonce={nonce}" +
                      $"&access_type=offline";
        System.Diagnostics.Debug.WriteLine($"=== AUTH URL: {authUrl}");
        System.Diagnostics.Debug.WriteLine($"=== REDIRECT URI: {AppConfig.OAuthRedirectUri}");
        var result = await WebAuthenticator.Default.AuthenticateAsync(
            new Uri(authUrl),
            new Uri(AppConfig.OAuthRedirectUri));

        if (result == null)
        {
            System.Diagnostics.Debug.WriteLine("=== GOOGLE AUTH: AuthenticateAsync returned null (no result at all — likely cancelled or the redirect never reached the app)");
            return false;
        }

        result.Properties.TryGetValue("code", out var code);
        if (string.IsNullOrEmpty(code))
        {
            // Dump everything the redirect actually contained. If Google sent
            // back an error instead of a code (e.g. access_denied, or a
            // Workspace admin-policy rejection), it'll be in here under a
            // different key ("error", "error_description", etc.) — this is
            // the only way to see what actually happened instead of just
            // getting a silent "Sign in failed".
            var allProps = string.Join(", ", result.Properties.Select(kv => $"{kv.Key}={kv.Value}"));
            System.Diagnostics.Debug.WriteLine($"=== GOOGLE AUTH: no 'code' in redirect. Full properties: [{allProps}]");
            return false;
        }

        var idToken = await ExchangeCodeForIdTokenAsync(code);
        if (string.IsNullOrEmpty(idToken)) return false;

        return await ExchangeTokenWithApiAsync(idToken);
    }

    // -------------------------------------------------------
    // Exchange OAuth code for Google ID token
    // -------------------------------------------------------
    private async Task<string?> ExchangeCodeForIdTokenAsync(string code)
    {
        try
        {
            using var tokenClient = new HttpClient(); // no base address for external call
            var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"] = code,
                ["client_id"] = AppConfig.GoogleClientId,
                ["redirect_uri"] = AppConfig.OAuthRedirectUri,
                ["grant_type"] = "authorization_code"
            });

            var response = await tokenClient.PostAsync(
                "https://oauth2.googleapis.com/token", body);
            var raw = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine($"=== TOKEN EXCHANGE: {raw}");

            if (!response.IsSuccessStatusCode) return null;

            var json = JsonSerializer.Deserialize<JsonElement>(raw);
            return json.GetProperty("id_token").GetString();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== CODE EXCHANGE ERROR: {ex.Message}");
            return null;
        }
    }

    // -------------------------------------------------------
    // Send ID token to our API, get back our JWT
    // -------------------------------------------------------
    private async Task<bool> ExchangeTokenWithApiAsync(string idToken)
    {
        try
        {
            var payload = JsonSerializer.Serialize(
                new { id_token = idToken }, _jsonOptions);
            var content = new StringContent(payload, Encoding.UTF8, "application/json");

            var response = await _http.PostAsync($"{AppConfig.BaseUrl}/api/auth/google", content);
            var raw = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine($"=== API AUTH RESPONSE: {raw}");

            if (!response.IsSuccessStatusCode) return false;

            var authResult = JsonSerializer.Deserialize<AuthResult>(raw, _jsonOptions);
            if (authResult == null) return false;

            await ApplySuccessfulAuthAsync(authResult);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== API AUTH ERROR: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Sets in-memory session fields and persists them to SecureStorage.
    /// Shared by Google sign-in and email/password login, since both end
    /// with the same shape of response from the backend.
    /// </summary>
    private async Task ApplySuccessfulAuthAsync(AuthResult authResult)
    {
        _jwt = authResult.Token;
        _userId = authResult.UserId;
        _householdId = authResult.HouseholdId;
        _email = authResult.Email;
        _displayName = authResult.DisplayName;
        _isNewUser = authResult.IsNewUser;

        await SecureStorage.SetAsync("auth_jwt", _jwt);
        await SecureStorage.SetAsync("auth_user_id", _userId);
        await SecureStorage.SetAsync("auth_household_id", _householdId);
        await SecureStorage.SetAsync("auth_email", _email ?? "");
        await SecureStorage.SetAsync("auth_display_name", _displayName ?? "");
    }

    // -------------------------------------------------------
    // Email/password auth
    // -------------------------------------------------------

    /// <summary>
    /// Registers a new account. On success, the account is NOT signed in —
    /// /api/auth/register deliberately doesn't return a token, since the
    /// email must be verified first. Returns a friendly error message
    /// (parsed from the server's "detail" field) on failure, e.g. "An
    /// account with this email already exists."
    /// </summary>
    public async Task<EmailAuthResult> RegisterAsync(string email, string password, string displayName)
    {
        try
        {
            var payload = JsonSerializer.Serialize(
                new { email, password, display_name = displayName }, _jsonOptions);
            var content = new StringContent(payload, Encoding.UTF8, "application/json");

            var response = await _http.PostAsync($"{AppConfig.BaseUrl}/api/auth/register", content);
            var raw = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine($"=== REGISTER RESPONSE: {response.StatusCode} {raw}");

            if (!response.IsSuccessStatusCode)
            {
                return EmailAuthResult.Failed(ExtractErrorDetail(raw));
            }

            return EmailAuthResult.Ok();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== REGISTER ERROR: {ex.Message}");
            return EmailAuthResult.Failed("Couldn't reach the server. Check your connection and try again.");
        }
    }

    /// <summary>
    /// Signs in with email/password. On success, behaves exactly like a
    /// successful Google sign-in (session set, IsNewUser available —
    /// always false here, since login is only for existing accounts).
    /// On failure, surfaces the server's specific reason (wrong password,
    /// registered with Google instead, not yet verified, etc.) rather than
    /// a generic error.
    /// </summary>
    public async Task<EmailAuthResult> LoginWithEmailAsync(string email, string password)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new { email, password }, _jsonOptions);
            var content = new StringContent(payload, Encoding.UTF8, "application/json");

            var response = await _http.PostAsync($"{AppConfig.BaseUrl}/api/auth/login", content);
            var raw = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine($"=== LOGIN RESPONSE: {response.StatusCode} {raw}");

            if (!response.IsSuccessStatusCode)
            {
                return EmailAuthResult.Failed(ExtractErrorDetail(raw));
            }

            var authResult = JsonSerializer.Deserialize<AuthResult>(raw, _jsonOptions);
            if (authResult == null)
            {
                return EmailAuthResult.Failed("Something went wrong. Please try again.");
            }

            await ApplySuccessfulAuthAsync(authResult);
            return EmailAuthResult.Ok();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== LOGIN ERROR: {ex.Message}");
            return EmailAuthResult.Failed("Couldn't reach the server. Check your connection and try again.");
        }
    }

    /// <summary>
    /// Requests a fresh verification email for an account stuck unverified
    /// (expired link, never received it, etc.). Always returns the same
    /// generic confirmation regardless of outcome — matches the server's
    /// deliberate non-enumeration behavior, so this never reveals whether
    /// an email is registered.
    /// </summary>
    public async Task ResendVerificationAsync(string email)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new { email }, _jsonOptions);
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync($"{AppConfig.BaseUrl}/api/auth/resend-verification", content);
            System.Diagnostics.Debug.WriteLine($"=== RESEND VERIFICATION STATUS: {response.StatusCode}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== RESEND VERIFICATION ERROR: {ex.Message}");
        }
    }

    /// <summary>
    /// Pulls a friendly message out of a FastAPI error body, which is
    /// always shaped {"detail": "..."} for the errors this app raises
    /// intentionally (HTTPException). Falls back to a generic message if
    /// the body doesn't parse or isn't in that shape (e.g. a raw 500).
    /// </summary>
    private static string ExtractErrorDetail(string rawResponseBody)
    {
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(rawResponseBody);
            if (json.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
            {
                return detail.GetString() ?? "Something went wrong. Please try again.";
            }
        }
        catch { /* fall through to generic message below */ }

        return "Something went wrong. Please try again.";
    }

    // -------------------------------------------------------
    // Restore session on app launch
    // -------------------------------------------------------
    public async Task<bool> TryRestoreSessionAsync()
    {
        try
        {
            _jwt = await SecureStorage.GetAsync("auth_jwt");
            _userId = await SecureStorage.GetAsync("auth_user_id");
            _householdId = await SecureStorage.GetAsync("auth_household_id");
            _email = await SecureStorage.GetAsync("auth_email");
            _displayName = await SecureStorage.GetAsync("auth_display_name");

            if (!IsAuthenticated) return false;

            // A present, unexpired JWT alone doesn't mean the account still
            // exists — the user/household could have been deleted
            // server-side after the token was issued, and the token itself
            // has no way to reflect that until it naturally expires (up to
            // 7 days). Confirm with the server rather than trusting the
            // local cache blindly.
            var verified = await VerifySessionAsync();
            if (!verified)
            {
                SignOut();
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Calls /api/auth/verify to confirm the JWT's user still has a real
    /// row in the database. Uses a manual Authorization header since
    /// AuthService's HttpClient deliberately has no auth-header injection
    /// (see constructor) — that's ApiService's HttpClient's job, but this
    /// is the one call AuthService itself needs to make with a token.
    /// </summary>
    private async Task<bool> VerifySessionAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{AppConfig.BaseUrl}/api/auth/verify");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _jwt);

            var response = await _http.SendAsync(request);
            System.Diagnostics.Debug.WriteLine($"=== VERIFY SESSION STATUS: {response.StatusCode}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            // Network failure shouldn't force a sign-out — that would kick
            // the user back to Login every time they're briefly offline at
            // launch. Only an explicit 401 from the server (account/session
            // genuinely gone) should invalidate the local session.
            System.Diagnostics.Debug.WriteLine($"=== VERIFY SESSION ERROR (treating as offline, not invalid): {ex.Message}");
            return true;
        }
    }

    // -------------------------------------------------------
    // Sign out
    // -------------------------------------------------------
    public void SignOut()
    {
        _jwt = _userId = _householdId = _email = _displayName = null;
        SecureStorage.Remove("auth_jwt");
        SecureStorage.Remove("auth_user_id");
        SecureStorage.Remove("auth_household_id");
        SecureStorage.Remove("auth_email");
        SecureStorage.Remove("auth_display_name");
    }

    public string? GetAuthHeader() =>
        IsAuthenticated ? $"Bearer {_jwt}" : null;

    private class AuthResult
    {
        public string Token { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public string HouseholdId { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;

        [JsonPropertyName("is_new_user")]
        public bool IsNewUser { get; set; }
    }

    public class EmailAuthResult
    {
        public bool Success { get; private set; }
        public string? ErrorMessage { get; private set; }

        public static EmailAuthResult Ok() => new() { Success = true };
        public static EmailAuthResult Failed(string message) => new() { Success = false, ErrorMessage = message };
    }
}