using CommunityToolkit.Mvvm.ComponentModel;
using Vitals.Maui.Models;

namespace Vitals.Maui.Services;

public partial class PatientStateService : ObservableObject
{
    private readonly ApiService _api;

    [ObservableProperty]
    private List<Patient> _patients = new();

    [ObservableProperty]
    private Patient? _selectedPatient;

    public PatientStateService(ApiService api)
    {
        _api = api;
    }

    public async Task InitializeAsync()
    {
        if (Patients.Any()) return;

        try
        {
            var list = await _api.GetPatientsAsync();
            if (list is null || !list.Any()) return;

            Patients = list;

            var lastId = Preferences.Get("last_patient_id", string.Empty);

            if (!string.IsNullOrEmpty(lastId))
                SelectedPatient = Patients.FirstOrDefault(p => p.PatientId == lastId);

            SelectedPatient ??= Patients.FirstOrDefault();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== PATIENT STATE INIT ERROR: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"=== STACK: {ex.StackTrace}");
        }
    }

    partial void OnSelectedPatientChanged(Patient? value)
    {
        if (value is not null)
            Preferences.Set("last_patient_id", value.PatientId);
    }
}