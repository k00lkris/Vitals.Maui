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

    private static string PatientKey(string patientId, string key) =>
        $"patient:{patientId}:{key}";

    public UserPreferences LocalSnapshot(string? patientId = null)
    {
        // Theme is user/device scoped. Optional-vital settings are patient
        // scoped whenever we know which patient is active.
        bool GetVital(string key, bool defaultValue) =>
            string.IsNullOrWhiteSpace(patientId)
                ? Preferences.Get(key, defaultValue)
                : Preferences.Get(PatientKey(patientId, key), defaultValue);

        return new UserPreferences
        {
            UserId = _auth.UserId ?? string.Empty,
            PatientId = patientId,
            Theme = Preferences.Get("theme", "vitals_blue"),
            ShowHeartRate = GetVital("show_heart_rate", true),
            ShowSpo2 = GetVital("show_spo2", true),
            ShowTemperature = GetVital("show_temperature", true),
            ShowWeight = GetVital("show_weight", false),
            ShowGlucose = GetVital("show_glucose", false),
        };
    }

    public async Task<UserPreferences> RefreshAsync(string? patientId = null)
    {
        var local = LocalSnapshot(patientId);

        if (!string.IsNullOrWhiteSpace(patientId))
        {
            // Patient vital visibility has its own patient-scoped API. Do not
            // make these settings depend on the user-preference endpoint.
            var patientRemote = await _api.GetPatientVitalPreferencesAsync(patientId);
            if (patientRemote is not null)
            {
                local.PatientId = patientId;
                local.ShowHeartRate = patientRemote.ShowHeartRate;
                local.ShowSpo2 = patientRemote.ShowSpo2;
                local.ShowTemperature = patientRemote.ShowTemperature;
                local.ShowWeight = patientRemote.ShowWeight;
                local.ShowGlucose = patientRemote.ShowGlucose;
                CacheLocal(local, patientId);
            }
        }

        // Theme remains user-scoped. Failure here must never erase or block
        // the independently loaded patient vital preferences.
        if (!string.IsNullOrWhiteSpace(_auth.UserId))
        {
            var userRemote = await _api.GetUserPreferencesAsync(_auth.UserId);
            if (userRemote is not null)
            {
                local.Theme = userRemote.Theme;
                Preferences.Set("theme", local.Theme);
            }
        }

        return local;
    }

    public async Task<bool> SaveAsync(
        UserPreferences preferences,
        string? patientId = null)
    {
        if (string.IsNullOrWhiteSpace(patientId))
            return false;

        var saved = await _api.UpdatePatientVitalPreferencesAsync(
            patientId,
            new
            {
                show_heart_rate = preferences.ShowHeartRate,
                show_spo2 = preferences.ShowSpo2,
                show_temperature = preferences.ShowTemperature,
                show_weight = preferences.ShowWeight,
                show_glucose = preferences.ShowGlucose,
            });

        if (saved is null)
            return false;

        // Cache ONLY the server-confirmed values. Previously we cached first,
        // so a failed request left Settings looking enabled even though the
        // database was still false.
        var confirmed = new UserPreferences
        {
            UserId = _auth.UserId ?? string.Empty,
            PatientId = patientId,
            Theme = preferences.Theme,
            ShowHeartRate = saved.ShowHeartRate,
            ShowSpo2 = saved.ShowSpo2,
            ShowTemperature = saved.ShowTemperature,
            ShowWeight = saved.ShowWeight,
            ShowGlucose = saved.ShowGlucose,
        };
        CacheLocal(confirmed, patientId);
        return true;
    }

    public async Task<bool> SaveThemeAsync(string theme)
    {
        Preferences.Set("theme", theme);

        if (string.IsNullOrWhiteSpace(_auth.UserId))
            return false;

        return await _api.UpdateUserPreferencesAsync(
            _auth.UserId,
            new { theme });
    }

    public void CacheLocal(
        UserPreferences preferences,
        string? patientId = null)
    {
        Preferences.Set("theme", preferences.Theme);

        if (string.IsNullOrWhiteSpace(patientId))
        {
            // Legacy/onboarding defaults when there is not yet an active
            // patient. These are not used once a patient is selected.
            Preferences.Set("show_heart_rate", preferences.ShowHeartRate);
            Preferences.Set("show_spo2", preferences.ShowSpo2);
            Preferences.Set("show_temperature", preferences.ShowTemperature);
            Preferences.Set("show_weight", preferences.ShowWeight);
            Preferences.Set("show_glucose", preferences.ShowGlucose);
            return;
        }

        Preferences.Set(
            PatientKey(patientId, "show_heart_rate"),
            preferences.ShowHeartRate);
        Preferences.Set(
            PatientKey(patientId, "show_spo2"),
            preferences.ShowSpo2);
        Preferences.Set(
            PatientKey(patientId, "show_temperature"),
            preferences.ShowTemperature);
        Preferences.Set(
            PatientKey(patientId, "show_weight"),
            preferences.ShowWeight);
        Preferences.Set(
            PatientKey(patientId, "show_glucose"),
            preferences.ShowGlucose);
    }
}
