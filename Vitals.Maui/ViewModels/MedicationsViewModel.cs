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

    public MedicationsViewModel(ApiService api, PatientStateService patientState)
    {
        _api = api;
        _patientState = patientState;

        _patientState.PropertyChanged += async (s, e) =>
        {
            if (e.PropertyName == nameof(PatientStateService.SelectedPatient))
            {
                OnPropertyChanged(nameof(SelectedPatient));
                await LoadMedicationsAsync();
            }
        };
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
            var meds = await _api.GetMedicationsAsync(
                _patientState.SelectedPatient.PatientId);

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