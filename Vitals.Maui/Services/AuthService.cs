using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
#if IOS
using AuthenticationServices;
using Foundation;
using UIKit;
using System.Security.Cryptography;
#endif

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
    private string? _authProvider;
    private bool _isNewUser;

    public bool IsAuthenticated => !string.IsNullOrEmpty(_jwt);
    public string? UserId => _userId;
    public string? HouseholdId => _householdId;
    public string? Email => _email;
    public string? DisplayName => _displayName;
    // Raw value from the backend: "password", "google.com", or "apple.com"
    // — SettingsViewModel maps this to a friendly label rather
    // than displaying the raw string directly.
    public string? AuthProvider => _authProvider;

    // Only meaningful immediately after a successful interactive auth call.
    // It reflects what the backend said about THIS sign-in, not a persisted
    // session flag. Read it immediately after auth and route accordingly.
    public bool IsNewUser => _isNewUser;

    public bool IsAppleSignInAvailable =>
#if IOS
        OperatingSystem.IsIOSVersionAtLeast(13);
#else
        false;
#endif



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
    /// Shared by Google, Apple, and email/password auth, since all end
    /// with the same shape of response from the backend.
    /// </summary>
    private async Task ApplySuccessfulAuthAsync(AuthResult authResult)
    {
        _jwt = authResult.Token;
        _userId = authResult.UserId;
        _householdId = authResult.HouseholdId;
        _email = authResult.Email;
        _displayName = authResult.DisplayName;
        _authProvider = authResult.AuthProvider;
        _isNewUser = authResult.IsNewUser;

        await SetSessionValueAsync("auth_jwt", _jwt ?? "");
        await SetSessionValueAsync("auth_user_id", _userId ?? "");
        // New accounts intentionally have no household until onboarding
        // selects a tier or joins an existing household. Persist that state
        // as an empty string and keep the in-memory HouseholdId nullable.
        await SetSessionValueAsync("auth_household_id", _householdId ?? "");
        await SetSessionValueAsync("auth_email", _email ?? "");
        await SetSessionValueAsync("auth_display_name", _displayName ?? "");
        await SetSessionValueAsync("auth_provider", _authProvider ?? "");
    }

    /// <summary>
    /// Updates just the token and household_id — used after
    /// /api/household/select-tier or /api/household/join, both of which
    /// issue a fresh JWT reflecting a household_id that didn't exist (or
    /// was different) when the user first signed in. Everything else about
    /// the session (user_id, email, display_name) is unchanged, so there's
    /// no need for the full AuthResult shape those calls go through
    /// ApiService, not AuthService, since they require an already-valid
    /// session — this just persists what they return.
    /// </summary>
    public async Task UpdateSessionAsync(string newToken, string newHouseholdId)
    {
        _jwt = newToken;
        _householdId = newHouseholdId;
        await SetSessionValueAsync("auth_jwt", _jwt ?? "");
        await SetSessionValueAsync("auth_household_id", _householdId ?? "");
    }

    // -------------------------------------------------------
    // Native Sign in with Apple (iOS)
    // -------------------------------------------------------
    public async Task<AppleAuthResult> SignInWithAppleAsync()
    {
#if IOS
        if (!IsAppleSignInAvailable)
            return AppleAuthResult.Failed("Sign in with Apple is not available on this device.");

        try
        {
            var rawNonce = CreateAppleNonce();
            var hashedNonce = Sha256(rawNonce);
            var state = Guid.NewGuid().ToString("N");

            var provider = new ASAuthorizationAppleIdProvider();
            var request = provider.CreateRequest();
            request.RequestedScopes = new[]
            {
                ASAuthorizationScope.FullName,
                ASAuthorizationScope.Email
            };
            request.Nonce = hashedNonce;
            request.State = state;

            var authDelegate = new AppleAuthorizationDelegate(state);
            using var controller = new ASAuthorizationController(
                new ASAuthorizationRequest[] { request });

            controller.Delegate = authDelegate;
            controller.PresentationContextProvider = authDelegate;
            controller.PerformRequests();

            var nativeResult = await authDelegate.Completion.Task;

            // The native delegate/controller are weakly held on Apple's side;
            // keep them alive until the callback has completed.
            GC.KeepAlive(controller);
            GC.KeepAlive(authDelegate);

            if (nativeResult.Cancelled)
                return AppleAuthResult.CancelledByUser();

            if (!nativeResult.Success ||
                string.IsNullOrWhiteSpace(nativeResult.IdentityToken))
            {
                return AppleAuthResult.Failed(
                    nativeResult.ErrorMessage ?? "Apple sign-in failed. Please try again.");
            }

            var payload = JsonSerializer.Serialize(
                new
                {
                    id_token = nativeResult.IdentityToken,
                    raw_nonce = rawNonce,
                    display_name = nativeResult.DisplayName
                },
                _jsonOptions);

            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(
                $"{AppConfig.BaseUrl}/api/auth/apple",
                content);
            var raw = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine(
                $"=== APPLE AUTH RESPONSE: {response.StatusCode} {raw}");

            if (!response.IsSuccessStatusCode)
                return AppleAuthResult.Failed(ExtractErrorDetail(raw));

            var authResult = JsonSerializer.Deserialize<AuthResult>(raw, _jsonOptions);
            if (authResult is null)
                return AppleAuthResult.Failed("Apple sign-in returned an invalid response.");

            await ApplySuccessfulAuthAsync(authResult);
            return AppleAuthResult.Ok();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== APPLE AUTH ERROR: {ex}");
            return AppleAuthResult.Failed(
                "Apple sign-in couldn't be completed. Please try again.");
        }
#else
        await Task.CompletedTask;
        return AppleAuthResult.Failed("Sign in with Apple is only available on iPhone and iPad.");
#endif
    }

#if IOS
    private static string CreateAppleNonce()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string Sha256(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed class AppleNativeResult
    {
        public bool Success { get; init; }
        public bool Cancelled { get; init; }
        public string? IdentityToken { get; init; }
        public string? DisplayName { get; init; }
        public string? ErrorMessage { get; init; }
    }

    private sealed class AppleAuthorizationDelegate :
        ASAuthorizationControllerDelegate,
        IASAuthorizationControllerPresentationContextProviding
    {
        private readonly string _expectedState;

        public TaskCompletionSource<AppleNativeResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AppleAuthorizationDelegate(string expectedState)
        {
            _expectedState = expectedState;
        }

        public override void DidComplete(
            ASAuthorizationController controller,
            ASAuthorization authorization)
        {
            try
            {
                var credential =
                    authorization.GetCredential<ASAuthorizationAppleIdCredential>();

                if (credential is null)
                {
                    Completion.TrySetResult(new AppleNativeResult
                    {
                        ErrorMessage = "Apple returned an unexpected credential."
                    });
                    return;
                }

                if (!string.Equals(
                        credential.State,
                        _expectedState,
                        StringComparison.Ordinal))
                {
                    Completion.TrySetResult(new AppleNativeResult
                    {
                        ErrorMessage = "Apple sign-in state validation failed."
                    });
                    return;
                }

                if (credential.IdentityToken is null)
                {
                    Completion.TrySetResult(new AppleNativeResult
                    {
                        ErrorMessage = "Apple did not return an identity token."
                    });
                    return;
                }

                var token = new NSString(
                    credential.IdentityToken,
                    NSStringEncoding.UTF8).ToString();

                var nameParts = new[]
                {
                    credential.FullName?.GivenName,
                    credential.FullName?.FamilyName
                }
                .Where(part => !string.IsNullOrWhiteSpace(part));

                Completion.TrySetResult(new AppleNativeResult
                {
                    Success = true,
                    IdentityToken = token,
                    DisplayName = string.Join(" ", nameParts)
                });
            }
            catch (Exception ex)
            {
                Completion.TrySetResult(new AppleNativeResult
                {
                    ErrorMessage = ex.Message
                });
            }
        }

        public override void DidComplete(
            ASAuthorizationController controller,
            NSError error)
        {
            var code = (ASAuthorizationError)(long)error.Code;
            if (code == ASAuthorizationError.Canceled)
            {
                Completion.TrySetResult(new AppleNativeResult
                {
                    Cancelled = true
                });
                return;
            }

            Completion.TrySetResult(new AppleNativeResult
            {
                ErrorMessage = error.LocalizedDescription
            });
        }

        public UIWindow GetPresentationAnchor(
            ASAuthorizationController controller)
        {
            var keyWindow = UIApplication.SharedApplication
                .ConnectedScenes
                .OfType<UIWindowScene>()
                .SelectMany(scene => scene.Windows)
                .FirstOrDefault(window => window.IsKeyWindow);

            return keyWindow
                ?? UIApplication.SharedApplication.Windows.First();
        }
    }
#endif

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
                var isUnverified = response.StatusCode == System.Net.HttpStatusCode.Forbidden;
                return EmailAuthResult.Failed(ExtractErrorDetail(raw), isUnverifiedEmail: isUnverified);
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
    // Session storage abstraction
    // -------------------------------------------------------
    // .NET 9 + Xcode 26 currently strips Keychain entitlements from iOS
    // simulator builds. MAUI SecureStorage then fails with SecItem* -34018.
    // Keep production/device builds on SecureStorage, but use Preferences in
    // DEBUG iOS simulators so Apple-auth/onboarding can be exercised without
    // weakening the real app's credential storage.
    private static bool UseSimulatorSessionStorage
    {
        get
        {
#if IOS && DEBUG
            return Microsoft.Maui.Devices.DeviceInfo.Current.DeviceType ==
                   Microsoft.Maui.Devices.DeviceType.Virtual;
#else
            return false;
#endif
        }
    }

    private static Task SetSessionValueAsync(string key, string value)
    {
        if (UseSimulatorSessionStorage)
        {
            Microsoft.Maui.Storage.Preferences.Default.Set($"sim_{key}", value);
            return Task.CompletedTask;
        }

        return SecureStorage.SetAsync(key, value);
    }

    private static async Task<string?> GetSessionValueAsync(string key)
    {
        if (UseSimulatorSessionStorage)
        {
            return Microsoft.Maui.Storage.Preferences.Default.Get(
                $"sim_{key}", string.Empty);
        }

        return await SecureStorage.GetAsync(key);
    }

    private static void RemoveSessionValue(string key)
    {
        if (UseSimulatorSessionStorage)
        {
            Microsoft.Maui.Storage.Preferences.Default.Remove($"sim_{key}");
            return;
        }

        SecureStorage.Remove(key);
    }

    // -------------------------------------------------------
    // Restore session on app launch
    // -------------------------------------------------------
    public async Task<bool> TryRestoreSessionAsync()
    {
        try
        {
            _jwt = await GetSessionValueAsync("auth_jwt");
            _userId = await GetSessionValueAsync("auth_user_id");
            _householdId = await GetSessionValueAsync("auth_household_id");
            _email = await GetSessionValueAsync("auth_email");
            _displayName = await GetSessionValueAsync("auth_display_name");
            _authProvider = await GetSessionValueAsync("auth_provider");

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
        _jwt = _userId = _householdId = _email = _displayName = _authProvider = null;
        RemoveSessionValue("auth_jwt");
        RemoveSessionValue("auth_user_id");
        RemoveSessionValue("auth_household_id");
        RemoveSessionValue("auth_email");
        RemoveSessionValue("auth_display_name");
        RemoveSessionValue("auth_provider");
    }

    public string? GetAuthHeader() =>
        IsAuthenticated ? $"Bearer {_jwt}" : null;

    private class AuthResult
    {
        public string Token { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public string? HouseholdId { get; set; }
        public string Email { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;

        [JsonPropertyName("auth_provider")]
        public string? AuthProvider { get; set; }

        [JsonPropertyName("is_new_user")]
        public bool IsNewUser { get; set; }
    }

    public class AppleAuthResult
    {
        public bool Success { get; private set; }
        public bool Cancelled { get; private set; }
        public string? ErrorMessage { get; private set; }

        public static AppleAuthResult Ok() =>
            new() { Success = true };

        public static AppleAuthResult CancelledByUser() =>
            new() { Cancelled = true };

        public static AppleAuthResult Failed(string message) =>
            new() { ErrorMessage = message };
    }

    public class EmailAuthResult
    {
        public bool Success { get; private set; }
        public string? ErrorMessage { get; private set; }
        // True specifically when login failed because the account exists
        // but hasn't verified its email (HTTP 403) — distinct from wrong
        // password, wrong provider, etc. (401). Lets the UI offer a
        // "resend verification" action only when it's actually relevant.
        public bool IsUnverifiedEmail { get; private set; }

        public static EmailAuthResult Ok() => new() { Success = true };
        public static EmailAuthResult Failed(string message, bool isUnverifiedEmail = false) =>
            new() { Success = false, ErrorMessage = message, IsUnverifiedEmail = isUnverifiedEmail };
    }
}