using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Vitals.Maui.Models;
using Vitals.Maui.Services;

namespace Vitals.Maui.ViewModels;

public partial class VitalsEntryViewModel : ObservableObject
{
    private readonly ApiService _api;


    [ObservableProperty]
    private ObservableCollection<Patient> _patients = new();

    [ObservableProperty]
    private Patient? _selectedPatient;

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
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isSuccess;

    public VitalsEntryViewModel(ApiService api)
    {
        _api = api;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsBusy = true;
        StatusMessage = string.Empty;

        try
        {
            var patientList = await _api.GetPatientsAsync();
            Patients = new ObservableCollection<Patient>(patientList);
            // Small delay to let the Picker register the ItemsSource
            await Task.Delay(100);

            var lastId = Preferences.Get("last_patient_id", string.Empty);
            if (!string.IsNullOrEmpty(lastId))
                SelectedPatient = Patients.FirstOrDefault(p => p.PatientId == lastId);

            SelectedPatient ??= Patients.FirstOrDefault();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not load patients: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSelectedPatientChanged(Patient? value)
    {
        if (value is not null)
            Preferences.Set("last_patient_id", value.PatientId);
    }

    [RelayCommand]
    public async Task SubmitVitalsAsync()
    {
        if (SelectedPatient is null)
        {
            StatusMessage = "Please select a patient.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Systolic) &&
            string.IsNullOrWhiteSpace(Diastolic) &&
            string.IsNullOrWhiteSpace(HeartRate) &&
            string.IsNullOrWhiteSpace(OxygenSaturation) &&
            string.IsNullOrWhiteSpace(Temperature) &&
            string.IsNullOrWhiteSpace(BloodGlucose))
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
                PatientId = SelectedPatient.PatientId,
                RecordedAt = null, // backend does COALESCE to now()
                Systolic = TryParseInt(Systolic),
                Diastolic = TryParseInt(Diastolic),
                HeartRate = TryParseInt(HeartRate),
                OxygenSaturation = TryParseInt(OxygenSaturation),
                Temperature = TryParseDouble(Temperature),
                BloodGlucose = TryParseInt(BloodGlucose),
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
        Notes = string.Empty;
    }

    private static int? TryParseInt(string val) =>
        int.TryParse(val, out var result) ? result : null;

    private static double? TryParseDouble(string val) =>
        double.TryParse(val, out var result) ? result : null;
}