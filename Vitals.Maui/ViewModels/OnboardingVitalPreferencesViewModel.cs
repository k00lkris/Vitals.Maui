using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Vitals.Maui.ViewModels;

public partial class OnboardingVitalPreferencesViewModel : ObservableObject
{
    // Wired by the page's code-behind, same pattern as the other onboarding VMs.
    public Action? OnContinue { get; set; }
    public Action? OnBack { get; set; }

    // Same Preferences keys SettingsViewModel and VitalsEntryViewModel already
    // use — setting these here means Settings and the real Vitals Entry
    // screen are immediately consistent with whatever's chosen during
    // onboarding, not a separate onboarding-only preference.
    // Blood pressure has no toggle here either, matching VitalsEntryPage —
    // it's always tracked.
    [ObservableProperty] private bool _showHeartRate = true;
    [ObservableProperty] private bool _showSpo2 = true;
    [ObservableProperty] private bool _showTemperature = true;
    [ObservableProperty] private bool _showWeight = false;
    [ObservableProperty] private bool _showGlucose = false;

    public OnboardingVitalPreferencesViewModel()
    {
        ShowHeartRate = Preferences.Get("show_heart_rate", true);
        ShowSpo2 = Preferences.Get("show_spo2", true);
        ShowTemperature = Preferences.Get("show_temperature", true);
        ShowWeight = Preferences.Get("show_weight", false);
        ShowGlucose = Preferences.Get("show_glucose", false);
    }

    partial void OnShowHeartRateChanged(bool value) => Preferences.Set("show_heart_rate", value);
    partial void OnShowSpo2Changed(bool value) => Preferences.Set("show_spo2", value);
    partial void OnShowTemperatureChanged(bool value) => Preferences.Set("show_temperature", value);
    partial void OnShowWeightChanged(bool value) => Preferences.Set("show_weight", value);
    partial void OnShowGlucoseChanged(bool value) => Preferences.Set("show_glucose", value);

    [RelayCommand]
    public void Continue()
    {
        OnContinue?.Invoke();
    }

    [RelayCommand]
    public void Back()
    {
        OnBack?.Invoke();
    }
}
