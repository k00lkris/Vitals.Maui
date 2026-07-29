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

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== API AUTH ERROR: {ex.Message}");
            return false;
        }
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
            return IsAuthenticated;
        }
        catch
        {
            return false;
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
}