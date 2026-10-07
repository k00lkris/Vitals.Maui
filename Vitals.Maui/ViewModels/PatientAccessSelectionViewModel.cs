using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vitals.Maui.Models;
using Vitals.Maui.Services;

namespace Vitals.Maui.ViewModels;

public partial class PatientAccessChoice : ObservableObject
{
    public Patient Patient { get; }

    [ObservableProperty]
    private bool _isSelected;

    public string FullName => Patient.FullName;
    public string Details =>
        string.IsNullOrWhiteSpace(Patient.Dob)
            ? "Date of birth not set"
            : $"Born {Patient.Dob}";

    public PatientAccessChoice(Patient patient)
    {
        Patient = patient;
    }
}

public partial class PatientAccessSelectionViewModel : ObservableObject
{
    private readonly ApiService _api;
    private readonly EntitlementService _entitlements;
    private readonly PatientStateService _patientState;
    private bool _loaded;

    public Action? OnCompleted { get; set; }

    [ObservableProperty]
    private ObservableCollection<PatientAccessChoice> _patients = new();

    [ObservableProperty]
    private int _requiredCount;

    [ObservableProperty]
    private int _selectedCount;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public string SelectionSummary => $"{SelectedCount} of {RequiredCount} selected";

    public bool CanContinue =>
        !IsBusy &&
        RequiredCount > 0 &&
        SelectedCount == RequiredCount;

    public PatientAccessSelectionViewModel(
        ApiService api,
        EntitlementService entitlements,
        PatientStateService patientState)
    {
        _api = api;
        _entitlements = entitlements;
        _patientState = patientState;
    }

    public async Task LoadAsync()
    {
        if (_loaded) return;

        IsBusy = true;
        StatusMessage = string.Empty;
        try
        {
            var entitlement = await _entitlements.RefreshAsync();
            if (entitlement is null)
            {
                StatusMessage = "We couldn't load your plan. Please check your connection and try again.";
                return;
            }

            if (!entitlement.RequiresBasicPatientSelection)
            {
                _loaded = true;
                OnCompleted?.Invoke();
                return;
            }

            RequiredCount = entitlement.PatientLimit ?? 0;
            var patients = await _api.GetPatientsAsync();

            // Deliberately begin with no choices selected. The downgrade flow
            // should never silently decide which family members stay active.
            Patients = new ObservableCollection<PatientAccessChoice>(
                patients.Select(patient => new PatientAccessChoice(patient)));

            SelectedCount = 0;
            _loaded = true;
        }
        finally
        {
            IsBusy = false;
            NotifySelectionStateChanged();
        }
    }

    public bool SetSelection(PatientAccessChoice choice, bool selected)
    {
        if (IsBusy) return false;

        if (selected && !choice.IsSelected && SelectedCount >= RequiredCount)
        {
            StatusMessage = $"Your current plan supports {RequiredCount} active patients. Deselect one before choosing another.";
            return false;
        }

        choice.IsSelected = selected;
        SelectedCount = Patients.Count(item => item.IsSelected);
        StatusMessage = string.Empty;
        NotifySelectionStateChanged();
        return true;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!CanContinue)
        {
            StatusMessage = $"Choose exactly {RequiredCount} patients to continue.";
            return;
        }

        IsBusy = true;
        StatusMessage = string.Empty;
        NotifySelectionStateChanged();

        try
        {
            var selectedIds = Patients
                .Where(item => item.IsSelected)
                .Select(item => item.Patient.PatientId)
                .ToArray();

            var updated = await _api.SelectActivePatientsAsync(selectedIds);
            if (updated is null)
            {
                StatusMessage = "We couldn't save your patient choices. Please try again.";
                return;
            }

            await _entitlements.RefreshAsync();

            // Rebuild the shared patient cache so the shell can never keep a
            // patient that just became locked as its selected profile.
            _patientState.Reset();
            await _patientState.InitializeAsync();

            OnCompleted?.Invoke();
        }
        finally
        {
            IsBusy = false;
            NotifySelectionStateChanged();
        }
    }

    partial void OnRequiredCountChanged(int value) => NotifySelectionStateChanged();
    partial void OnSelectedCountChanged(int value) => NotifySelectionStateChanged();
    partial void OnIsBusyChanged(bool value) => NotifySelectionStateChanged();

    private void NotifySelectionStateChanged()
    {
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(CanContinue));
    }
}
