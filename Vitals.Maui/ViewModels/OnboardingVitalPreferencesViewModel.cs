using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vitals.Maui.Services;

namespace Vitals.Maui.ViewModels;

public partial class OnboardingVitalPreferencesViewModel : ObservableObject
{
    private readonly ApiService _api;
    private readonly AuthService _auth;
    // Wired by the page's code-behind, same pattern as the other onboarding VMs.
    public Action? OnContinue { get; set; }
    public Action? OnBack { get; set; }

    // Same Preferences keys SettingsViewModel and VitalsEntryViewModel already
    // use — setting these here means Settings and the real Vitals Entry
    // screen are immediately consistent with whatever's chosen during
    // onboarding, not a separate onboarding-only preference.
    [ObservableProperty] private bool _showBloodPressure = true;
    [ObservableProperty] private bool _showHeartRate = true;
    [ObservableProperty] private bool _showSpo2 = true;
    [ObservableProperty] private bool _showTemperature = true;
    [ObservableProperty] private bool _showWeight = false;
    [ObservableProperty] private bool _showGlucose = false;

    public OnboardingVitalPreferencesViewModel(ApiService api, AuthService auth)
    {
        _api = api;
        _auth = auth;
        ShowBloodPressure = Preferences.Get("show_blood_pressure", true);
        ShowHeartRate = Preferences.Get("show_heart_rate", true);
        ShowSpo2 = Preferences.Get("show_spo2", true);
        ShowTemperature = Preferences.Get("show_temperature", true);
        ShowWeight = Preferences.Get("show_weight", false);
        ShowGlucose = Preferences.Get("show_glucose", false);
    }

    partial void OnShowBloodPressureChanged(bool value) => Preferences.Set("show_blood_pressure", value);
    partial void OnShowHeartRateChanged(bool value) => Preferences.Set("show_heart_rate", value);
    partial void OnShowSpo2Changed(bool value) => Preferences.Set("show_spo2", value);
    partial void OnShowTemperatureChanged(bool value) => Preferences.Set("show_temperature", value);
    partial void OnShowWeightChanged(bool value) => Preferences.Set("show_weight", value);
    partial void OnShowGlucoseChanged(bool value) => Preferences.Set("show_glucose", value);

    [RelayCommand]
    public async Task Continue()
    {
        if (!(ShowBloodPressure || ShowHeartRate || ShowSpo2 || ShowTemperature || ShowWeight || ShowGlucose))
        {
            await Shell.Current.DisplayAlert(
                "Choose at least one vital",
                "Select at least one vital to track before continuing.",
                "OK");
            return;
        }

        var userId = _auth.UserId;
        if (!string.IsNullOrEmpty(userId))
        {
            var saved = await _api.UpdateUserPreferencesAsync(userId, new
            {
                show_blood_pressure = ShowBloodPressure,
                show_heart_rate = ShowHeartRate,
                show_spo2 = ShowSpo2,
                show_temperature = ShowTemperature,
                show_weight = ShowWeight,
                show_glucose = ShowGlucose,
            });

            if (!saved)
            {
                await Shell.Current.DisplayAlert(
                    "Couldn't save preferences",
                    "Your vital preferences couldn't be saved. Please try again.",
                    "OK");
                return;
            }
        }

        OnContinue?.Invoke();
    }

    [RelayCommand]
    public void Back()
    {
        OnBack?.Invoke();
    }
}
