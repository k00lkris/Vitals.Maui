using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Vitals.Maui.Models;
using Vitals.Maui.Services;
using Vitals.Maui.Views;

namespace Vitals.Maui.ViewModels;

public partial class AllergiesViewModel : ObservableObject
{
    private readonly ApiService _api;
    private readonly PatientStateService _patientState;

    public Patient? SelectedPatient => _patientState.SelectedPatient;

    [ObservableProperty] private ObservableCollection<Allergy> _medicationAllergies = new();
    [ObservableProperty] private ObservableCollection<Allergy> _foodAllergies = new();
    [ObservableProperty] private ObservableCollection<Allergy> _environmentalAllergies = new();
    [ObservableProperty] private ObservableCollection<Allergy> _otherAllergies = new();

    [ObservableProperty] private bool _hasMedication;
    [ObservableProperty] private bool _hasFood;
    [ObservableProperty] private bool _hasEnvironmental;
    [ObservableProperty] private bool _hasOther;
    [ObservableProperty] private bool _hasNoAllergies;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public AllergiesViewModel(ApiService api, PatientStateService patientState)
    {
        _api = api;
        _patientState = patientState;

        _patientState.PropertyChanged += async (s, e) =>
        {
            if (e.PropertyName == nameof(PatientStateService.SelectedPatient))
            {
                OnPropertyChanged(nameof(SelectedPatient));
                await LoadAllergiesAsync();
            }
        };
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        await _patientState.InitializeAsync();
        OnPropertyChanged(nameof(SelectedPatient));
        await LoadAllergiesAsync();
    }

    public async Task LoadAllergiesAsync()
    {
        if (_patientState.SelectedPatient is null) return;

        IsBusy = true;
        StatusMessage = string.Empty;

        try
        {
            var allergies = await _api.GetAllergiesAsync(
                _patientState.SelectedPatient.PatientId);

            MedicationAllergies = new ObservableCollection<Allergy>(
                allergies.Where(a => a.AllergyType == "medication"));
            FoodAllergies = new ObservableCollection<Allergy>(
                allergies.Where(a => a.AllergyType == "food"));
            EnvironmentalAllergies = new ObservableCollection<Allergy>(
                allergies.Where(a => a.AllergyType == "environmental"));
            OtherAllergies = new ObservableCollection<Allergy>(
                allergies.Where(a => a.AllergyType == "other"));

            HasMedication = MedicationAllergies.Any();
            HasFood = FoodAllergies.Any();
            HasEnvironmental = EnvironmentalAllergies.Any();
            HasOther = OtherAllergies.Any();
            HasNoAllergies = !allergies.Any();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not load allergies: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task OpenAllergyDetailAsync(Allergy allergy)
    {
        var vm = Application.Current!.Handler.MauiContext!
            .Services.GetService<AllergyDetailViewModel>()!;
        await vm.InitializeAsync(allergy, _patientState.SelectedPatient!.PatientId);

        var popup = new AllergyDetailPopup(vm);
        await Shell.Current.CurrentPage.ShowPopupAsync(popup);
        await LoadAllergiesAsync();
    }

    [RelayCommand]
    public async Task OpenAddAllergyAsync()
    {
        var vm = Application.Current!.Handler.MauiContext!
            .Services.GetService<AllergyDetailViewModel>()!;
        await vm.InitializeAsync(null, _patientState.SelectedPatient!.PatientId);

        var popup = new AllergyDetailPopup(vm);
        await Shell.Current.CurrentPage.ShowPopupAsync(popup);
        await LoadAllergiesAsync();
    }
}