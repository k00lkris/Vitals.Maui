using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vitals.Maui.Models;
using Vitals.Maui.Services;

namespace Vitals.Maui.ViewModels;

public partial class OnboardingFirstVitalReadingViewModel : ObservableObject
{
    private readonly ApiService _api;
    private readonly PatientStateService _patientState;

    public System.Collections.ObjectModel.ObservableCollection<Patient> Patients =>
        new(_patientState.Patients);

    public bool HasMultiplePatients => _patientState.Patients.Count > 1;

    public Patient? SelectedPatient
    {
        get => _patientState.SelectedPatient;
        set => _patientState.SelectedPatient = value;
    }

    // Same show/hide preferences just set on the previous screen.
    [ObservableProperty] private bool _showHeartRate = true;
    [ObservableProperty] private bool _showSpo2 = true;
    [ObservableProperty] private bool _showTemperature = true;
    [ObservableProperty] private bool _showWeight = false;
    [ObservableProperty] private bool _showGlucose = false;

    [ObservableProperty] private string _systolic = string.Empty;
    [ObservableProperty] private string _diastolic = string.Empty;
    [ObservableProperty] private string _heartRate = string.Empty;
    [ObservableProperty] private string _oxygenSaturation = string.Empty;
    [ObservableProperty] private string _temperature = string.Empty;
    [ObservableProperty] private string _bloodGlucose = string.Empty;
    [ObservableProperty] private string _weight = string.Empty;
    [ObservableProperty] private string _notes = string.Empty;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public OnboardingFirstVitalReadingViewModel(ApiService api, PatientStateService patientState)
    {
        _api = api;
        _patientState = patientState;
    }

    /// <summary>
    /// Loads the preferences just set on the Vital Preferences screen, and
    /// initializes PatientStateService — this is the first point in
    /// onboarding where patient data actually gets fetched, picking up the
    /// patient(s) just created in OnboardingPatientSetupViewModel.
    ///
    /// Explicitly resets PatientStateService first rather than trusting it's
    /// already clean. It's a Singleton — if it still held patients cached
    /// from an earlier session in the same running app process (e.g. testing
    /// a different account without a full app restart in between),
    /// InitializeAsync()'s "if (Patients.Any()) return" guard would skip
    /// fetching entirely and leave a stale, wrong patient selected. Onboarding
    /// always represents a brand-new household context, so it must never
    /// depend on a reset having already happened correctly somewhere earlier
    /// in the chain — it forces its own clean slate.
    /// </summary>
    public async Task LoadAsync()
    {
        ShowHeartRate = Preferences.Get("show_heart_rate", true);
        ShowSpo2 = Preferences.Get("show_spo2", true);
        ShowTemperature = Preferences.Get("show_temperature", true);
        ShowWeight = Preferences.Get("show_weight", false);
        ShowGlucose = Preferences.Get("show_glucose", false);

        _patientState.Reset();
        await _patientState.InitializeAsync();
        OnPropertyChanged(nameof(Patients));
        OnPropertyChanged(nameof(HasMultiplePatients));
        OnPropertyChanged(nameof(SelectedPatient));
    }

    [RelayCommand]
    public async Task SubmitAsync()
    {
        if (SelectedPatient is null)
        {
            StatusMessage = "Please select who this reading is for.";
            return;
        }

        var heartRate = ShowHeartRate ? HeartRate : string.Empty;
        var oxygenSaturation = ShowSpo2 ? OxygenSaturation : string.Empty;
        var temperature = ShowTemperature ? Temperature : string.Empty;
        var bloodGlucose = ShowGlucose ? BloodGlucose : string.Empty;
        var weight = ShowWeight ? Weight : string.Empty;

        if (string.IsNullOrWhiteSpace(Systolic) &&
            string.IsNullOrWhiteSpace(Diastolic) &&
            string.IsNullOrWhiteSpace(heartRate) &&
            string.IsNullOrWhiteSpace(oxygenSaturation) &&
            string.IsNullOrWhiteSpace(temperature) &&
            string.IsNullOrWhiteSpace(bloodGlucose) &&
            string.IsNullOrWhiteSpace(weight))
        {
            StatusMessage = "Enter at least one reading, or tap Skip.";
            return;
        }

        IsBusy = true;
        StatusMessage = string.Empty;

        try
        {
            var entry = new VitalEntry
            {
                PatientId = SelectedPatient.PatientId,
                RecordedAt = null,
                Systolic = TryParseInt(Systolic),
                Diastolic = TryParseInt(Diastolic),
                HeartRate = TryParseInt(heartRate),
                OxygenSaturation = TryParseInt(oxygenSaturation),
                Temperature = TryParseDouble(temperature),
                BloodGlucose = TryParseInt(bloodGlucose),
                Weight = TryParseDouble(weight),
                Notes = Notes
            };

            var success = await _api.RecordVitalsAsync(entry);
            if (success)
            {
                FinishOnboarding();
            }
            else
            {
                StatusMessage = "Something went wrong. Please try again, or tap Skip.";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void Skip()
    {
        FinishOnboarding();
    }

    [RelayCommand]
    public void Back()
    {
        // Intentionally not wired to Navigation.PopAsync by the page —
        // going "back" from the final onboarding step would return to
        // Vital Preferences, which is fine, so this stays a normal pop.
        OnBack?.Invoke();
    }

    public Action? OnBack { get; set; }

    private void FinishOnboarding()
    {
        Preferences.Set("onboarding_complete", true);
        AppNavigation.SetRootPage(new Vitals.Maui.AppShell(_patientState));
    }

    private static int? TryParseInt(string val) =>
        int.TryParse(val, out var result) ? result : null;

    private static double? TryParseDouble(string val) =>
        double.TryParse(val, out var result) ? result : null;
}
