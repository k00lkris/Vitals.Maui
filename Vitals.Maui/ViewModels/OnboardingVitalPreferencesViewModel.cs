using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vitals.Maui.Models;
using Vitals.Maui.Services;

namespace Vitals.Maui.ViewModels;

public partial class OnboardingVitalPreferencesViewModel : ObservableObject
{
    private readonly UserPreferencesService _preferences;
    private readonly PatientStateService _patientState;

    // Wired by the page's code-behind, same pattern as the other onboarding VMs.
    public Action? OnContinue { get; set; }
    public Action? OnBack { get; set; }

    // Initial per-patient vital selection. In a multi-patient onboarding
    // flow these choices seed each newly created patient, and can later be
    // customized independently in Settings.
    // Blood pressure has no toggle here either, matching VitalsEntryPage —
    // it's always tracked.
    [ObservableProperty] private bool _showHeartRate = true;
    [ObservableProperty] private bool _showSpo2 = true;
    [ObservableProperty] private bool _showTemperature = true;
    [ObservableProperty] private bool _showWeight = false;
    [ObservableProperty] private bool _showGlucose = false;

    public OnboardingVitalPreferencesViewModel(
        UserPreferencesService preferences,
        PatientStateService patientState)
    {
        _preferences = preferences;
        _patientState = patientState;
        var current = _preferences.LocalSnapshot();
        ShowHeartRate = current.ShowHeartRate;
        ShowSpo2 = current.ShowSpo2;
        ShowTemperature = current.ShowTemperature;
        ShowWeight = current.ShowWeight;
        ShowGlucose = current.ShowGlucose;
    }

    [RelayCommand]
    public async Task ContinueAsync()
    {
        // Onboarding has one initial vital-selection screen even for a
        // Family household. Seed every newly created patient with the chosen
        // starting set; afterward Settings can customize each patient
        // independently.
        _patientState.Reset();
        await _patientState.InitializeAsync();

        var current = _preferences.LocalSnapshot();
        var selected = new UserPreferences
        {
            UserId = current.UserId,
            Theme = current.Theme,
            ShowHeartRate = ShowHeartRate,
            ShowSpo2 = ShowSpo2,
            ShowTemperature = ShowTemperature,
            ShowWeight = ShowWeight,
            ShowGlucose = ShowGlucose,
        };

        foreach (var patient in _patientState.Patients)
        {
            selected.PatientId = patient.PatientId;
            await _preferences.SaveAsync(selected, patient.PatientId);
        }

        OnContinue?.Invoke();
    }

    [RelayCommand]
    public void Back()
    {
        OnBack?.Invoke();
    }
}
