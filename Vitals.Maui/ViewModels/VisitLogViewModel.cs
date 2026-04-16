using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Vitals.Maui.Models;
using Vitals.Maui.Services;
using Vitals.Maui.Views;

namespace Vitals.Maui.ViewModels;

public partial class VisitLogViewModel : ObservableObject
{
    private readonly ApiService _api;
    private readonly PatientStateService _patientState;

    public Patient? SelectedPatient => _patientState.SelectedPatient;

    [ObservableProperty] private ObservableCollection<VisitLog> _visits = new();
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _hasNoVisits;

    public VisitLogViewModel(ApiService api, PatientStateService patientState)
    {
        _api = api;
        _patientState = patientState;

        _patientState.PropertyChanged += async (s, e) =>
        {
            if (e.PropertyName == nameof(PatientStateService.SelectedPatient))
            {
                OnPropertyChanged(nameof(SelectedPatient));
                await LoadVisitsAsync();
            }
        };
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        await _patientState.InitializeAsync();
        OnPropertyChanged(nameof(SelectedPatient));
        await LoadVisitsAsync();
    }

    public async Task LoadVisitsAsync()
    {
        if (_patientState.SelectedPatient is null) return;

        IsBusy = true;
        StatusMessage = string.Empty;

        try
        {
            var visits = await _api.GetVisitsAsync(
                _patientState.SelectedPatient.PatientId);
            Visits = new ObservableCollection<VisitLog>(visits);
            HasNoVisits = !Visits.Any();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not load visits: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task OpenVisitDetailAsync(VisitLog visit)
    {
        var vm = Application.Current!.Handler.MauiContext!
            .Services.GetService<VisitDetailViewModel>()!;
        await vm.InitializeAsync(visit, _patientState.SelectedPatient!.PatientId);

        var popup = new VisitDetailPopup(vm);
        await Shell.Current.CurrentPage.ShowPopupAsync(popup);
        await LoadVisitsAsync();
    }

    [RelayCommand]
    public async Task OpenAddVisitAsync()
    {
        var vm = Application.Current!.Handler.MauiContext!
            .Services.GetService<VisitDetailViewModel>()!;
        await vm.InitializeAsync(null, _patientState.SelectedPatient!.PatientId);

        var popup = new VisitDetailPopup(vm);
        await Shell.Current.CurrentPage.ShowPopupAsync(popup);
        await LoadVisitsAsync();
    }
}