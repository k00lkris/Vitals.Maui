using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Vitals.Maui.Models;
using Vitals.Maui.Services;
using Vitals.Maui.Views;

namespace Vitals.Maui.ViewModels;

public partial class CareTeamViewModel : ObservableObject
{
    private readonly ApiService _api;
    private readonly PatientStateService _patientState;

    public Patient? SelectedPatient => _patientState.SelectedPatient;

    [ObservableProperty] private ObservableCollection<Doctor> _doctors = new();
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _hasNoDoctors;

    public CareTeamViewModel(ApiService api, PatientStateService patientState)
    {
        _api = api;
        _patientState = patientState;

        _patientState.PropertyChanged += async (s, e) =>
        {
            if (e.PropertyName == nameof(PatientStateService.SelectedPatient))
            {
                OnPropertyChanged(nameof(SelectedPatient));
                await LoadDoctorsAsync();
            }
        };
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        await _patientState.InitializeAsync();
        OnPropertyChanged(nameof(SelectedPatient));
        await LoadDoctorsAsync();
    }

    public async Task LoadDoctorsAsync()
    {
        if (_patientState.SelectedPatient is null) return;

        IsBusy = true;
        StatusMessage = string.Empty;

        try
        {
            var list = await _api.GetDoctorsAsync(
                _patientState.SelectedPatient.PatientId);

            // Primary first, then alphabetical
            var sorted = list
                .OrderByDescending(d => d.IsPrimary)
                .ThenBy(d => d.Name)
                .ToList();

            Doctors = new ObservableCollection<Doctor>(sorted);
            HasNoDoctors = !Doctors.Any();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not load care team: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task OpenDoctorDetailAsync(Doctor doctor)
    {
        if (_patientState.SelectedPatient is null) return;

        var vm = Application.Current?.Handler?.MauiContext?.Services
            .GetService<DoctorDetailViewModel>();

        if (vm is null) return;

        await vm.InitializeAsync(doctor, _patientState.SelectedPatient.PatientId);

        var popup = new DoctorDetailPopup(vm);
        await Shell.Current.CurrentPage.ShowPopupAsync(popup);
        await LoadDoctorsAsync();
    }

    [RelayCommand]
    public async Task OpenAddDoctorAsync()
    {
        try
        {
            var vm = Application.Current!.Handler.MauiContext!
                .Services.GetService<DoctorDetailViewModel>()!;
            await vm.InitializeAsync(null, _patientState.SelectedPatient!.PatientId);

            var popup = new DoctorDetailPopup(vm);
            await Shell.Current.CurrentPage.ShowPopupAsync(popup);
            await LoadDoctorsAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== ADD DOCTOR CRASH: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"=== STACK: {ex.StackTrace}");
            await Shell.Current.DisplayAlert("Error",
                "Could not open Add Doctor. Please check your connection.", "OK");
        }
    }
}