using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vitals.Maui.Services;

namespace Vitals.Maui.ViewModels;

public partial class OnboardingVitalPreferencesViewModel : ObservableObject
{
    private readonly VitalPreferencesService _vitalPreferences;

    public Action? OnContinue { get; set; }
    public Action? OnBack { get; set; }

    // Blood pressure remains always tracked; the optional vitals proxy the
    // same shared service used by Settings and Vitals Entry.
    public bool ShowHeartRate
    {
        get => _vitalPreferences.ShowHeartRate;
        set
        {
            if (value == _vitalPreferences.ShowHeartRate) return;
            _ = _vitalPreferences.SetShowHeartRateAsync(value);
        }
    }

    public bool ShowSpo2
    {
        get => _vitalPreferences.ShowSpo2;
        set
        {
            if (value == _vitalPreferences.ShowSpo2) return;
            _ = _vitalPreferences.SetShowSpo2Async(value);
        }
    }

    public bool ShowTemperature
    {
        get => _vitalPreferences.ShowTemperature;
        set
        {
            if (value == _vitalPreferences.ShowTemperature) return;
            _ = _vitalPreferences.SetShowTemperatureAsync(value);
        }
    }

    public bool ShowWeight
    {
        get => _vitalPreferences.ShowWeight;
        set
        {
            if (value == _vitalPreferences.ShowWeight) return;
            _ = _vitalPreferences.SetShowWeightAsync(value);
        }
    }

    public bool ShowGlucose
    {
        get => _vitalPreferences.ShowGlucose;
        set
        {
            if (value == _vitalPreferences.ShowGlucose) return;
            _ = _vitalPreferences.SetShowGlucoseAsync(value);
        }
    }

    public OnboardingVitalPreferencesViewModel(VitalPreferencesService vitalPreferences)
    {
        _vitalPreferences = vitalPreferences;

        _vitalPreferences.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(VitalPreferencesService.ShowHeartRate):
                    OnPropertyChanged(nameof(ShowHeartRate));
                    break;
                case nameof(VitalPreferencesService.ShowSpo2):
                    OnPropertyChanged(nameof(ShowSpo2));
                    break;
                case nameof(VitalPreferencesService.ShowTemperature):
                    OnPropertyChanged(nameof(ShowTemperature));
                    break;
                case nameof(VitalPreferencesService.ShowWeight):
                    OnPropertyChanged(nameof(ShowWeight));
                    break;
                case nameof(VitalPreferencesService.ShowGlucose):
                    OnPropertyChanged(nameof(ShowGlucose));
                    break;
            }
        };

        // Handles resumed onboarding: cached values appear first and server
        // values replace them when reachable.
        _ = _vitalPreferences.LoadAsync();
    }

    [RelayCommand]
    public async Task Continue()
    {
        // Ensure the final combination has reached the serialized save queue
        // before moving to the first-reading step.
        await _vitalPreferences.SaveAsync();
        OnContinue?.Invoke();
    }

    [RelayCommand]
    public void Back()
    {
        OnBack?.Invoke();
    }
}
