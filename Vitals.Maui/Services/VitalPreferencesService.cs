using CommunityToolkit.Mvvm.ComponentModel;
using Vitals.Maui.Models;

namespace Vitals.Maui.Services;

/// <summary>
/// Shared, account-aware source of truth for which vitals the current user
/// tracks. The users table is authoritative; per-user MAUI Preferences are
/// only an offline/startup cache.
///
/// Blood pressure is deliberately always enabled because there is currently
/// no show_blood_pressure preference in the product or database.
/// </summary>
public partial class VitalPreferencesService : ObservableObject
{
    private readonly ApiService _api;
    private readonly AuthService _auth;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    private string? _loadedUserId;

    [ObservableProperty] private string _theme = "vitals_blue";
    [ObservableProperty] private bool _showHeartRate = true;
    [ObservableProperty] private bool _showSpo2 = true;
    [ObservableProperty] private bool _showTemperature = true;
    [ObservableProperty] private bool _showWeight;
    [ObservableProperty] private bool _showGlucose;

    public bool ShowBloodPressure => true;

    public VitalPreferencesService(ApiService api, AuthService auth)
    {
        _api = api;
        _auth = auth;

        // Theme is safe/useful before authentication so the launch screen
        // doesn't flash back to the default theme. Vital flags themselves
        // are account-scoped and are loaded once we know the signed-in user.
        Theme = Preferences.Get("theme", "vitals_blue");
    }

    /// <summary>
    /// Loads the current account's cached values immediately, then replaces
    /// them with the server values when available. A cache is namespaced by
    /// user id so two accounts sharing a device cannot inherit each other's
    /// vital selections.
    /// </summary>
    public async Task LoadAsync(bool forceRefresh = false)
    {
        var userId = _auth.UserId;
        if (string.IsNullOrWhiteSpace(userId))
            return;

        await _loadLock.WaitAsync();
        try
        {
            if (!forceRefresh && string.Equals(_loadedUserId, userId, StringComparison.Ordinal))
                return;

            ApplyLocalCache(userId);
            _loadedUserId = userId;

            var server = await _api.GetUserPreferencesAsync(userId);
            if (server is null)
                return;

            Apply(server);
            SaveLocalCache(userId);
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public Task SetThemeAsync(string value)
    {
        value = string.IsNullOrWhiteSpace(value) ? "vitals_blue" : value;
        if (Theme == value)
            return Task.CompletedTask;

        Theme = value;
        ThemeService.Apply(value);
        SaveLocalCacheForCurrentUser();
        return SaveAsync();
    }

    public Task SetShowHeartRateAsync(bool value) =>
        SetVitalAsync(() => ShowHeartRate, v => ShowHeartRate = v, value);

    public Task SetShowSpo2Async(bool value) =>
        SetVitalAsync(() => ShowSpo2, v => ShowSpo2 = v, value);

    public Task SetShowTemperatureAsync(bool value) =>
        SetVitalAsync(() => ShowTemperature, v => ShowTemperature = v, value);

    public Task SetShowWeightAsync(bool value) =>
        SetVitalAsync(() => ShowWeight, v => ShowWeight = v, value);

    public Task SetShowGlucoseAsync(bool value) =>
        SetVitalAsync(() => ShowGlucose, v => ShowGlucose = v, value);

    private Task SetVitalAsync(Func<bool> getter, Action<bool> setter, bool value)
    {
        if (getter() == value)
            return Task.CompletedTask;

        setter(value);
        SaveLocalCacheForCurrentUser();
        return SaveAsync();
    }

    /// <summary>
    /// Persists a full snapshot. Saves are serialized and the snapshot is
    /// built only after entering the lock, so rapid switch changes converge
    /// on the newest state instead of allowing an older request to finish
    /// last and overwrite newer choices.
    /// </summary>
    public async Task SaveAsync()
    {
        var userId = _auth.UserId;
        if (string.IsNullOrWhiteSpace(userId))
            return;

        await _saveLock.WaitAsync();
        try
        {
            var success = await _api.UpdateUserPreferencesAsync(
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

            if (!success)
                System.Diagnostics.Debug.WriteLine("=== VITAL PREFS: server save failed; local cache retained");
        }
        catch (Exception ex)
        {
            // Offline changes remain in the per-user cache. A later server
            // refresh may replace them with the database state, which is
            // intentionally authoritative.
            System.Diagnostics.Debug.WriteLine($"=== VITAL PREFS SAVE ERROR: {ex.Message}");
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>
    /// Clears only in-memory account association on sign-out. Per-user cache
    /// remains on the device for offline startup if that same account returns.
    /// </summary>
    public void Reset()
    {
        _loadedUserId = null;
        ShowHeartRate = true;
        ShowSpo2 = true;
        ShowTemperature = true;
        ShowWeight = false;
        ShowGlucose = false;
        WriteCompatibilityKeys();
    }

    private void Apply(UserPreferences preferences)
    {
        Theme = string.IsNullOrWhiteSpace(preferences.Theme) ? "vitals_blue" : preferences.Theme;
        ShowHeartRate = preferences.ShowHeartRate;
        ShowSpo2 = preferences.ShowSpo2;
        ShowTemperature = preferences.ShowTemperature;
        ShowWeight = preferences.ShowWeight;
        ShowGlucose = preferences.ShowGlucose;

        ThemeService.Apply(Theme);
        WriteCompatibilityKeys();
    }

    private void ApplyLocalCache(string userId)
    {
        var prefix = CachePrefix(userId);

        if (!Preferences.Get(prefix + "cached", false))
        {
            ShowHeartRate = true;
            ShowSpo2 = true;
            ShowTemperature = true;
            ShowWeight = false;
            ShowGlucose = false;
            WriteCompatibilityKeys();
            return;
        }

        Theme = Preferences.Get(prefix + "theme", Preferences.Get("theme", "vitals_blue"));
        ShowHeartRate = Preferences.Get(prefix + "show_heart_rate", true);
        ShowSpo2 = Preferences.Get(prefix + "show_spo2", true);
        ShowTemperature = Preferences.Get(prefix + "show_temperature", true);
        ShowWeight = Preferences.Get(prefix + "show_weight", false);
        ShowGlucose = Preferences.Get(prefix + "show_glucose", false);

        ThemeService.Apply(Theme);
        WriteCompatibilityKeys();
    }

    private void SaveLocalCacheForCurrentUser()
    {
        var userId = _auth.UserId;
        if (!string.IsNullOrWhiteSpace(userId))
            SaveLocalCache(userId);
        else
            WriteCompatibilityKeys();
    }

    private void SaveLocalCache(string userId)
    {
        var prefix = CachePrefix(userId);
        Preferences.Set(prefix + "cached", true);
        Preferences.Set(prefix + "theme", Theme);
        Preferences.Set(prefix + "show_heart_rate", ShowHeartRate);
        Preferences.Set(prefix + "show_spo2", ShowSpo2);
        Preferences.Set(prefix + "show_temperature", ShowTemperature);
        Preferences.Set(prefix + "show_weight", ShowWeight);
        Preferences.Set(prefix + "show_glucose", ShowGlucose);

        // Keep the old unscoped keys synchronized during the migration so
        // onboarding/other screens not yet moved to this service don't drift.
        WriteCompatibilityKeys();
    }

    private void WriteCompatibilityKeys()
    {
        Preferences.Set("theme", Theme);
        Preferences.Set("show_heart_rate", ShowHeartRate);
        Preferences.Set("show_spo2", ShowSpo2);
        Preferences.Set("show_temperature", ShowTemperature);
        Preferences.Set("show_weight", ShowWeight);
        Preferences.Set("show_glucose", ShowGlucose);
    }

    private static string CachePrefix(string userId) => $"vital_prefs:{userId}:";
}
