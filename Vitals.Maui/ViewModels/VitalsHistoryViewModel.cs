using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Vitals.Maui.Models;
using Vitals.Maui.Services;

namespace Vitals.Maui.ViewModels;

public partial class VitalsHistoryViewModel : ObservableObject
{
    private readonly ApiService _api;
    private readonly PatientStateService _patientState;

    public Patient? SelectedPatient => _patientState.SelectedPatient;

    [ObservableProperty] private ObservableCollection<VitalHistoryDisplay> _rows = new();
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _hasNoData;

    // Day buttons
    [ObservableProperty] private int _selectedDays = 15;
    [ObservableProperty] private Color _btn15Color = Color.FromArgb("#1976d2");
    [ObservableProperty] private Color _btn30Color = Color.FromArgb("#0f3460");
    [ObservableProperty] private Color _btn45Color = Color.FromArgb("#0f3460");
    [ObservableProperty] private Color _btn60Color = Color.FromArgb("#0f3460");
    [ObservableProperty] private Color _btnCustomColor = Color.FromArgb("#0f3460");
    [ObservableProperty] private string _customDaysLabel = "Custom";

    public VitalsHistoryViewModel(ApiService api, PatientStateService patientState)
    {
        _api = api;
        _patientState = patientState;

        _patientState.PropertyChanged += async (s, e) =>
        {
            if (e.PropertyName == nameof(PatientStateService.SelectedPatient))
            {
                OnPropertyChanged(nameof(SelectedPatient));
                await LoadHistoryAsync();
            }
        };
    }

    public async Task LoadAsync(int days = 15)
    {
        SelectedDays = days;
        UpdateButtonColors(days);
        await LoadHistoryAsync();
    }

    [RelayCommand]
    public async Task SelectDaysAsync(string days)
    {
        if (int.TryParse(days, out var d))
        {
            SelectedDays = d;
            UpdateButtonColors(d);
            CustomDaysLabel = "Custom";
            await LoadHistoryAsync();
        }
    }

    [RelayCommand]
    public async Task SelectCustomDaysAsync(string customDays)
    {
        if (int.TryParse(customDays, out var d) && d > 0)
        {
            SelectedDays = d;
            CustomDaysLabel = $"{d}d";
            UpdateButtonColors(-1);
            await LoadHistoryAsync();
        }
    }

    private async Task LoadHistoryAsync()
    {
        if (_patientState.SelectedPatient is null) return;

        IsBusy = true;
        StatusMessage = string.Empty;

        try
        {
            var history = await _api.GetVitalsHistoryAsync(
                _patientState.SelectedPatient.PatientId, SelectedDays);

            var sorted = history
                .OrderByDescending(r => r.Date)
                .Select(VitalHistoryDisplay.FromRow)
                .ToList();

            Rows = new ObservableCollection<VitalHistoryDisplay>(sorted);
            HasNoData = !Rows.Any();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not load history: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateButtonColors(int days)
    {
        var active = Color.FromArgb("#1976d2");
        var inactive = Color.FromArgb("#0f3460");
        Btn15Color = days == 15 ? active : inactive;
        Btn30Color = days == 30 ? active : inactive;
        Btn45Color = days == 45 ? active : inactive;
        Btn60Color = days == 60 ? active : inactive;
        BtnCustomColor = days == -1 ? active : inactive;
    }
}