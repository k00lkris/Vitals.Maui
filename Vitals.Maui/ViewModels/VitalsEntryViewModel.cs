using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vitals.Maui.Models;
using Vitals.Maui.Services;

namespace Vitals.Maui.ViewModels;

public partial class VitalsEntryViewModel : ObservableObject
{
    private readonly ApiService _api;
    private readonly PatientStateService _patientState;
    private readonly UserPreferencesService _preferences;

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

    // Patient-recorded glucose context. Context is optional so a reading can
    // still be saved when the user does not know or does not want to record
    // it; the backend stores "unknown" and capability-gates analyses that
    // require comparable fasting/meal state.
    [ObservableProperty] private string? _glucoseContext;
    [ObservableProperty] private string? _glucoseMealType;
    [ObservableProperty] private string _glucoseMinutesAfterMeal = string.Empty;
    [ObservableProperty] private bool _showGlucoseMealOptions;
    [ObservableProperty] private bool _showGlucosePostMealMinutes;

    [ObservableProperty] private Color _glucoseFastingColor = UnselectedColor;
    [ObservableProperty] private Color _glucosePreMealColor = UnselectedColor;
    [ObservableProperty] private Color _glucosePostMealColor = UnselectedColor;
    [ObservableProperty] private Color _glucoseBedtimeColor = UnselectedColor;
    [ObservableProperty] private Color _glucoseRandomColor = UnselectedColor;
    [ObservableProperty] private Color _glucoseOtherColor = UnselectedColor;

    [ObservableProperty] private Color _glucoseBreakfastColor = UnselectedColor;
    [ObservableProperty] private Color _glucoseLunchColor = UnselectedColor;
    [ObservableProperty] private Color _glucoseDinnerColor = UnselectedColor;
    [ObservableProperty] private Color _glucoseSnackColor = UnselectedColor;

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

    [RelayCommand] private void SelectGlucoseFasting() => SetGlucoseContext("fasting");
    [RelayCommand] private void SelectGlucosePreMeal() => SetGlucoseContext("pre_meal");
    [RelayCommand] private void SelectGlucosePostMeal() => SetGlucoseContext("post_meal");
    [RelayCommand] private void SelectGlucoseBedtime() => SetGlucoseContext("bedtime");
    [RelayCommand] private void SelectGlucoseRandom() => SetGlucoseContext("random");
    [RelayCommand] private void SelectGlucoseOther() => SetGlucoseContext("other");

    private void SetGlucoseContext(string value)
    {
        GlucoseContext = GlucoseContext == value ? null : value;

        GlucoseFastingColor = GlucoseContext == "fasting" ? SelectedColor : UnselectedColor;
        GlucosePreMealColor = GlucoseContext == "pre_meal" ? SelectedColor : UnselectedColor;
        GlucosePostMealColor = GlucoseContext == "post_meal" ? SelectedColor : UnselectedColor;
        GlucoseBedtimeColor = GlucoseContext == "bedtime" ? SelectedColor : UnselectedColor;
        GlucoseRandomColor = GlucoseContext == "random" ? SelectedColor : UnselectedColor;
        GlucoseOtherColor = GlucoseContext == "other" ? SelectedColor : UnselectedColor;

        ShowGlucoseMealOptions = GlucoseContext is "pre_meal" or "post_meal";
        ShowGlucosePostMealMinutes = GlucoseContext == "post_meal";

        if (!ShowGlucoseMealOptions)
            SetGlucoseMealType(null);

        if (!ShowGlucosePostMealMinutes)
            GlucoseMinutesAfterMeal = string.Empty;
    }

    [RelayCommand] private void SelectGlucoseBreakfast() => ToggleGlucoseMealType("breakfast");
    [RelayCommand] private void SelectGlucoseLunch() => ToggleGlucoseMealType("lunch");
    [RelayCommand] private void SelectGlucoseDinner() => ToggleGlucoseMealType("dinner");
    [RelayCommand] private void SelectGlucoseSnack() => ToggleGlucoseMealType("snack");

    private void ToggleGlucoseMealType(string value) =>
        SetGlucoseMealType(GlucoseMealType == value ? null : value);

    private void SetGlucoseMealType(string? value)
    {
        GlucoseMealType = value;
        GlucoseBreakfastColor = value == "breakfast" ? SelectedColor : UnselectedColor;
        GlucoseLunchColor = value == "lunch" ? SelectedColor : UnselectedColor;
        GlucoseDinnerColor = value == "dinner" ? SelectedColor : UnselectedColor;
        GlucoseSnackColor = value == "snack" ? SelectedColor : UnselectedColor;
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
        UserPreferencesService preferences)
    {
        _api = api;
        _patientState = patientState;
        _preferences = preferences;

        _patientState.PropertyChanged += async (s, e) =>
        {
            if (e.PropertyName == nameof(PatientStateService.SelectedPatient))
            {
                OnPropertyChanged(nameof(SelectedPatient));
                await LoadSelectedPatientPreferencesAsync();
            }
            else if (e.PropertyName == nameof(PatientStateService.Patients))
            {
                OnPropertyChanged(nameof(Patients));
            }
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
        ApplyPreferences(
            _preferences.LocalSnapshot(_patientState.SelectedPatient?.PatientId));
    }

    private async Task LoadSelectedPatientPreferencesAsync()
    {
        var patientId = _patientState.SelectedPatient?.PatientId;
        if (string.IsNullOrWhiteSpace(patientId))
            return;

        var preferences = await _preferences.RefreshAsync(patientId);

        if (_patientState.SelectedPatient?.PatientId != patientId)
            return;

        ApplyPreferences(preferences);
    }

    private void ApplyPreferences(UserPreferences preferences)
    {
        ShowHeartRate = preferences.ShowHeartRate;
        ShowSpo2 = preferences.ShowSpo2;
        ShowTemperature = preferences.ShowTemperature;
        ShowWeight = preferences.ShowWeight;
        ShowGlucose = preferences.ShowGlucose;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        await _patientState.InitializeAsync();
        OnPropertyChanged(nameof(Patients));
        OnPropertyChanged(nameof(SelectedPatient));
        await LoadSelectedPatientPreferencesAsync();
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
                GlucoseContext = string.IsNullOrWhiteSpace(bloodGlucose)
                    ? null
                    : GlucoseContext ?? "unknown",
                GlucoseMealType = string.IsNullOrWhiteSpace(bloodGlucose)
                    || GlucoseContext is not ("pre_meal" or "post_meal")
                    ? null
                    : GlucoseMealType,
                GlucoseMinutesAfterMeal = string.IsNullOrWhiteSpace(bloodGlucose)
                    || GlucoseContext != "post_meal"
                    ? null
                    : TryParseInt(GlucoseMinutesAfterMeal),
                // Current Vitals Entry is a user-entered spot-reading workflow.
                // Dense CGM/import sources will populate a different source_type
                // when those integrations are added.
                GlucoseSourceType = string.IsNullOrWhiteSpace(bloodGlucose)
                    ? null
                    : "manual_bgm",
                GlucoseOriginalValue = string.IsNullOrWhiteSpace(bloodGlucose)
                    ? null
                    : TryParseInt(bloodGlucose),
                GlucoseOriginalUnit = string.IsNullOrWhiteSpace(bloodGlucose)
                    ? null
                    : "mg/dL",
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
        GlucoseContext = null;
        GlucoseMealType = null;
        GlucoseMinutesAfterMeal = string.Empty;
        ShowGlucoseMealOptions = false;
        ShowGlucosePostMealMinutes = false;
        GlucoseFastingColor = UnselectedColor;
        GlucosePreMealColor = UnselectedColor;
        GlucosePostMealColor = UnselectedColor;
        GlucoseBedtimeColor = UnselectedColor;
        GlucoseRandomColor = UnselectedColor;
        GlucoseOtherColor = UnselectedColor;
        GlucoseBreakfastColor = UnselectedColor;
        GlucoseLunchColor = UnselectedColor;
        GlucoseDinnerColor = UnselectedColor;
        GlucoseSnackColor = UnselectedColor;
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