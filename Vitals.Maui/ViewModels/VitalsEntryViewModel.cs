using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vitals.Maui.Models;
using Vitals.Maui.Services;

namespace Vitals.Maui.ViewModels;

public partial class VitalsEntryViewModel : ObservableObject
{
    private readonly ApiService _api;
    private readonly PatientStateService _patientState;

    [ObservableProperty]
    private string _systolic = string.Empty;

    [ObservableProperty]
    private string _diastolic = string.Empty;

    [ObservableProperty]
    private string _heartRate = string.Empty;

    [ObservableProperty]
    private string _oxygenSaturation = string.Empty;

    [ObservableProperty]
    private string _temperature = string.Empty;

    [ObservableProperty]
    private string _bloodGlucose = string.Empty;

    [ObservableProperty]
    private string _notes = string.Empty;

    [ObservableProperty]
    private string _weight = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isSuccess;

    // Field visibility, driven by the same Preferences keys SettingsViewModel
    // writes to. Systolic/Diastolic have no corresponding setting — blood
    // pressure is always shown — so there's no visibility flag for them.
    [ObservableProperty]
    private bool _showHeartRate = true;

    [ObservableProperty]
    private bool _showSpo2 = true;

    [ObservableProperty]
    private bool _showTemperature = true;

    [ObservableProperty]
    private bool _showWeight = false;

    [ObservableProperty]
    private bool _showGlucose = false;

    // Heart Rate Analysis Spec §4 — Activity Context and Posture. Nothing
    // selected (both null) means "unknown", same as any reading logged
    // before this UI existed — not an error state, just excluded from
    // resting-specific analysis until the user actually picks one.
    private static readonly Color SelectedColor = Color.FromArgb("#1976d2");
    private static readonly Color UnselectedColor = Color.FromArgb("#2a3a5c");

    [ObservableProperty] private string? _hrActivityContext;
    [ObservableProperty] private Color _restingColor = UnselectedColor;
    [ObservableProperty] private Color _postActivityColor = UnselectedColor;
    [ObservableProperty] private Color _duringActivityColor = UnselectedColor;

    [ObservableProperty] private string? _hrPosture;
    [ObservableProperty] private Color _seatedColor = UnselectedColor;
    [ObservableProperty] private Color _supineColor = UnselectedColor;
    [ObservableProperty] private Color _standingColor = UnselectedColor;

    [RelayCommand] private void SelectResting() => SetActivityContext("resting");
    [RelayCommand] private void SelectPostActivity() => SetActivityContext("post_activity");
    [RelayCommand] private void SelectDuringActivity() => SetActivityContext("during_activity");

    private void SetActivityContext(string value)
    {
        // Tapping the already-selected option clears it back to unknown,
        // rather than forcing a choice — this is optional context, not a
        // required field, and there's no "unknown" button to tap instead.
        HrActivityContext = HrActivityContext == value ? null : value;
        RestingColor = HrActivityContext == "resting" ? SelectedColor : UnselectedColor;
        PostActivityColor = HrActivityContext == "post_activity" ? SelectedColor : UnselectedColor;
        DuringActivityColor = HrActivityContext == "during_activity" ? SelectedColor : UnselectedColor;
    }

    [RelayCommand] private void SelectSeated() => SetPosture("seated");
    [RelayCommand] private void SelectSupine() => SetPosture("supine");
    [RelayCommand] private void SelectStanding() => SetPosture("standing");

    private void SetPosture(string value)
    {
        HrPosture = HrPosture == value ? null : value;
        SeatedColor = HrPosture == "seated" ? SelectedColor : UnselectedColor;
        SupineColor = HrPosture == "supine" ? SelectedColor : UnselectedColor;
        StandingColor = HrPosture == "standing" ? SelectedColor : UnselectedColor;
    }

    // Delegate to shared state
    public System.Collections.ObjectModel.ObservableCollection<Patient> Patients =>
        new(_patientState.Patients);

    public Patient? SelectedPatient
    {
        get => _patientState.SelectedPatient;
        set => _patientState.SelectedPatient = value;
    }

    public VitalsEntryViewModel(ApiService api, PatientStateService patientState)
    {
        _api = api;
        _patientState = patientState;

        _patientState.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(PatientStateService.SelectedPatient))
                OnPropertyChanged(nameof(SelectedPatient));
            else if (e.PropertyName == nameof(PatientStateService.Patients))
                OnPropertyChanged(nameof(Patients));
        };

        LoadDisplayPreferences();
    }

    /// <summary>
    /// Reads the same show/hide keys SettingsViewModel writes to. Called
    /// from the constructor and again from LoadAsync (i.e. on page
    /// appearing), since a Settings change made after this ViewModel was
    /// first constructed wouldn't otherwise be picked up if the page/VM
    /// instance is cached rather than recreated on navigation.
    /// </summary>
    private void LoadDisplayPreferences()
    {
        ShowHeartRate = Preferences.Get("show_heart_rate", true);
        ShowSpo2 = Preferences.Get("show_spo2", true);
        ShowTemperature = Preferences.Get("show_temperature", true);
        ShowWeight = Preferences.Get("show_weight", false);
        ShowGlucose = Preferences.Get("show_glucose", false);
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        LoadDisplayPreferences();
        await _patientState.InitializeAsync();
        OnPropertyChanged(nameof(Patients));
        OnPropertyChanged(nameof(SelectedPatient));
    }

    [RelayCommand]
    public async Task SubmitVitalsAsync()
    {
        if (_patientState.SelectedPatient is null)
        {
            StatusMessage = "Please select a patient.";
            return;
        }

        // Only require/submit fields that are actually visible. A hidden
        // field left with stale text (from before it was toggled off)
        // should never be treated as "entered" or sent to the API.
        var systolic = Systolic;
        var diastolic = Diastolic;
        var heartRate = ShowHeartRate ? HeartRate : string.Empty;
        var oxygenSaturation = ShowSpo2 ? OxygenSaturation : string.Empty;
        var temperature = ShowTemperature ? Temperature : string.Empty;
        var bloodGlucose = ShowGlucose ? BloodGlucose : string.Empty;
        var weight = ShowWeight ? Weight : string.Empty;

        if (string.IsNullOrWhiteSpace(systolic) &&
            string.IsNullOrWhiteSpace(diastolic) &&
            string.IsNullOrWhiteSpace(heartRate) &&
            string.IsNullOrWhiteSpace(oxygenSaturation) &&
            string.IsNullOrWhiteSpace(temperature) &&
            string.IsNullOrWhiteSpace(bloodGlucose) &&
            string.IsNullOrWhiteSpace(weight))
        {
            StatusMessage = "Please enter at least one vital.";
            return;
        }

        IsBusy = true;
        StatusMessage = string.Empty;
        IsSuccess = false;

        try
        {
            var entry = new VitalEntry
            {
                PatientId = _patientState.SelectedPatient.PatientId,
                RecordedAt = null,
                // The device's real UTC offset right now — e.g. -300 for
                // Central Daylight Time. Not conditional on any specific
                // vital being present; applies to the whole reading, and
                // every future vital's analysis benefits from real
                // per-reading local time, not just heart rate's.
                LocalOffsetMinutes = (int)DateTimeOffset.Now.Offset.TotalMinutes,
                Systolic = TryParseInt(systolic),
                Diastolic = TryParseInt(diastolic),
                HeartRate = TryParseInt(heartRate),
                OxygenSaturation = TryParseInt(oxygenSaturation),
                Temperature = TryParseDouble(temperature),
                BloodGlucose = TryParseInt(bloodGlucose),
                Weight = TryParseDouble(weight),
                Notes = Notes,
                // Only meaningful alongside an actual heart-rate value —
                // omitted entirely otherwise, rather than sending context
                // for a reading that doesn't exist.
                HrActivityContext = string.IsNullOrWhiteSpace(heartRate) ? null : HrActivityContext,
                HrPosture = string.IsNullOrWhiteSpace(heartRate) ? null : HrPosture,
                HrSourceType = string.IsNullOrWhiteSpace(heartRate) ? null : "manual",
            };

            var success = await _api.RecordVitalsAsync(entry);

            if (success)
            {
                IsSuccess = true;
                StatusMessage = "Vitals recorded.";
                ClearForm();
            }
            else
            {
                StatusMessage = "Something went wrong. Please try again.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ClearForm()
    {
        Systolic = string.Empty;
        Diastolic = string.Empty;
        HeartRate = string.Empty;
        OxygenSaturation = string.Empty;
        Temperature = string.Empty;
        BloodGlucose = string.Empty;
        Weight = string.Empty;
        Notes = string.Empty;
        HrActivityContext = null;
        RestingColor = UnselectedColor;
        PostActivityColor = UnselectedColor;
        DuringActivityColor = UnselectedColor;
        HrPosture = null;
        SeatedColor = UnselectedColor;
        SupineColor = UnselectedColor;
        StandingColor = UnselectedColor;
    }

    private static int? TryParseInt(string val) =>
        int.TryParse(val, out var result) ? result : null;

    private static double? TryParseDouble(string val) =>
        double.TryParse(val, out var result) ? result : null;
}