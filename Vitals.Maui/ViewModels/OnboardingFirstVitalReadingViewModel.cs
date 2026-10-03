using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vitals.Maui.Models;
using Vitals.Maui.Services;

namespace Vitals.Maui.ViewModels;

public partial class OnboardingFirstVitalReadingViewModel : ObservableObject
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

    // Onboarding uses the same entry contract and context controls as the
    // main Record Vitals screen, while retaining its patient-picker and
    // finish/skip behavior.
    public System.Collections.ObjectModel.ObservableCollection<Patient> Patients =>
        new(_patientState.Patients);

    public bool HasMultiplePatients => _patientState.Patients.Count > 1;

    public Patient? SelectedPatient
    {
        get => _patientState.SelectedPatient;
        set => _patientState.SelectedPatient = value;
    }

    public OnboardingFirstVitalReadingViewModel(
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
                OnPropertyChanged(nameof(HasMultiplePatients));
            }
        };
    }

    /// <summary>
    /// Reloads the current household's patients for the final onboarding
    /// step, preserving the patient selected/created earlier in onboarding
    /// when possible, then hydrates that patient's persisted vital settings.
    /// </summary>
    public async Task LoadAsync()
    {
        var intendedPatientId = _patientState.SelectedPatient?.PatientId;

        _patientState.Reset();
        await _patientState.InitializeAsync();

        if (!string.IsNullOrWhiteSpace(intendedPatientId))
        {
            var intended = _patientState.Patients
                .FirstOrDefault(p => p.PatientId == intendedPatientId);
            if (intended is not null)
                _patientState.SelectedPatient = intended;
        }

        OnPropertyChanged(nameof(Patients));
        OnPropertyChanged(nameof(HasMultiplePatients));
        OnPropertyChanged(nameof(SelectedPatient));
        await LoadSelectedPatientPreferencesAsync();
    }

    private async Task LoadSelectedPatientPreferencesAsync()
    {
        var patientId = _patientState.SelectedPatient?.PatientId;
        if (string.IsNullOrWhiteSpace(patientId))
            return;

        var preferences = await _preferences.RefreshAsync(patientId);

        if (_patientState.SelectedPatient?.PatientId != patientId)
            return;

        ShowHeartRate = preferences.ShowHeartRate;
        ShowSpo2 = preferences.ShowSpo2;
        ShowTemperature = preferences.ShowTemperature;
        ShowWeight = preferences.ShowWeight;
        ShowGlucose = preferences.ShowGlucose;
    }

    [RelayCommand]
    public async Task SubmitVitalsAsync()
    {
        if (SelectedPatient is null)
        {
            StatusMessage = "Please select who this reading is for.";
            return;
        }

        // Match the main Record Vitals behavior: hidden optional vitals are
        // neither validated nor submitted.
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
            StatusMessage = "Enter at least one reading, or tap Skip.";
            return;
        }

        IsBusy = true;
        IsSuccess = false;
        StatusMessage = string.Empty;

        try
        {
            var entry = new VitalEntry
            {
                PatientId = SelectedPatient.PatientId,
                RecordedAt = null,
                LocalOffsetMinutes = (int)DateTimeOffset.Now.Offset.TotalMinutes,
                Systolic = TryParseInt(systolic),
                Diastolic = TryParseInt(diastolic),
                HeartRate = TryParseInt(heartRate),
                OxygenSaturation = TryParseInt(oxygenSaturation),
                Temperature = TryParseDouble(temperature),
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
                HrActivityContext = string.IsNullOrWhiteSpace(heartRate)
                    ? null
                    : HrActivityContext,
                HrPosture = string.IsNullOrWhiteSpace(heartRate)
                    ? null
                    : HrPosture,
                HrSourceType = string.IsNullOrWhiteSpace(heartRate)
                    ? null
                    : "manual",
            };

            var success = await _api.RecordVitalsAsync(entry);
            if (success)
            {
                IsSuccess = true;
                StatusMessage = "Vitals recorded.";
                FinishOnboarding();
            }
            else
            {
                StatusMessage = "Something went wrong. Please try again, or tap Skip.";
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

    [RelayCommand]
    public void Skip() => FinishOnboarding();

    [RelayCommand]
    public void Back() => OnBack?.Invoke();

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
