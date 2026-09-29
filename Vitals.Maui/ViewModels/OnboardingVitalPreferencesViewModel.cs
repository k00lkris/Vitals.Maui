using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vitals.Maui.Services;

namespace Vitals.Maui.ViewModels;

public partial class OnboardingVitalPreferencesViewModel : ObservableObject
{
    private readonly VitalPreferencesService _preferences;

    // Wired by the page's code-behind, same pattern as the other onboarding VMs.
    public Action? OnContinue { get; set; }
    public Action? OnBack { get; set; }

    // Blood pressure intentionally has no toggle and remains always tracked.
    public bool ShowHeartRate
    {
        get => _preferences.ShowHeartRate;
        set => _preferences.ShowHeartRate = value;
    }

    public bool ShowSpo2
    {
        get => _preferences.ShowSpo2;
        set => _preferences.ShowSpo2 = value;
    }

    public bool ShowTemperature
    {
        get => _preferences.ShowTemperature;
        set => _preferences.ShowTemperature = value;
    }

    public bool ShowWeight
    {
        get => _preferences.ShowWeight;
        set => _preferences.ShowWeight = value;
    }

    public bool ShowGlucose
    {
        get => _preferences.ShowGlucose;
        set => _preferences.ShowGlucose = value;
    }

    public OnboardingVitalPreferencesViewModel(VitalPreferencesService preferences)
    {
        _preferences = preferences;

        _preferences.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(VitalPreferencesService.ShowHeartRate)
                or nameof(VitalPreferencesService.ShowSpo2)
                or nameof(VitalPreferencesService.ShowTemperature)
                or nameof(VitalPreferencesService.ShowWeight)
                or nameof(VitalPreferencesService.ShowGlucose))
            {
                OnPropertyChanged(e.PropertyName);
            }
        };
    }

    [RelayCommand]
    public async Task ContinueAsync()
    {
        // This boundary is worth awaiting so the server persistence attempt
        // completes before the first-reading screen starts loading preferences.
        await _preferences.SaveAsync();
        OnContinue?.Invoke();
    }

    [RelayCommand]
    public void Back()
    {
        OnBack?.Invoke();
    }
}
