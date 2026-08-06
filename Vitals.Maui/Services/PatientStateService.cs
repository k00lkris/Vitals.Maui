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

    /// <summary>
    /// Clears cached patient data. This service is registered as a Singleton
    /// (see MauiProgram.cs), so it otherwise lives for the entire app process
    /// — InitializeAsync's "if (Patients.Any()) return" guard means once
    /// populated, it never reloads on its own. Without an explicit reset,
    /// signing in as a different account within the same process (no app
    /// restart) would keep showing whichever household's patients were
    /// cached from before, even though AuthService correctly switched
    /// identity. Call this on sign-out and right after any successful
    /// sign-in, so a session boundary always means a clean slate.
    ///
    /// Also clears the persisted "last_patient_id" Preference — this is
    /// device-level storage, not in-memory state, so it survives logout
    /// and login on its own. That was harmless before household sharing
    /// existed (a stale ID would just fail to match and fall through to a
    /// default), but now that two different accounts can share the same
    /// patient list, a stale ID left over from a PREVIOUS account can
    /// validly match a patient in a NEW account's shared list — showing
    /// the wrong "active" patient after switching accounts on the same
    /// device. Clearing it here means each fresh session always starts
    /// with no carried-over selection, regardless of who logged in before.
    /// </summary>
    public void Reset()
    {
        Patients = new List<Patient>();
        SelectedPatient = null;
        Preferences.Remove("last_patient_id");
    }

    partial void OnSelectedPatientChanged(Patient? value)
    {
        if (value is not null)
            Preferences.Set("last_patient_id", value.PatientId);
    }
}