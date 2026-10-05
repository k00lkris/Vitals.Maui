using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Vitals.Maui.Models;
using Vitals.Maui.Services;
using Vitals.Maui.Views;

namespace Vitals.Maui.ViewModels;

public partial class VitalsHistoryViewModel : ObservableObject
{
    private readonly ApiService _api;
    private readonly PatientStateService _patientState;
    private readonly UserPreferencesService _preferences;
    private UserPreferences _activePreferences = new();

    public Patient? SelectedPatient => _patientState.SelectedPatient;

    [ObservableProperty] private ObservableCollection<VitalHistoryDisplay> _rows = new();
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _hasNoData;

    // Blood pressure is always shown. Optional history columns follow the
    // selected patient's persisted vital preferences.
    [ObservableProperty] private bool _showHeartRate = true;
    [ObservableProperty] private bool _showSpo2 = true;
    [ObservableProperty] private bool _showTemperature = true;
    [ObservableProperty] private bool _showWeight;
    [ObservableProperty] private bool _showGlucose;

    // Day buttons — background colors
    [ObservableProperty] private int _selectedDays = 15;

    private Color _btnDay15Color = Colors.Transparent;
    public Color BtnDay15Color { get => _btnDay15Color; set { _btnDay15Color = value; OnPropertyChanged(); } }

    private Color _btnDay30Color = Colors.Transparent;
    public Color BtnDay30Color { get => _btnDay30Color; set { _btnDay30Color = value; OnPropertyChanged(); } }

    private Color _btnDay45Color = Colors.Transparent;
    public Color BtnDay45Color { get => _btnDay45Color; set { _btnDay45Color = value; OnPropertyChanged(); } }

    private Color _btnDay60Color = Colors.Transparent;
    public Color BtnDay60Color { get => _btnDay60Color; set { _btnDay60Color = value; OnPropertyChanged(); } }

    private Color _btnCustomColor = Colors.Transparent;
    public Color BtnCustomColor { get => _btnCustomColor; set { _btnCustomColor = value; OnPropertyChanged(); } }

    // Day buttons — text colors
    private Color _btnDay15TextColor = Colors.White;
    public Color BtnDay15TextColor { get => _btnDay15TextColor; set { _btnDay15TextColor = value; OnPropertyChanged(); } }

    private Color _btnDay30TextColor = Colors.White;
    public Color BtnDay30TextColor { get => _btnDay30TextColor; set { _btnDay30TextColor = value; OnPropertyChanged(); } }

    private Color _btnDay45TextColor = Colors.White;
    public Color BtnDay45TextColor { get => _btnDay45TextColor; set { _btnDay45TextColor = value; OnPropertyChanged(); } }

    private Color _btnDay60TextColor = Colors.White;
    public Color BtnDay60TextColor { get => _btnDay60TextColor; set { _btnDay60TextColor = value; OnPropertyChanged(); } }

    private Color _btnCustomTextColor = Colors.White;
    public Color BtnCustomTextColor { get => _btnCustomTextColor; set { _btnCustomTextColor = value; OnPropertyChanged(); } }

    [ObservableProperty] private string _customDaysLabel = "Custom";

    public VitalsHistoryViewModel(
        ApiService api,
        PatientStateService patientState,
        UserPreferencesService preferences)
    {
        _api = api;
        _patientState = patientState;
        _preferences = preferences;

        _patientState.PropertyChanged += async (s, e) =>
        {
            if (e.PropertyName == nameof(PatientStateService.SelectedPatient))
            {
                OnPropertyChanged(nameof(SelectedPatient));
                await LoadSelectedPatientAsync();
            }
        };
    }

    public async Task LoadAsync(int days = 15)
    {
        await _patientState.InitializeAsync();
        OnPropertyChanged(nameof(SelectedPatient));

        SelectedDays = days;
        UpdateButtonColors(days);
        await LoadSelectedPatientAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        // Pull-to-refresh should reload the CURRENT range and patient rather
        // than resetting the screen back to 15 days.
        await _patientState.InitializeAsync();
        OnPropertyChanged(nameof(SelectedPatient));
        await LoadSelectedPatientAsync();
    }

    private async Task LoadSelectedPatientAsync()
    {
        var patientId = _patientState.SelectedPatient?.PatientId;
        if (string.IsNullOrWhiteSpace(patientId))
        {
            Rows.Clear();
            HasNoData = true;
            return;
        }

        var preferences = await _preferences.RefreshAsync(patientId);

        // Do not apply a stale preference response after a rapid patient switch.
        if (_patientState.SelectedPatient?.PatientId != patientId)
            return;

        _activePreferences = preferences;
        ApplyPreferences(preferences);
        await LoadHistoryAsync(patientId, preferences);
    }

    private void ApplyPreferences(UserPreferences preferences)
    {
        ShowHeartRate = preferences.ShowHeartRate;
        ShowSpo2 = preferences.ShowSpo2;
        ShowTemperature = preferences.ShowTemperature;
        ShowWeight = preferences.ShowWeight;
        ShowGlucose = preferences.ShowGlucose;
    }

    [RelayCommand]
    public async Task SelectDaysAsync(string days)
    {
        if (int.TryParse(days, out var d))
        {
            SelectedDays = d;
            UpdateButtonColors(d);
            CustomDaysLabel = "Custom";

            var patientId = _patientState.SelectedPatient?.PatientId;
            if (!string.IsNullOrWhiteSpace(patientId))
                await LoadHistoryAsync(patientId, _activePreferences);
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

            var patientId = _patientState.SelectedPatient?.PatientId;
            if (!string.IsNullOrWhiteSpace(patientId))
                await LoadHistoryAsync(patientId, _activePreferences);
        }
    }

    private async Task LoadHistoryAsync(
        string patientId,
        UserPreferences preferences)
    {
        IsBusy = true;
        StatusMessage = string.Empty;

        try
        {
            var history = await _api.GetVitalsHistoryAsync(patientId, SelectedDays);

            // A patient switch can finish while history is loading. Do not put
            // the previous patient's rows into the newly selected patient view.
            if (_patientState.SelectedPatient?.PatientId != patientId)
                return;

            var sorted = history
                .Where(r => HasVisibleVital(r, preferences))
                .OrderByDescending(r => r.Date)
                .Select(r => VitalHistoryDisplay.FromRow(r, preferences))
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

    public async Task OpenVitalRecordAsync(VitalHistoryDisplay row)
    {
        if (string.IsNullOrWhiteSpace(row.VitalId))
        {
            StatusMessage = "This history row does not have a record id and cannot be edited.";
            return;
        }

        var vm = Application.Current!.Handler.MauiContext!
            .Services.GetService<VitalHistoryDetailViewModel>()!;

        await vm.InitializeAsync(row.VitalId);

        var popup = new VitalHistoryDetailPopup(vm);
        await Shell.Current.CurrentPage.ShowPopupAsync(popup);

        // Whether the user saved, deleted, reassigned, or simply closed the
        // popup, refresh from the server so History cannot display stale data.
        var patientId = _patientState.SelectedPatient?.PatientId;
        if (!string.IsNullOrWhiteSpace(patientId))
            await LoadHistoryAsync(patientId, _activePreferences);
    }

    private static bool HasVisibleVital(
        VitalHistoryRow row,
        UserPreferences preferences) =>
        row.Systolic.HasValue ||
        row.Diastolic.HasValue ||
        (preferences.ShowHeartRate && row.HeartRate.HasValue) ||
        (preferences.ShowSpo2 && row.Spo2.HasValue) ||
        (preferences.ShowTemperature && row.Temperature.HasValue) ||
        (preferences.ShowWeight && row.Weight.HasValue) ||
        (preferences.ShowGlucose && row.BloodGlucose.HasValue);

    private void UpdateButtonColors(int days)
    {
        if (Application.Current?.Resources is null) return;

        var res = Application.Current.Resources;
        var active = res.TryGetValue("ButtonBackground", out var a) ? (Color)a : Color.FromArgb("#00acc1");
        var inactive = res.TryGetValue("ButtonSecondary", out var i) ? (Color)i : Color.FromArgb("#b2dff2");
        var activeTxt = res.TryGetValue("TextPrimary", out var at) ? (Color)at : Colors.White;
        var inactiveTxt = res.TryGetValue("ButtonSecondaryText", out var it) ? (Color)it : Color.FromArgb("#0d2137");

        BtnDay15Color = days == 15 ? active : inactive;
        BtnDay30Color = days == 30 ? active : inactive;
        BtnDay45Color = days == 45 ? active : inactive;
        BtnDay60Color = days == 60 ? active : inactive;
        BtnCustomColor = days == -1 ? active : inactive;

        BtnDay15TextColor = days == 15 ? activeTxt : inactiveTxt;
        BtnDay30TextColor = days == 30 ? activeTxt : inactiveTxt;
        BtnDay45TextColor = days == 45 ? activeTxt : inactiveTxt;
        BtnDay60TextColor = days == 60 ? activeTxt : inactiveTxt;
        BtnCustomTextColor = days == -1 ? activeTxt : inactiveTxt;
    }
}
