using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Globalization;
using Vitals.Maui.Models;
using Vitals.Maui.Services;

namespace Vitals.Maui.ViewModels;

public partial class VitalHistoryDetailViewModel : ObservableObject
{
    private readonly ApiService _api;
    private readonly UserPreferencesService _preferences;
    private VitalRecord? _original;

    [ObservableProperty] private ObservableCollection<Patient> _patients = new();
    [ObservableProperty] private Patient? _selectedPatient;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;

    [ObservableProperty] private DateTime _recordedDate = DateTime.Today;
    [ObservableProperty] private TimeSpan _recordedTime = DateTime.Now.TimeOfDay;

    [ObservableProperty] private string _systolic = string.Empty;
    [ObservableProperty] private string _diastolic = string.Empty;
    [ObservableProperty] private string _heartRate = string.Empty;
    [ObservableProperty] private string _oxygenSaturation = string.Empty;
    [ObservableProperty] private string _temperature = string.Empty;
    [ObservableProperty] private string _weight = string.Empty;
    [ObservableProperty] private string _bloodGlucose = string.Empty;
    [ObservableProperty] private string _glucoseMinutesAfterMeal = string.Empty;
    [ObservableProperty] private string _notes = string.Empty;

    [ObservableProperty] private bool _showHeartRate = true;
    [ObservableProperty] private bool _showSpo2 = true;
    [ObservableProperty] private bool _showTemperature = true;
    [ObservableProperty] private bool _showWeight;
    [ObservableProperty] private bool _showGlucose;

    [ObservableProperty] private string _selectedHrActivity = "Unknown";
    [ObservableProperty] private string _selectedHrPosture = "Unknown";
    [ObservableProperty] private string _selectedTemperatureSite = "Unknown";
    [ObservableProperty] private string _selectedGlucoseContext = "Unknown";
    [ObservableProperty] private string _selectedGlucoseMealType = "None";
    [ObservableProperty] private bool _showGlucoseMealOptions;
    [ObservableProperty] private bool _showGlucosePostMealMinutes;

    public IReadOnlyList<string> HrActivityOptions { get; } =
        new[] { "Unknown", "Resting", "Post activity", "During activity" };

    public IReadOnlyList<string> HrPostureOptions { get; } =
        new[] { "Unknown", "Seated", "Supine", "Standing" };

    public IReadOnlyList<string> TemperatureSiteOptions { get; } =
        new[] { "Unknown", "Oral", "Temporal", "Tympanic", "Axillary", "Rectal", "Other" };

    public IReadOnlyList<string> GlucoseContextOptions { get; } =
        new[] { "Unknown", "Fasting", "Pre meal", "Post meal", "Bedtime", "Random", "Other" };

    public IReadOnlyList<string> GlucoseMealTypeOptions { get; } =
        new[] { "None", "Breakfast", "Lunch", "Dinner", "Snack", "Other" };

    public bool IsReassigned =>
        _original is not null &&
        SelectedPatient is not null &&
        !string.Equals(
            _original.PatientId,
            SelectedPatient.PatientId,
            StringComparison.OrdinalIgnoreCase);

    public string ReassignmentMessage =>
        IsReassigned && SelectedPatient is not null
            ? $"This will move the entire measurement entry to {SelectedPatient.FullName}. Hidden stored values are preserved."
            : string.Empty;

    public Action? OnSaved { get; set; }
    public Action? OnCancelled { get; set; }

    public VitalHistoryDetailViewModel(
        ApiService api,
        UserPreferencesService preferences)
    {
        _api = api;
        _preferences = preferences;
    }

    partial void OnSelectedPatientChanged(Patient? value)
    {
        OnPropertyChanged(nameof(IsReassigned));
        OnPropertyChanged(nameof(ReassignmentMessage));

        if (value is not null)
            _ = LoadPreferencesAsync(value.PatientId);
    }

    partial void OnSelectedGlucoseContextChanged(string value)
    {
        ShowGlucoseMealOptions = value is "Pre meal" or "Post meal";
        ShowGlucosePostMealMinutes = value == "Post meal";

        if (!ShowGlucoseMealOptions)
            SelectedGlucoseMealType = "None";

        if (!ShowGlucosePostMealMinutes)
            GlucoseMinutesAfterMeal = string.Empty;
    }

    public async Task InitializeAsync(string vitalId)
    {
        IsBusy = true;
        StatusMessage = string.Empty;

        try
        {
            _original = await _api.GetVitalRecordAsync(vitalId);
            if (_original is null)
            {
                StatusMessage = "Could not load this vital record.";
                return;
            }

            var patients = await _api.GetPatientsAsync();
            Patients = new ObservableCollection<Patient>(patients);

            LoadFromRecord(_original);

            SelectedPatient = Patients.FirstOrDefault(
                p => string.Equals(
                    p.PatientId,
                    _original.PatientId,
                    StringComparison.OrdinalIgnoreCase));

            if (SelectedPatient is null)
            {
                StatusMessage = "The patient assigned to this record is no longer available.";
                return;
            }

            await LoadPreferencesAsync(SelectedPatient.PatientId);
            OnPropertyChanged(nameof(IsReassigned));
            OnPropertyChanged(nameof(ReassignmentMessage));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not load record: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void LoadFromRecord(VitalRecord record)
    {
        var local = record.RecordedAt?.ToLocalTime() ?? DateTime.Now;
        RecordedDate = local.Date;
        RecordedTime = local.TimeOfDay;

        Systolic = Format(record.Systolic);
        Diastolic = Format(record.Diastolic);
        HeartRate = Format(record.HeartRate);
        OxygenSaturation = Format(record.OxygenSaturation);
        Temperature = Format(record.Temperature);
        Weight = Format(record.Weight);
        BloodGlucose = Format(record.BloodGlucose);
        GlucoseMinutesAfterMeal = Format(record.GlucoseMinutesAfterMeal);
        Notes = record.Notes ?? string.Empty;

        SelectedHrActivity = record.HrActivityContext switch
        {
            "resting" => "Resting",
            "post_activity" => "Post activity",
            "during_activity" => "During activity",
            _ => "Unknown"
        };

        SelectedHrPosture = record.HrPosture switch
        {
            "seated" => "Seated",
            "supine" => "Supine",
            "standing" => "Standing",
            _ => "Unknown"
        };

        SelectedTemperatureSite = record.TemperatureSite switch
        {
            "oral" => "Oral",
            "temporal" => "Temporal",
            "tympanic" => "Tympanic",
            "axillary" => "Axillary",
            "rectal" => "Rectal",
            "other" => "Other",
            _ => "Unknown"
        };

        SelectedGlucoseContext = record.GlucoseContext switch
        {
            "fasting" => "Fasting",
            "pre_meal" => "Pre meal",
            "post_meal" => "Post meal",
            "bedtime" => "Bedtime",
            "random" => "Random",
            "other" => "Other",
            _ => "Unknown"
        };

        SelectedGlucoseMealType = record.GlucoseMealType switch
        {
            "breakfast" => "Breakfast",
            "lunch" => "Lunch",
            "dinner" => "Dinner",
            "snack" => "Snack",
            "other" => "Other",
            _ => "None"
        };
    }

    private async Task LoadPreferencesAsync(string patientId)
    {
        var preferences = await _preferences.RefreshAsync(patientId);

        // Ignore a stale preference request if the picker changed again while
        // the network request was in flight.
        if (!string.Equals(
                SelectedPatient?.PatientId,
                patientId,
                StringComparison.OrdinalIgnoreCase))
            return;

        ShowHeartRate = preferences.ShowHeartRate;
        ShowSpo2 = preferences.ShowSpo2;
        ShowTemperature = preferences.ShowTemperature;
        ShowWeight = preferences.ShowWeight;
        ShowGlucose = preferences.ShowGlucose;
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        if (_original is null || SelectedPatient is null)
        {
            StatusMessage = "This record is not ready to save.";
            return;
        }

        if (!TryOptionalInt(Systolic, 50, 250, "Systolic", out var systolic) ||
            !TryOptionalInt(Diastolic, 30, 150, "Diastolic", out var diastolic) ||
            !TryOptionalInt(HeartRate, 30, 220, "Heart rate", out var heartRate) ||
            !TryOptionalInt(OxygenSaturation, 50, 100, "SpO₂", out var spo2) ||
            !TryOptionalDouble(Temperature, 90, 110, "Temperature", out var temperature) ||
            !TryOptionalDouble(Weight, 50, 700, "Weight", out var weight) ||
            !TryOptionalInt(BloodGlucose, 30, 600, "Glucose", out var glucose))
        {
            return;
        }

        if (systolic.HasValue != diastolic.HasValue)
        {
            StatusMessage = "Blood pressure needs both systolic and diastolic values, or neither.";
            return;
        }

        int? glucoseMinutes = null;
        if (glucose.HasValue && SelectedGlucoseContext == "Post meal")
        {
            if (!TryOptionalInt(
                    GlucoseMinutesAfterMeal,
                    0,
                    720,
                    "Minutes after meal",
                    out glucoseMinutes))
                return;
        }

        if (systolic is null &&
            heartRate is null &&
            spo2 is null &&
            temperature is null &&
            weight is null &&
            glucose is null)
        {
            StatusMessage = "A history record must contain at least one vital. Use Delete if you want to remove the entry.";
            return;
        }

        if (IsReassigned)
        {
            var confirmed = await Shell.Current.DisplayAlert(
                "Reassign Vital Record",
                $"Move this entire entry to {SelectedPatient.FullName}? All values and context stored with the entry will move together.",
                "Move",
                "Cancel");

            if (!confirmed)
                return;
        }

        var localDateTime = DateTime.SpecifyKind(
            RecordedDate.Date + RecordedTime,
            DateTimeKind.Unspecified);
        var offset = TimeZoneInfo.Local.GetUtcOffset(localDateTime);
        var recordedAt = new DateTimeOffset(localDateTime, offset);

        var glucoseContext = glucose.HasValue
            ? GlucoseContextToApi(SelectedGlucoseContext)
            : null;

        var entry = new VitalEntry
        {
            PatientId = SelectedPatient.PatientId,
            RecordedAt = recordedAt.UtcDateTime,
            LocalOffsetMinutes = (int)offset.TotalMinutes,
            Systolic = systolic,
            Diastolic = diastolic,
            HeartRate = heartRate,
            OxygenSaturation = spo2,
            Temperature = temperature,
            TemperatureSite = temperature.HasValue
                ? TemperatureSiteToApi(SelectedTemperatureSite)
                : null,
            Weight = weight,
            BloodGlucose = glucose,
            GlucoseContext = glucoseContext,
            GlucoseMealType = glucose.HasValue &&
                              glucoseContext is "pre_meal" or "post_meal"
                ? GlucoseMealTypeToApi(SelectedGlucoseMealType)
                : null,
            GlucoseMinutesAfterMeal = glucoseMinutes,
            GlucoseMealEventId = _original.GlucoseMealEventId,
            GlucoseSourceType = glucose.HasValue
                ? _original.GlucoseSourceType ?? "manual_bgm"
                : null,
            GlucoseOriginalValue = glucose.HasValue
                ? (_original.GlucoseSourceType is null or "manual_bgm"
                    ? glucose.Value
                    : _original.GlucoseOriginalValue)
                : null,
            GlucoseOriginalUnit = glucose.HasValue
                ? _original.GlucoseOriginalUnit ?? "mg/dL"
                : null,
            GlucoseSourceDevice = glucose.HasValue
                ? _original.GlucoseSourceDevice
                : null,
            Source = _original.Source,
            Notes = Notes.Trim(),
            HrActivityContext = heartRate.HasValue
                ? HrActivityToApi(SelectedHrActivity)
                : null,
            HrPosture = heartRate.HasValue
                ? HrPostureToApi(SelectedHrPosture)
                : null,
            HrSourceType = heartRate.HasValue
                ? _original.HrSourceType ?? "manual"
                : null,
        };

        IsBusy = true;
        StatusMessage = string.Empty;

        try
        {
            var success = await _api.UpdateVitalRecordAsync(
                _original.VitalId,
                entry);

            if (success)
            {
                OnSaved?.Invoke();
            }
            else
            {
                StatusMessage = "Could not update this vital record.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not update record: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task DeleteAsync()
    {
        if (_original is null)
            return;

        var patientName = Patients.FirstOrDefault(
            p => string.Equals(
                p.PatientId,
                _original.PatientId,
                StringComparison.OrdinalIgnoreCase))?.FullName ?? "this patient";

        var confirmed = await Shell.Current.DisplayAlert(
            "Delete Vital Record",
            $"Permanently delete this entire vital entry for {patientName}? This removes every vital and context value stored in this entry.",
            "Delete",
            "Cancel");

        if (!confirmed)
            return;

        IsBusy = true;
        StatusMessage = string.Empty;

        try
        {
            var success = await _api.DeleteVitalRecordAsync(_original.VitalId);
            if (success)
            {
                OnSaved?.Invoke();
            }
            else
            {
                StatusMessage = "Could not delete this vital record.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not delete record: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => OnCancelled?.Invoke();

    private bool TryOptionalInt(
        string text,
        int min,
        int max,
        string label,
        out int? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
            return true;

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var parsed) ||
            parsed < min ||
            parsed > max)
        {
            StatusMessage = $"{label} must be between {min} and {max}.";
            return false;
        }

        value = parsed;
        return true;
    }

    private bool TryOptionalDouble(
        string text,
        double min,
        double max,
        string label,
        out double? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
            return true;

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed) ||
            parsed < min ||
            parsed > max)
        {
            StatusMessage = $"{label} must be between {min} and {max}.";
            return false;
        }

        value = parsed;
        return true;
    }

    private static string Format(int? value) =>
        value?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;

    private static string Format(double? value) =>
        value?.ToString("0.###", CultureInfo.CurrentCulture) ?? string.Empty;

    private static string? HrActivityToApi(string value) => value switch
    {
        "Resting" => "resting",
        "Post activity" => "post_activity",
        "During activity" => "during_activity",
        _ => null
    };

    private static string? HrPostureToApi(string value) => value switch
    {
        "Seated" => "seated",
        "Supine" => "supine",
        "Standing" => "standing",
        _ => null
    };

    private static string TemperatureSiteToApi(string value) => value switch
    {
        "Oral" => "oral",
        "Temporal" => "temporal",
        "Tympanic" => "tympanic",
        "Axillary" => "axillary",
        "Rectal" => "rectal",
        "Other" => "other",
        _ => "unknown"
    };

    private static string GlucoseContextToApi(string value) => value switch
    {
        "Fasting" => "fasting",
        "Pre meal" => "pre_meal",
        "Post meal" => "post_meal",
        "Bedtime" => "bedtime",
        "Random" => "random",
        "Other" => "other",
        _ => "unknown"
    };

    private static string? GlucoseMealTypeToApi(string value) => value switch
    {
        "Breakfast" => "breakfast",
        "Lunch" => "lunch",
        "Dinner" => "dinner",
        "Snack" => "snack",
        "Other" => "other",
        _ => null
    };
}
