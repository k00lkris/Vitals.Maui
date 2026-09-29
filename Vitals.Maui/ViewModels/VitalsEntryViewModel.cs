using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vitals.Maui.Models;
using Vitals.Maui.Services;

namespace Vitals.Maui.ViewModels;

public partial class VitalsEntryViewModel : ObservableObject
{
    private readonly ApiService _api;
    private readonly PatientStateService _patientState;
    private readonly VitalPreferencesService _vitalPreferences;

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

    // Field visibility is shared account state. Blood pressure has no toggle
    // and remains visible by design.
    public bool ShowHeartRate => _vitalPreferences.ShowHeartRate;
    public bool ShowSpo2 => _vitalPreferences.ShowSpo2;
    public bool ShowTemperature => _vitalPreferences.ShowTemperature;
    public bool ShowWeight => _vitalPreferences.ShowWeight;
    public bool ShowGlucose => _vitalPreferences.ShowGlucose;

    // Shared selection fill for the optional context controls below.
    // Selected uses a translucent blue fill so the active choice is much
    // easier to see than a border-only state while remaining readable in
    // both light and dark themes. Unselected stays transparent and relies
    // on the XAML card stroke for its outline.
    private static readonly Color SelectedColor = Color.FromArgb("#661976D2");
    private static readonly Color UnselectedColor = Colors.Transparent;

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

    // Temperature Analysis Spec — measurement site is optional at entry
    // time, but known same-site readings are what allow Vitals to build a
    // personal baseline and make site-sensitive comparisons. As with the
    // HR context selectors, tapping the selected option again clears it
    // back to unknown rather than forcing a choice.
    [ObservableProperty] private string? _temperatureSite;
    [ObservableProperty] private Color _tempOralColor = UnselectedColor;
    [ObservableProperty] private Color _tempTemporalColor = UnselectedColor;
    [ObservableProperty] private Color _tempTympanicColor = UnselectedColor;
    [ObservableProperty] private Color _tempAxillaryColor = UnselectedColor;
    [ObservableProperty] private Color _tempRectalColor = UnselectedColor;
    [ObservableProperty] private Color _tempOtherColor = UnselectedColor;

    [RelayCommand] private void SelectTempOral() => SetTemperatureSite("oral");
    [RelayCommand] private void SelectTempTemporal() => SetTemperatureSite("temporal");
    [RelayCommand] private void SelectTempTympanic() => SetTemperatureSite("tympanic");
    [RelayCommand] private void SelectTempAxillary() => SetTemperatureSite("axillary");
    [RelayCommand] private void SelectTempRectal() => SetTemperatureSite("rectal");
    [RelayCommand] private void SelectTempOther() => SetTemperatureSite("other");

    private void SetTemperatureSite(string value)
    {
        TemperatureSite = TemperatureSite == value ? null : value;

        TempOralColor = TemperatureSite == "oral" ? SelectedColor : UnselectedColor;
        TempTemporalColor = TemperatureSite == "temporal" ? SelectedColor : UnselectedColor;
        TempTympanicColor = TemperatureSite == "tympanic" ? SelectedColor : UnselectedColor;
        TempAxillaryColor = TemperatureSite == "axillary" ? SelectedColor : UnselectedColor;
        TempRectalColor = TemperatureSite == "rectal" ? SelectedColor : UnselectedColor;
        TempOtherColor = TemperatureSite == "other" ? SelectedColor : UnselectedColor;
    }

    // Delegate to shared state
    public System.Collections.ObjectModel.ObservableCollection<Patient> Patients =>
        new(_patientState.Patients);

    public Patient? SelectedPatient
    {
        get => _patientState.SelectedPatient;
        set => _patientState.SelectedPatient = value;
    }

    public VitalsEntryViewModel(
        ApiService api,
        PatientStateService patientState,
        VitalPreferencesService vitalPreferences)
    {
        _api = api;
        _patientState = patientState;
        _vitalPreferences = vitalPreferences;

        _patientState.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(PatientStateService.SelectedPatient))
                OnPropertyChanged(nameof(SelectedPatient));
            else if (e.PropertyName == nameof(PatientStateService.Patients))
                OnPropertyChanged(nameof(Patients));
        };

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
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        await _vitalPreferences.LoadAsync();
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
                // Temperature site is meaningful only when an actual
                // temperature was entered. If the user leaves the optional
                // selector untouched, send "unknown" explicitly so the
                // stored reading accurately reflects missing site context.
                TemperatureSite = string.IsNullOrWhiteSpace(temperature)
                    ? null
                    : TemperatureSite ?? "unknown",
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
        TemperatureSite = null;
        TempOralColor = UnselectedColor;
        TempTemporalColor = UnselectedColor;
        TempTympanicColor = UnselectedColor;
        TempAxillaryColor = UnselectedColor;
        TempRectalColor = UnselectedColor;
        TempOtherColor = UnselectedColor;
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