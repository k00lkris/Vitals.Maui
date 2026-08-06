using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vitals.Maui.Models;
using Vitals.Maui.Services;

namespace Vitals.Maui.ViewModels;

public partial class OnboardingJoinPatientSelectionViewModel : ObservableObject
{
    private readonly ApiService _api;
    private readonly PatientStateService _patientState;

    // Wired by the page's code-behind.
    public Action? OnBack { get; set; }
    public Action? OnContinue { get; set; }
    public Action? OnCreateNewPatient { get; set; }

    [ObservableProperty] private System.Collections.ObjectModel.ObservableCollection<Patient> _patients = new();
    [ObservableProperty] private Patient? _selectedPatient;
    [ObservableProperty] private bool _canCreateNewPatient;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public OnboardingJoinPatientSelectionViewModel(ApiService api, PatientStateService patientState)
    {
        _api = api;
        _patientState = patientState;
    }

    /// <summary>
    /// Loads the household's existing patients — this is who the joining
    /// user can choose to attach to as their default view. Excludes any
    /// patient already claimed with relationship='self' by someone else,
    /// so an already-claimed identity (e.g. the account holder's own
    /// record) can never appear as an option for a second person to
    /// mistakenly confirm as themselves. canCreateNewPatient comes
    /// straight from /api/household/join's response (see
    /// AuthService/ApiService), computed server-side against the
    /// household's actual patient_limit vs current count.
    /// </summary>
    public async Task LoadAsync(bool canCreateNewPatient)
    {
        CanCreateNewPatient = canCreateNewPatient;

        IsBusy = true;
        try
        {
            _patientState.Reset();
            var list = await _api.GetPatientsAsync(excludeSelfClaimed: true);
            Patients = new System.Collections.ObjectModel.ObservableCollection<Patient>(list);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void SelectPatient(Patient patient)
    {
        SelectedPatient = patient;
    }

    /// <summary>
    /// Shows a DOB/gender confirmation before actually claiming the
    /// patient — a plain name match is easy to mis-tap, especially with
    /// duplicate-sounding names (exactly what prompted this: two patients
    /// both literally named "Spartan"). Only calls ClaimPatientAsync
    /// (the real, server-side "self" link) after explicit confirmation.
    /// </summary>
    [RelayCommand]
    public async Task ContinueAsync()
    {
        if (SelectedPatient is null)
        {
            StatusMessage = "Choose a patient to continue.";
            return;
        }

        var dobText = string.IsNullOrWhiteSpace(SelectedPatient.Dob) ? "unknown" : SelectedPatient.Dob;
        var genderText = string.IsNullOrWhiteSpace(SelectedPatient.Gender) ? "not specified" : SelectedPatient.Gender;

        var confirmed = await Shell.Current.DisplayAlert(
            "Confirm it's you",
            $"{SelectedPatient.FullName}\nBorn: {dobText}\nGender: {genderText}\n\nIs this you?",
            "Yes, this is me", "No, go back");

        if (!confirmed) return;

        IsBusy = true;
        StatusMessage = string.Empty;

        try
        {
            var result = await _api.ClaimPatientAsync(SelectedPatient.PatientId);
            if (!result.Success)
            {
                StatusMessage = result.ErrorMessage ?? "Something went wrong. Please try again.";
                return;
            }

            _patientState.SelectedPatient = SelectedPatient;
            OnContinue?.Invoke();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void CreateNewPatient()
    {
        if (!CanCreateNewPatient) return;
        OnCreateNewPatient?.Invoke();
    }

    [RelayCommand]
    public void Back()
    {
        OnBack?.Invoke();
    }
}
