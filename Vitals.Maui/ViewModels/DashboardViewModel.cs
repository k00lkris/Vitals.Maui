using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;
using SkiaSharp;
using System.Collections.ObjectModel;
using Vitals.Maui.Models;
using Vitals.Maui.Services;
using Vitals.Maui.Views;

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

    // Smoothed series
    [ObservableProperty] private ISeries[] _smoothedBpSeries = Array.Empty<ISeries>();

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

        // Build x-axis as days from first reading
        var dates = history
            .Select(r => DateTime.Parse(r.Date).ToLocalTime())
            .ToList();
        var xDays = MathService.DateTimesToDays(dates);

        // Raw values
        var sysRaw = history.Select(r => r.Systolic.HasValue ? (double)r.Systolic.Value : double.NaN).ToArray();
        var diaRaw = history.Select(r => r.Diastolic.HasValue ? (double)r.Diastolic.Value : double.NaN).ToArray();
        var hrRaw = history.Select(r => r.HeartRate.HasValue ? (double)r.HeartRate.Value : double.NaN).ToArray();
        var spo2Raw = history.Select(r => r.Spo2.HasValue ? (double)r.Spo2.Value : double.NaN).ToArray();
        var tempRaw = history.Select(r => r.Temperature.HasValue ? (double)r.Temperature.Value : double.NaN).ToArray();

        // Filter out NaN before LOESS
        double[] LoessFiltered(double[] xAll, double[] yAll)
        {
            var validIdx = yAll.Select((v, i) => (v, i))
                               .Where(t => !double.IsNaN(t.v))
                               .ToList();
            if (validIdx.Count < 4) return yAll;

            var xValid = validIdx.Select(t => xAll[t.i]).ToArray();
            var yValid = validIdx.Select(t => t.v).ToArray();
            var smoothed = MathService.Loess(xValid, yValid, 0.3);

            // Map back to full array
            var result = yAll.ToArray();
            for (int k = 0; k < validIdx.Count; k++)
                result[validIdx[k].i] = smoothed[k];
            return result;
        }

        var sysSmoothed = LoessFiltered(xDays, sysRaw);
        var diaSmoothed = LoessFiltered(xDays, diaRaw);
        var hrSmoothed = LoessFiltered(xDays, hrRaw);
        var spo2Smoothed = LoessFiltered(xDays, spo2Raw);
        var tempSmoothed = LoessFiltered(xDays, tempRaw);

        // Helper — raw scatter series
        static LineSeries<double?> RawSeries(double[] raw, string name, string hex) =>
            new LineSeries<double?>
            {
                Values = raw.Select(v => double.IsNaN(v) ? (double?)null : v).ToArray(),
                Name = name,
                Stroke = new SolidColorPaint(SKColor.Parse(hex)) { StrokeThickness = 1 },
                GeometrySize = 4,
                GeometryStroke = new SolidColorPaint(SKColor.Parse(hex)),
                GeometryFill = new SolidColorPaint(SKColor.Parse(hex)),
                Fill = null,
                LineSmoothness = 0
            };

        // Helper — LOESS smooth line (no dots)
        static LineSeries<double?> SmoothedSeries(double[] smoothed, string name, string hex) =>
            new LineSeries<double?>
            {
                Values = smoothed.Select(v => double.IsNaN(v) ? (double?)null : v).ToArray(),
                Name = name,
                Stroke = new SolidColorPaint(SKColor.Parse(hex))
                {
                    StrokeThickness = 3,
                    PathEffect = new DashEffect(new float[] { 6, 3 })
                },
                GeometrySize = 0,
                GeometryFill = null,
                GeometryStroke = null,
                Fill = null,
                LineSmoothness = 0.6
            };

        // Blood Pressure
        BpSeries = new ISeries[]
        {
        RawSeries(sysRaw,  "Systolic",       "#d32f2f"),
        RawSeries(diaRaw,  "Diastolic",      "#1976d2"),
        SmoothedSeries(sysSmoothed,  "Systolic Trend",  "#ff6659"),
        SmoothedSeries(diaSmoothed,  "Diastolic Trend", "#63a4ff"),
        };

        // Heart Rate
        HeartRateSeries = new ISeries[]
        {
        RawSeries(hrRaw,       "Heart Rate",    "#388e3c"),
        SmoothedSeries(hrSmoothed,   "HR Trend",      "#6abf69"),
        };

        // SpO2
        Spo2Series = new ISeries[]
        {
        RawSeries(spo2Raw,     "SpO\u2082",     "#7b1fa2"),
        SmoothedSeries(spo2Smoothed, "SpO\u2082 Trend", "#ae52d4"),
        };

        // Temperature
        TempSeries = new ISeries[]
        {
        RawSeries(tempRaw,     "Temp \u00b0F",  "#f57c00"),
        SmoothedSeries(tempSmoothed, "Temp Trend",    "#ffad42"),
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

    [RelayCommand]
    public async Task OpenVitalsAnalysisAsync()
    {
        var vm = Application.Current!.Handler.MauiContext!
            .Services.GetService<VitalsAnalysisViewModel>()!;
        await vm.LoadAsync(SelectedDays);

        var popup = new VitalsAnalysisView(vm);
        await Shell.Current.CurrentPage.ShowPopupAsync(popup);
    }
}