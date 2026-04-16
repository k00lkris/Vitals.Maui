using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Vitals.Maui.Models;
using Vitals.Maui.Services;
using Vitals.Maui.Views;

namespace Vitals.Maui.ViewModels;

public partial class IncidentLogViewModel : ObservableObject
{
    private readonly ApiService _api;
    private readonly PatientStateService _patientState;

    public Patient? SelectedPatient => _patientState.SelectedPatient;

    [ObservableProperty] private ObservableCollection<IncidentLog> _incidents = new();
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _hasNoIncidents;

    public IncidentLogViewModel(ApiService api, PatientStateService patientState)
    {
        _api = api;
        _patientState = patientState;

        _patientState.PropertyChanged += async (s, e) =>
        {
            if (e.PropertyName == nameof(PatientStateService.SelectedPatient))
            {
                OnPropertyChanged(nameof(SelectedPatient));
                await LoadIncidentsAsync();
            }
        };
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        await _patientState.InitializeAsync();
        OnPropertyChanged(nameof(SelectedPatient));
        await LoadIncidentsAsync();
    }

    public async Task LoadIncidentsAsync()
    {
        if (_patientState.SelectedPatient is null) return;

        IsBusy = true;
        StatusMessage = string.Empty;

        try
        {
            var incidents = await _api.GetIncidentsAsync(
                _patientState.SelectedPatient.PatientId);
            Incidents = new ObservableCollection<IncidentLog>(incidents);
            HasNoIncidents = !Incidents.Any();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not load incidents: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task OpenIncidentDetailAsync(IncidentLog incident)
    {
        var vm = Application.Current!.Handler.MauiContext!
            .Services.GetService<IncidentDetailViewModel>()!;
        await vm.InitializeAsync(incident, _patientState.SelectedPatient!.PatientId);

        var popup = new IncidentDetailPopup(vm);
        await Shell.Current.CurrentPage.ShowPopupAsync(popup);
        await LoadIncidentsAsync();
    }

    [RelayCommand]
    public async Task OpenAddIncidentAsync()
    {
        var vm = Application.Current!.Handler.MauiContext!
            .Services.GetService<IncidentDetailViewModel>()!;
        await vm.InitializeAsync(null, _patientState.SelectedPatient!.PatientId);

        var popup = new IncidentDetailPopup(vm);
        await Shell.Current.CurrentPage.ShowPopupAsync(popup);
        await LoadIncidentsAsync();
    }
}