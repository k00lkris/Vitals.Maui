using Vitals.Maui.Models;

namespace Vitals.Maui.Services;

public class UserPreferencesService
{
    private readonly ApiService _api;
    private readonly AuthService _auth;

    public UserPreferencesService(ApiService api, AuthService auth)
    {
        _api = api;
        _auth = auth;
    }

    public UserPreferences LocalSnapshot() => new()
    {
        UserId = _auth.UserId ?? string.Empty,
        Theme = Preferences.Get("theme", "vitals_blue"),
        ShowHeartRate = Preferences.Get("show_heart_rate", true),
        ShowSpo2 = Preferences.Get("show_spo2", true),
        ShowTemperature = Preferences.Get("show_temperature", true),
        ShowWeight = Preferences.Get("show_weight", false),
        ShowGlucose = Preferences.Get("show_glucose", false),
    };

    public async Task<UserPreferences> RefreshAsync()
    {
        var local = LocalSnapshot();
        if (string.IsNullOrWhiteSpace(_auth.UserId))
            return local;

        var remote = await _api.GetUserPreferencesAsync(_auth.UserId);
        if (remote is null)
            return local;

        CacheLocal(remote);
        return remote;
    }

    public async Task<bool> SaveAsync(UserPreferences preferences)
    {
        CacheLocal(preferences);

        if (string.IsNullOrWhiteSpace(_auth.UserId))
            return false;

        return await _api.UpdateUserPreferencesAsync(
            _auth.UserId,
            new
            {
                theme = preferences.Theme,
                show_heart_rate = preferences.ShowHeartRate,
                show_spo2 = preferences.ShowSpo2,
                show_temperature = preferences.ShowTemperature,
                show_weight = preferences.ShowWeight,
                show_glucose = preferences.ShowGlucose,
            });
    }

    public void CacheLocal(UserPreferences preferences)
    {
        Preferences.Set("theme", preferences.Theme);
        Preferences.Set("show_heart_rate", preferences.ShowHeartRate);
        Preferences.Set("show_spo2", preferences.ShowSpo2);
        Preferences.Set("show_temperature", preferences.ShowTemperature);
        Preferences.Set("show_weight", preferences.ShowWeight);
        Preferences.Set("show_glucose", preferences.ShowGlucose);
    }
}
