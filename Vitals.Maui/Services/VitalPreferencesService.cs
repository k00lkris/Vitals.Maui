using CommunityToolkit.Mvvm.ComponentModel;
using Vitals.Maui.Models;

namespace Vitals.Maui.Services;

/// <summary>
/// Shared, account-scoped source of truth for the current user's display
/// preferences. The users table is authoritative; per-user MAUI Preferences
/// entries are only an offline/startup cache.
/// </summary>
public partial class VitalPreferencesService : ObservableObject
{
    private readonly ApiService _api;
    private readonly AuthService _auth;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    private bool _isApplyingSnapshot;
    private string? _loadedUserId;

    [ObservableProperty] private string _theme = "vitals_blue";
    [ObservableProperty] private bool _showHeartRate = true;
    [ObservableProperty] private bool _showSpo2 = true;
    [ObservableProperty] private bool _showTemperature = true;
    [ObservableProperty] private bool _showWeight;
    [ObservableProperty] private bool _showGlucose;

    // Blood pressure intentionally has no user toggle in the current product.
    public bool ShowBloodPressure => true;

    public VitalPreferencesService(ApiService api, AuthService auth)
    {
        _api = api;
        _auth = auth;
        ApplyCachedOrDefaults(_auth.UserId);
    }

    /// <summary>
    /// Hydrates the current account from its local cache first, then replaces
    /// that snapshot with the server row when available. A successful server
    /// load is remembered for the current user so normal page navigation does
    /// not re-fetch the same preferences repeatedly.
    /// </summary>
    public async Task LoadAsync(bool forceRefresh = false)
    {
        var userId = _auth.UserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            Reset();
            return;
        }

        await _loadGate.WaitAsync();
        try
        {
            // The account may have changed while this call was waiting.
            if (!string.Equals(_auth.UserId, userId, StringComparison.Ordinal))
                return;

            if (!forceRefresh && string.Equals(_loadedUserId, userId, StringComparison.Ordinal))
                return;

            ApplyCachedOrDefaults(userId);

            var serverPreferences = await _api.GetUserPreferencesAsync(userId);

            // Never apply one account's response after a sign-out/account switch.
            if (!string.Equals(_auth.UserId, userId, StringComparison.Ordinal))
                return;

            if (serverPreferences is null)
            {
                // Keep the account-scoped cache as an offline fallback. Do not
                // mark the server load complete so a later call can retry.
                return;
            }

            ApplySnapshot(serverPreferences);
            WriteCache(userId);
            _loadedUserId = userId;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>
    /// Flushes the latest in-memory snapshot to the current user's server row.
    /// Useful at onboarding boundaries where navigation should wait until the
    /// selected preferences have definitely been persisted.
    /// </summary>
    public async Task SaveAsync()
    {
        var userId = _auth.UserId;
        if (string.IsNullOrWhiteSpace(userId))
            return;

        WriteCache(userId);
        await SaveLatestSnapshotAsync(userId);
    }

    /// <summary>
    /// Clears only the in-memory account association. Per-user cached values
    /// remain on-device for offline use if that same account signs in again.
    /// </summary>
    public void Reset()
    {
        _loadedUserId = null;
        ApplySnapshot(new UserPreferences());
    }

    partial void OnThemeChanged(string value)
    {
        // App.xaml.cs still reads this global key before authentication, so
        // keep it current even though all vital flags are cached per user.
        Preferences.Set("theme", value);

        // Session restoration loads preferences on a background task. Theme
        // resources are UI state, so marshal that part back to the main thread.
        if (Application.Current is not null)
        {
            if (MainThread.IsMainThread)
                ThemeService.Apply(value);
            else
                MainThread.BeginInvokeOnMainThread(() => ThemeService.Apply(value));
        }

        HandlePreferenceChanged();
    }

    partial void OnShowHeartRateChanged(bool value) => HandlePreferenceChanged();
    partial void OnShowSpo2Changed(bool value) => HandlePreferenceChanged();
    partial void OnShowTemperatureChanged(bool value) => HandlePreferenceChanged();
    partial void OnShowWeightChanged(bool value) => HandlePreferenceChanged();
    partial void OnShowGlucoseChanged(bool value) => HandlePreferenceChanged();

    private void HandlePreferenceChanged()
    {
        if (_isApplyingSnapshot)
            return;

        var userId = _auth.UserId;
        if (string.IsNullOrWhiteSpace(userId))
            return;

        WriteCache(userId);

        // Serialize saves so rapid switch changes cannot arrive at the API out
        // of order. Each queued save takes a fresh snapshot once it owns the
        // gate, so the last request always contains the latest state.
        _ = SaveLatestSnapshotAsync(userId);
    }

    private async Task SaveLatestSnapshotAsync(string userId)
    {
        await _saveGate.WaitAsync();
        try
        {
            // A queued write from an old account must never be redirected to
            // the user who signed in afterward.
            if (!string.Equals(_auth.UserId, userId, StringComparison.Ordinal))
                return;

            await _api.UpdateUserPreferencesAsync(
                userId,
                new
                {
                    theme = Theme,
                    show_heart_rate = ShowHeartRate,
                    show_spo2 = ShowSpo2,
                    show_temperature = ShowTemperature,
                    show_weight = ShowWeight,
                    show_glucose = ShowGlucose,
                });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== SAVE PREFS ERROR: {ex.Message}");
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private void ApplyCachedOrDefaults(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId) ||
            !Preferences.Get(CacheKey(userId, "cached"), false))
        {
            ApplySnapshot(new UserPreferences());
            return;
        }

        ApplySnapshot(new UserPreferences
        {
            Theme = Preferences.Get(CacheKey(userId, "theme"), "vitals_blue"),
            ShowHeartRate = Preferences.Get(CacheKey(userId, "show_heart_rate"), true),
            ShowSpo2 = Preferences.Get(CacheKey(userId, "show_spo2"), true),
            ShowTemperature = Preferences.Get(CacheKey(userId, "show_temperature"), true),
            ShowWeight = Preferences.Get(CacheKey(userId, "show_weight"), false),
            ShowGlucose = Preferences.Get(CacheKey(userId, "show_glucose"), false),
        });
    }

    private void ApplySnapshot(UserPreferences preferences)
    {
        _isApplyingSnapshot = true;
        try
        {
            Theme = string.IsNullOrWhiteSpace(preferences.Theme)
                ? "vitals_blue"
                : preferences.Theme;
            ShowHeartRate = preferences.ShowHeartRate;
            ShowSpo2 = preferences.ShowSpo2;
            ShowTemperature = preferences.ShowTemperature;
            ShowWeight = preferences.ShowWeight;
            ShowGlucose = preferences.ShowGlucose;
        }
        finally
        {
            _isApplyingSnapshot = false;
        }
    }

    private void WriteCache(string userId)
    {
        Preferences.Set(CacheKey(userId, "cached"), true);
        Preferences.Set(CacheKey(userId, "theme"), Theme);
        Preferences.Set(CacheKey(userId, "show_heart_rate"), ShowHeartRate);
        Preferences.Set(CacheKey(userId, "show_spo2"), ShowSpo2);
        Preferences.Set(CacheKey(userId, "show_temperature"), ShowTemperature);
        Preferences.Set(CacheKey(userId, "show_weight"), ShowWeight);
        Preferences.Set(CacheKey(userId, "show_glucose"), ShowGlucose);

        // Kept only for the pre-auth theme application in App.xaml.cs.
        Preferences.Set("theme", Theme);
    }

    private static string CacheKey(string userId, string name) =>
        $"user_preferences:{userId}:{name}";
}
