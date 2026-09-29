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

    public Patient? SelectedPatient => _patientState.SelectedPatient;

    // Initial per-patient vital selection for the active patient. Other
    // patients retain their own defaults until configured independently.
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
    }

    public async Task LoadAsync()
    {
        await _patientState.InitializeAsync();
        OnPropertyChanged(nameof(SelectedPatient));

        var patientId = SelectedPatient?.PatientId;
        if (string.IsNullOrWhiteSpace(patientId))
            return;

        var current = await _preferences.RefreshAsync(patientId);
        ShowHeartRate = current.ShowHeartRate;
        ShowSpo2 = current.ShowSpo2;
        ShowTemperature = current.ShowTemperature;
        ShowWeight = current.ShowWeight;
        ShowGlucose = current.ShowGlucose;
    }

    [RelayCommand]
    public async Task ContinueAsync()
    {
        var patientId = SelectedPatient?.PatientId;
        if (string.IsNullOrWhiteSpace(patientId))
        {
            OnContinue?.Invoke();
            return;
        }

        var current = _preferences.LocalSnapshot(patientId);
        await _preferences.SaveAsync(
            new UserPreferences
            {
                UserId = current.UserId,
                PatientId = patientId,
                Theme = current.Theme,
                ShowHeartRate = ShowHeartRate,
                ShowSpo2 = ShowSpo2,
                ShowTemperature = ShowTemperature,
                ShowWeight = ShowWeight,
                ShowGlucose = ShowGlucose,
            },
            patientId);

        OnContinue?.Invoke();
    }

    [RelayCommand]
    public void Back()
    {
        OnBack?.Invoke();
    }
}
