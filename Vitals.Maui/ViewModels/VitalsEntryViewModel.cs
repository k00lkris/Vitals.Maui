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
                Systolic = TryParseInt(systolic),
                Diastolic = TryParseInt(diastolic),
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
    }

    private static int? TryParseInt(string val) =>
        int.TryParse(val, out var result) ? result : null;

    private static double? TryParseDouble(string val) =>
        double.TryParse(val, out var result) ? result : null;
}