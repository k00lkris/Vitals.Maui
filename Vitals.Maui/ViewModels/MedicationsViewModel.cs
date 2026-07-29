using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Vitals.Maui.Models;
using Vitals.Maui.Services;
using Vitals.Maui.Views;

namespace Vitals.Maui.ViewModels;

public partial class MedicationsViewModel : ObservableObject
{
    private readonly ApiService _api;
    private readonly PatientStateService _patientState;

    public ObservableCollection<Patient> Patients => new(_patientState.Patients);

    public Patient? SelectedPatient
    {
        get => _patientState.SelectedPatient;
        set
        {
            _patientState.SelectedPatient = value;
            OnPropertyChanged();
            _ = LoadMedicationsAsync();
        }
    }

    [ObservableProperty] private ObservableCollection<Medication> _morningMeds = new();
    [ObservableProperty] private ObservableCollection<Medication> _middayMeds = new();
    [ObservableProperty] private ObservableCollection<Medication> _eveningMeds = new();
    [ObservableProperty] private ObservableCollection<Medication> _nightMeds = new();
    [ObservableProperty] private ObservableCollection<Medication> _otherMeds = new();

    [ObservableProperty] private bool _hasMorning;
    [ObservableProperty] private bool _hasMidday;
    [ObservableProperty] private bool _hasEvening;
    [ObservableProperty] private bool _hasNight;
    [ObservableProperty] private bool _hasOther;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _hasNoMedications;
    [ObservableProperty] private string _noMedicationsMessage = "No medications on record.";

    // Default true: hide inactive/discontinued meds (e.g. a prescription a
    // doctor took the patient off of) so the day-to-day list stays focused
    // on what's currently being taken. Persisted the same way Settings
    // preferences are, so the choice survives app restarts.
    [ObservableProperty] private bool _hideInactiveMedications = true;

    // Full, unfiltered list from the last API load. Re-filtering when the
    // toggle changes works off this cache instead of re-hitting the API.
    private List<Medication> _allMeds = new();

    public MedicationsViewModel(ApiService api, PatientStateService patientState)
    {
        _api = api;
        _patientState = patientState;

        HideInactiveMedications = Preferences.Get("hide_inactive_medications", true);

        _patientState.PropertyChanged += async (s, e) =>
        {
            if (e.PropertyName == nameof(PatientStateService.SelectedPatient))
            {
                OnPropertyChanged(nameof(SelectedPatient));
                await LoadMedicationsAsync();
            }
        };
    }

    partial void OnHideInactiveMedicationsChanged(bool value)
    {
        Preferences.Set("hide_inactive_medications", value);
        ApplyFilterAndGroup();
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        await _patientState.InitializeAsync();
        OnPropertyChanged(nameof(Patients));
        OnPropertyChanged(nameof(SelectedPatient));
        await LoadMedicationsAsync();
    }

    private async Task LoadMedicationsAsync()
    {
        if (_patientState.SelectedPatient is null) return;

        IsBusy = true;
        StatusMessage = string.Empty;

        try
        {
            _allMeds = await _api.GetMedicationsAsync(
                _patientState.SelectedPatient.PatientId);

            ApplyFilterAndGroup();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not load medications: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Filters _allMeds by HideInactiveMedications, then buckets by time of
    /// day. Split out from LoadMedicationsAsync so toggling the switch just
    /// re-filters the cached list rather than re-fetching from the API.
    /// </summary>
    private void ApplyFilterAndGroup()
    {
        var meds = HideInactiveMedications
            ? _allMeds.Where(m => m.IsActive).ToList()
            : _allMeds;

        // Group by time of day — a med can appear in multiple groups
        MorningMeds = new ObservableCollection<Medication>(
            meds.Where(m => m.TimeOfDay.Contains("morning")));
        MiddayMeds = new ObservableCollection<Medication>(
            meds.Where(m => m.TimeOfDay.Contains("midday")));
        EveningMeds = new ObservableCollection<Medication>(
            meds.Where(m => m.TimeOfDay.Contains("evening")));
        NightMeds = new ObservableCollection<Medication>(
            meds.Where(m => m.TimeOfDay.Contains("night")));
        OtherMeds = new ObservableCollection<Medication>(
            meds.Where(m => !m.TimeOfDay.Contains("morning") &&
                            !m.TimeOfDay.Contains("midday") &&
                            !m.TimeOfDay.Contains("evening") &&
                            !m.TimeOfDay.Contains("night")));

        HasMorning = MorningMeds.Any();
        HasMidday = MiddayMeds.Any();
        HasEvening = EveningMeds.Any();
        HasNight = NightMeds.Any();
        HasOther = OtherMeds.Any();
        HasNoMedications = !meds.Any();

        NoMedicationsMessage = HideInactiveMedications && _allMeds.Any(m => !m.IsActive)
            ? "No active medications on record. (Inactive medications are hidden.)"
            : "No medications on record.";
    }
    [RelayCommand]
    public async Task OpenMedicationDetailAsync(Medication medication)
    {
        var vm = Application.Current!.Handler.MauiContext!
            .Services.GetService<MedicationDetailViewModel>()!;
        await vm.InitializeAsync(medication, _patientState.SelectedPatient!.PatientId);

        var popup = new MedicationDetailPopup(vm);
        await Shell.Current.CurrentPage.ShowPopupAsync(popup);

        // Always reload after popup closes
        await LoadMedicationsAsync();
    }

    [RelayCommand]
    public async Task OpenAddMedicationAsync()
    {
        var vm = Application.Current!.Handler.MauiContext!
            .Services.GetService<MedicationDetailViewModel>()!;
        await vm.InitializeAsync(null, _patientState.SelectedPatient!.PatientId);

        var popup = new MedicationDetailPopup(vm);
        await Shell.Current.CurrentPage.ShowPopupAsync(popup);

        // Always reload after popup closes
        await LoadMedicationsAsync();
    }
}