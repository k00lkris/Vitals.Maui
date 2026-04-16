using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Vitals.Maui.Models;
using Vitals.Maui.Services;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace Vitals.Maui.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly ApiService _api;
    private readonly PatientStateService _patientState;

    // Patient picker
    public ObservableCollection<Patient> Patients => new(_patientState.Patients);

    public Patient? SelectedPatient
    {
        get => _patientState.SelectedPatient;
        set
        {
            _patientState.SelectedPatient = value;
            OnPropertyChanged();
            _ = LoadDashboardDataAsync();
        }
    }

    // Latest vitals
    [ObservableProperty] private string _latestBp = "—";
    [ObservableProperty] private string _latestHeartRate = "—";
    [ObservableProperty] private string _latestSpo2 = "—";
    [ObservableProperty] private string _latestTemperature = "—";
    [ObservableProperty] private string _latestRecordedAt = "—";

    // Averages
    [ObservableProperty] private string _avgBp = "—";
    [ObservableProperty] private string _avgHeartRate = "—";
    [ObservableProperty] private string _avgSpo2 = "—";
    [ObservableProperty] private string _avgTemperature = "—";

    // Selected days button
    [ObservableProperty] private int _selectedDays = 15;

    // Button states
    [ObservableProperty] private Color _btn15Color = Color.FromArgb("#1976d2");
    [ObservableProperty] private Color _btn30Color = Color.FromArgb("#0f3460");
    [ObservableProperty] private Color _btn45Color = Color.FromArgb("#0f3460");
    [ObservableProperty] private Color _btn60Color = Color.FromArgb("#0f3460");

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;

    // Charts
    [ObservableProperty] private bool _chartsExpanded;
    [ObservableProperty] private string _chartsToggleText = "📈  Expand Charts";

    // Chart series
    [ObservableProperty] private ISeries[] _bpSeries = Array.Empty<ISeries>();
    [ObservableProperty] private ISeries[] _heartRateSeries = Array.Empty<ISeries>();
    [ObservableProperty] private ISeries[] _spo2Series = Array.Empty<ISeries>();
    [ObservableProperty] private ISeries[] _tempSeries = Array.Empty<ISeries>();

    // Chart axes
    [ObservableProperty] private Axis[] _dateAxes = Array.Empty<Axis>();

    public DashboardViewModel(ApiService api, PatientStateService patientState)
    {
        _api = api;
        _patientState = patientState;

        _patientState.PropertyChanged += async (s, e) =>
        {
            if (e.PropertyName == nameof(PatientStateService.SelectedPatient))
            {
                OnPropertyChanged(nameof(SelectedPatient));
                await LoadDashboardDataAsync();
            }
        };
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        await _patientState.InitializeAsync();
        OnPropertyChanged(nameof(Patients));
        OnPropertyChanged(nameof(SelectedPatient));
        await LoadDashboardDataAsync();
    }

    [RelayCommand]
    public async Task SelectDaysAsync(string days)
    {
        if (int.TryParse(days, out var d))
        {
            SelectedDays = d;
            UpdateButtonColors(d);
            await LoadAveragesAsync();

            if (ChartsExpanded)
                await LoadChartsAsync();
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
    }

    private async Task LoadDashboardDataAsync()
    {
        if (_patientState.SelectedPatient is null) return;
        IsBusy = true;
        StatusMessage = string.Empty;

        try
        {
            await Task.WhenAll(
                LoadLatestVitalsAsync(),
                LoadAveragesAsync()
            );
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadLatestVitalsAsync()
    {
        var patientId = _patientState.SelectedPatient?.PatientId;
        if (patientId is null) return;

        var latest = await _api.GetLatestVitalsAsync(patientId);

        if (latest is null)
        {
            LatestBp = "—";
            LatestHeartRate = "—";
            LatestSpo2 = "—";
            LatestTemperature = "—";
            LatestRecordedAt = "No readings yet";
            return;
        }

        LatestBp = latest.Systolic.HasValue && latest.Diastolic.HasValue
            ? $"{latest.Systolic}/{latest.Diastolic}"
            : "—";
        LatestHeartRate = latest.HeartRate.HasValue
            ? $"{latest.HeartRate} bpm"
            : "—";
        LatestSpo2 = latest.OxygenSaturation.HasValue
            ? $"{latest.OxygenSaturation}%"
            : "—";
        LatestTemperature = latest.Temperature.HasValue
            ? $"{latest.Temperature:F1} °F"
            : "—";
        LatestRecordedAt = latest.RecordedAt.HasValue
            ? latest.RecordedAt.Value.ToLocalTime().ToString("MMM d, yyyy h:mm tt")
            : "—";
    }

    private async Task LoadAveragesAsync()
    {
        var patientId = _patientState.SelectedPatient?.PatientId;
        if (patientId is null) return;

        var avg = await _api.GetVitalsAveragesAsync(patientId, SelectedDays);

        if (avg is null)
        {
            AvgBp = "—";
            AvgHeartRate = "—";
            AvgSpo2 = "—";
            AvgTemperature = "—";
            return;
        }

        AvgBp = avg.Systolic.HasValue && avg.Diastolic.HasValue
            ? $"{avg.Systolic:F0}/{avg.Diastolic:F0}"
            : "—";
        AvgHeartRate = avg.HeartRate.HasValue
            ? $"{avg.HeartRate:F0} bpm"
            : "—";
        AvgSpo2 = avg.OxygenSaturation.HasValue
            ? $"{avg.OxygenSaturation:F1}%"
            : "—";
        AvgTemperature = avg.Temperature.HasValue
            ? $"{avg.Temperature:F1} °F"
            : "—";
    }

    [RelayCommand]
    public async Task GoToVitalsEntryAsync()
    {
        await Shell.Current.GoToAsync("//VitalsEntry");
    }

    [RelayCommand]
    public async Task ToggleChartsAsync()
    {
        ChartsExpanded = !ChartsExpanded;
        ChartsToggleText = ChartsExpanded ? "📉  Collapse Charts" : "📈  Expand Charts";

        if (ChartsExpanded)
            await LoadChartsAsync();
    }

    private async Task LoadChartsAsync()
    {
        var patientId = _patientState.SelectedPatient?.PatientId;
        if (patientId is null) return;

        var history = await _api.GetVitalsHistoryAsync(patientId, SelectedDays);
        if (!history.Any()) return;

        var dates = history
            .Select(r => DateTime.Parse(r.Date).Ticks)
            .ToArray();

        // Blood Pressure
        BpSeries = new ISeries[]
        {
        new LineSeries<double?>
        {
            Values = history.Select(r => (double?)r.Systolic).ToArray(),
            Name = "Systolic",
            Stroke = new SolidColorPaint(SKColor.Parse("#d32f2f")) { StrokeThickness = 2 },
            GeometrySize = 4,
            GeometryStroke = new SolidColorPaint(SKColor.Parse("#d32f2f")),
            Fill = null
        },
        new LineSeries<double?>
        {
            Values = history.Select(r => (double?)r.Diastolic).ToArray(),
            Name = "Diastolic",
            Stroke = new SolidColorPaint(SKColor.Parse("#1976d2")) { StrokeThickness = 2 },
            GeometrySize = 4,
            GeometryStroke = new SolidColorPaint(SKColor.Parse("#1976d2")),
            Fill = null
        }
        };

        // Heart Rate
        HeartRateSeries = new ISeries[]
        {
        new LineSeries<double?>
        {
            Values = history.Select(r => (double?)r.HeartRate).ToArray(),
            Name = "Heart Rate",
            Stroke = new SolidColorPaint(SKColor.Parse("#388e3c")) { StrokeThickness = 2 },
            GeometrySize = 4,
            GeometryStroke = new SolidColorPaint(SKColor.Parse("#388e3c")),
            Fill = null
        }
        };

        // SpO2
        Spo2Series = new ISeries[]
        {
        new LineSeries<double?>
        {
            Values = history.Select(r => (double?)r.Spo2).ToArray(),
            Name = "SpO₂",
            Stroke = new SolidColorPaint(SKColor.Parse("#7b1fa2")) { StrokeThickness = 2 },
            GeometrySize = 4,
            GeometryStroke = new SolidColorPaint(SKColor.Parse("#7b1fa2")),
            Fill = null
        }
        };

        // Temperature
        TempSeries = new ISeries[]
        {
        new LineSeries<double?>
        {
            Values = history.Select(r => r.Temperature).ToArray(),
            Name = "Temp °F",
            Stroke = new SolidColorPaint(SKColor.Parse("#f57c00")) { StrokeThickness = 2 },
            GeometrySize = 4,
            GeometryStroke = new SolidColorPaint(SKColor.Parse("#f57c00")),
            Fill = null
        }
        };

        // Date axis
        DateAxes = new Axis[]
        {
        new Axis
        {
            Labels = history
                .Select(r => DateTime.Parse(r.Date).ToString("MM/dd"))
                .ToArray(),
            LabelsRotation = 45,
            TextSize = 10,
            LabelsPaint = new SolidColorPaint(SKColor.Parse("#90caf9"))
        }
        };
    }

    [RelayCommand]
    public async Task GoToVitalsHistoryAsync()
    {
        await Shell.Current.GoToAsync(
            $"//VitalsHistory?days={SelectedDays}");
    }
}