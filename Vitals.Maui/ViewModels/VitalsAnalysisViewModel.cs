using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vitals.Maui.Models;
using Vitals.Maui.Services;

namespace Vitals.Maui.ViewModels;

public partial class VitalsAnalysisViewModel : ObservableObject
{
    private readonly ApiService _api;
    private readonly PatientStateService _patientState;
    private readonly UserPreferencesService _preferences;

    public Patient? SelectedPatient => _patientState.SelectedPatient;

    [ObservableProperty] private VitalsAnalysis? _analysis;

    // The header shows whichever metric's own reading count is
    // currently selected, not always BP's top-level count. Manually
    // notified (see SelectTab and RunAnalysisAsync) since this doesn't
    // have its own [ObservableProperty] backing field to auto-notify on.
    public int SelectedMetricReadingCount =>
        ShowHr
            ? Analysis?.HeartRate?.ReadingCount ?? 0
            : ShowSpo2
                ? Analysis?.Spo2?.ReadingCount ?? 0
                : ShowTemp
                    ? Analysis?.Temperature?.ReadingCount ?? 0
                    : ShowWeight
                        ? Analysis?.Weight?.ReadingCount ?? 0
                        : ShowGlucose
                            ? Analysis?.Glucose?.ReadingCount ?? 0
                            : Analysis?.ReadingCount ?? 0;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isInsufficient;
    [ObservableProperty] private bool _isOk;
    [ObservableProperty] private bool _hasAnalysisResponse;
    [ObservableProperty] private bool _hasAnalysisResults;

    // Day buttons — background colors
    [ObservableProperty] private int _selectedDays = 30;

    private Color _btn15Color = Colors.Transparent;
    public Color Btn15Color { get => _btn15Color; set { _btn15Color = value; OnPropertyChanged(); } }

    private Color _btn30Color = Colors.Transparent;
    public Color Btn30Color { get => _btn30Color; set { _btn30Color = value; OnPropertyChanged(); } }

    private Color _btn45Color = Colors.Transparent;
    public Color Btn45Color { get => _btn45Color; set { _btn45Color = value; OnPropertyChanged(); } }

    private Color _btn60Color = Colors.Transparent;
    public Color Btn60Color { get => _btn60Color; set { _btn60Color = value; OnPropertyChanged(); } }

    private Color _btnCustomColor = Colors.Transparent;
    public Color BtnCustomColor { get => _btnCustomColor; set { _btnCustomColor = value; OnPropertyChanged(); } }

    // Day buttons — text colors
    private Color _btn15TextColor = Colors.White;
    public Color Btn15TextColor { get => _btn15TextColor; set { _btn15TextColor = value; OnPropertyChanged(); } }

    private Color _btn30TextColor = Colors.White;
    public Color Btn30TextColor { get => _btn30TextColor; set { _btn30TextColor = value; OnPropertyChanged(); } }

    private Color _btn45TextColor = Colors.White;
    public Color Btn45TextColor { get => _btn45TextColor; set { _btn45TextColor = value; OnPropertyChanged(); } }

    private Color _btn60TextColor = Colors.White;
    public Color Btn60TextColor { get => _btn60TextColor; set { _btn60TextColor = value; OnPropertyChanged(); } }

    private Color _btnCustomTextColor = Colors.White;
    public Color BtnCustomTextColor { get => _btnCustomTextColor; set { _btnCustomTextColor = value; OnPropertyChanged(); } }

    [ObservableProperty] private string _customDaysLabel = "Custom";

    // Plain English summary
    [ObservableProperty] private string _plainEnglishSummary = string.Empty;
    [ObservableProperty] private string _pcpLine = string.Empty;

    // Tab buttons — background colors
    private Color _tabBpColor = Colors.Transparent;
    public Color TabBpColor { get => _tabBpColor; set { _tabBpColor = value; OnPropertyChanged(); } }

    private Color _tabHrColor = Colors.Transparent;
    public Color TabHrColor { get => _tabHrColor; set { _tabHrColor = value; OnPropertyChanged(); } }

    private Color _tabSpo2Color = Colors.Transparent;
    public Color TabSpo2Color { get => _tabSpo2Color; set { _tabSpo2Color = value; OnPropertyChanged(); } }

    private Color _tabTempColor = Colors.Transparent;
    public Color TabTempColor { get => _tabTempColor; set { _tabTempColor = value; OnPropertyChanged(); } }

    private Color _tabWeightColor = Colors.Transparent;
    public Color TabWeightColor { get => _tabWeightColor; set { _tabWeightColor = value; OnPropertyChanged(); } }

    private Color _tabGlucoseColor = Colors.Transparent;
    public Color TabGlucoseColor { get => _tabGlucoseColor; set { _tabGlucoseColor = value; OnPropertyChanged(); } }

    // Tab buttons — text colors
    private Color _tabBpTextColor = Colors.White;
    public Color TabBpTextColor { get => _tabBpTextColor; set { _tabBpTextColor = value; OnPropertyChanged(); } }

    private Color _tabHrTextColor = Colors.White;
    public Color TabHrTextColor { get => _tabHrTextColor; set { _tabHrTextColor = value; OnPropertyChanged(); } }

    private Color _tabSpo2TextColor = Colors.White;
    public Color TabSpo2TextColor { get => _tabSpo2TextColor; set { _tabSpo2TextColor = value; OnPropertyChanged(); } }

    private Color _tabTempTextColor = Colors.White;
    public Color TabTempTextColor { get => _tabTempTextColor; set { _tabTempTextColor = value; OnPropertyChanged(); } }

    private Color _tabWeightTextColor = Colors.White;
    public Color TabWeightTextColor { get => _tabWeightTextColor; set { _tabWeightTextColor = value; OnPropertyChanged(); } }

    private Color _tabGlucoseTextColor = Colors.White;
    public Color TabGlucoseTextColor { get => _tabGlucoseTextColor; set { _tabGlucoseTextColor = value; OnPropertyChanged(); } }

    [ObservableProperty] private bool _showBp = true;
    [ObservableProperty] private bool _showHr = false;
    [ObservableProperty] private bool _showSpo2 = false;
    [ObservableProperty] private bool _showTemp = false;
    [ObservableProperty] private bool _showWeight = false;
    [ObservableProperty] private bool _showGlucose = false;

    // BP's insufficient-data state belongs to the BP tab only. Optional
    // analyses (HR/SpO2/Temperature/Weight/Glucose) can still have useful
    // results even when BP has fewer than its own 7-reading gate.
    public bool ShowBpInsufficient => ShowBp && IsInsufficient;
    public bool ShowBpAnalysis => ShowBp && IsOk;

    // Which optional analysis tabs the selected patient tracks.
    // These are patient-scoped through UserPreferencesService.
    [ObservableProperty] private bool _trackHeartRate = true;
    [ObservableProperty] private bool _trackSpo2 = true;
    [ObservableProperty] private bool _trackTemperature = true;
    [ObservableProperty] private bool _trackWeight;
    [ObservableProperty] private bool _trackGlucose;

    // Secondary plain English
    [ObservableProperty] private string _hrSummary = string.Empty;
    [ObservableProperty] private string _hrPcpLine = string.Empty;
    [ObservableProperty] private string _spo2Summary = string.Empty;
    [ObservableProperty] private string _spo2PcpLine = string.Empty;
    [ObservableProperty] private string _tempSummary = string.Empty;
    [ObservableProperty] private string _tempPcpLine = string.Empty;
    [ObservableProperty] private string _weightSummary = string.Empty;
    [ObservableProperty] private string _weightPcpLine = string.Empty;
    [ObservableProperty] private string _glucoseSummary = string.Empty;
    [ObservableProperty] private string _glucosePcpLine = string.Empty;

    [ObservableProperty] private bool _showDiastolicWarning = false;
    [ObservableProperty] private string _diastolicWarningText = string.Empty;

    partial void OnShowBpChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowBpInsufficient));
        OnPropertyChanged(nameof(ShowBpAnalysis));
    }

    partial void OnIsInsufficientChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowBpInsufficient));
    }

    partial void OnIsOkChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowBpAnalysis));
    }

    public VitalsAnalysisViewModel(
        ApiService api,
        PatientStateService patientState,
        UserPreferencesService preferences)
    {
        _api = api;
        _patientState = patientState;
        _preferences = preferences;
    }

    public async Task LoadAsync(int days = 15)
    {
        SelectedDays = days;
        UpdateButtonColors(days);

        var patientId = _patientState.SelectedPatient?.PatientId;
        if (!string.IsNullOrWhiteSpace(patientId))
            ApplyPreferences(await _preferences.RefreshAsync(patientId));

        SelectTab("bp");
        await RunAnalysisAsync();
    }

    private void ApplyPreferences(UserPreferences preferences)
    {
        TrackHeartRate = preferences.ShowHeartRate;
        TrackSpo2 = preferences.ShowSpo2;
        TrackTemperature = preferences.ShowTemperature;
        TrackWeight = preferences.ShowWeight;
        TrackGlucose = preferences.ShowGlucose;
    }

    [RelayCommand]
    public async Task SelectDaysAsync(string days)
    {
        if (int.TryParse(days, out var d))
        {
            SelectedDays = d;
            UpdateButtonColors(d);
            CustomDaysLabel = "Custom";
            await RunAnalysisAsync();
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
            await RunAnalysisAsync();
        }
    }

    private async Task RunAnalysisAsync()
    {
        SelectTab("bp");
        if (_patientState.SelectedPatient is null) return;

        IsBusy = true;
        StatusMessage = string.Empty;

        try
        {
            var result = await _api.GetVitalsAnalysisAsync(
                _patientState.SelectedPatient.PatientId, SelectedDays);

            Analysis = result;
            OnPropertyChanged(nameof(SelectedMetricReadingCount));

            if (result is null)
            {
                StatusMessage = "Could not load analysis. Check your connection.";
                IsInsufficient = false;
                IsOk = false;
                HasAnalysisResponse = false;
                HasAnalysisResults = false;
                return;
            }

            IsInsufficient = result.IsInsufficient;
            IsOk = result.IsOk;
            HasAnalysisResponse = true;
            HasAnalysisResults =
                result.IsOk ||
                (TrackHeartRate && result.HeartRate is not null) ||
                (TrackSpo2 && result.Spo2 is not null) ||
                (TrackTemperature && result.Temperature is not null) ||
                (TrackWeight && result.Weight is not null) ||
                (TrackGlucose && result.Glucose is not null);

            if (result.IsOk)
            {
                BuildPlainEnglish(result);
                BuildPcpLine(result);
            }

            // Optional vital analyses are independent of whether BP has
            // enough readings for its own full analysis.
            BuildHrSummary(result);
            BuildSpo2Summary(result);
            BuildTempSummary(result);
            BuildWeightSummary(result);
            BuildGlucoseSummary(result);

            // Blood Pressure is the app's primary vital, so Analysis always
            // opens on BP even when BP has not yet met its own data threshold.
            // The BP tab can show its scoped "Not Enough Data Yet" state while
            // the user deliberately chooses another available vital if desired.
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateButtonColors(int days)
    {
        if (Application.Current?.Resources is null) return;

        var res = Application.Current.Resources;
        var active = res.TryGetValue("ButtonBackground", out var a) ? (Color)a : Color.FromArgb("#00acc1");
        var inactive = res.TryGetValue("ButtonSecondary", out var i) ? (Color)i : Color.FromArgb("#b2dff2");
        var activeTxt = res.TryGetValue("TextPrimary", out var at) ? (Color)at : Colors.White;
        var inactiveTxt = res.TryGetValue("ButtonSecondaryText", out var it) ? (Color)it : Color.FromArgb("#0d2137");

        Btn15Color = days == 15 ? active : inactive;
        Btn30Color = days == 30 ? active : inactive;
        Btn45Color = days == 45 ? active : inactive;
        Btn60Color = days == 60 ? active : inactive;
        BtnCustomColor = days == -1 ? active : inactive;

        Btn15TextColor = days == 15 ? activeTxt : inactiveTxt;
        Btn30TextColor = days == 30 ? activeTxt : inactiveTxt;
        Btn45TextColor = days == 45 ? activeTxt : inactiveTxt;
        Btn60TextColor = days == 60 ? activeTxt : inactiveTxt;
        BtnCustomTextColor = days == -1 ? activeTxt : inactiveTxt;
    }

    [RelayCommand]
    public void SelectTab(string tab)
    {
        if (Application.Current?.Resources is null) return;

        var res = Application.Current.Resources;
        var active = res.TryGetValue("ButtonBackground", out var a) ? (Color)a : Color.FromArgb("#00acc1");
        var inactive = res.TryGetValue("ButtonSecondary", out var i) ? (Color)i : Color.FromArgb("#b2dff2");
        var activeTxt = res.TryGetValue("TextPrimary", out var at) ? (Color)at : Colors.White;
        var inactiveTxt = res.TryGetValue("ButtonSecondaryText", out var it) ? (Color)it : Color.FromArgb("#0d2137");

        // Ignore stale/programmatic requests for a tab the user no longer
        // tracks. BP is always available and is the safe fallback.
        if ((tab == "hr" && !TrackHeartRate) ||
            (tab == "spo2" && !TrackSpo2) ||
            (tab == "temp" && !TrackTemperature) ||
            (tab == "weight" && !TrackWeight) ||
            (tab == "glucose" && !TrackGlucose))
        {
            tab = "bp";
        }

        ShowBp = tab == "bp";
        ShowHr = tab == "hr";
        ShowSpo2 = tab == "spo2";
        ShowTemp = tab == "temp";
        ShowWeight = tab == "weight";
        ShowGlucose = tab == "glucose";

        TabBpColor = tab == "bp" ? active : inactive;
        TabHrColor = tab == "hr" ? active : inactive;
        TabSpo2Color = tab == "spo2" ? active : inactive;
        TabTempColor = tab == "temp" ? active : inactive;
        TabWeightColor = tab == "weight" ? active : inactive;
        TabGlucoseColor = tab == "glucose" ? active : inactive;

        TabBpTextColor = tab == "bp" ? activeTxt : inactiveTxt;
        TabHrTextColor = tab == "hr" ? activeTxt : inactiveTxt;
        TabSpo2TextColor = tab == "spo2" ? activeTxt : inactiveTxt;
        TabTempTextColor = tab == "temp" ? activeTxt : inactiveTxt;
        TabWeightTextColor = tab == "weight" ? activeTxt : inactiveTxt;
        TabGlucoseTextColor = tab == "glucose" ? activeTxt : inactiveTxt;

        OnPropertyChanged(nameof(SelectedMetricReadingCount));
    }

    private void BuildPlainEnglish(VitalsAnalysis a)
    {
        if (a.Systolic is null) return;

        var sys = a.Systolic;

        ShowDiastolicWarning = false;
        DiastolicWarningText = string.Empty;

        if (a.Classification == "borderline_hypotension" &&
            a.Diastolic is not null &&
            a.Diastolic.Significant &&
            a.Diastolic.Slope > 0.2 &&
            !sys.Significant)
        {
            ShowDiastolicWarning = true;
            var momentumNote = a.Diastolic.Momentum == "accelerating"
                ? " The rate of increase is accelerating."
                : string.Empty;
            DiastolicWarningText =
                $"⚠️ While pumping pressure appears stable, resting pressure between beats " +
                $"has been rising at a statistically significant rate " +
                $"({a.Diastolic.SlopeDisplay} mmHg/day).{momentumNote} " +
                $"This pattern warrants discussion with her care team.";
        }

        if (a.Classification == "hypotension")
        {
            PlainEnglishSummary =
                $"Average blood pressure of {a.Systolic.Avg:F1}/{a.Diastolic?.Avg:F1} mmHg " +
                $"is below normal range. Low blood pressure can cause dizziness, fatigue, " +
                $"and fainting. If experiencing symptoms, contact the care team.";
            BuildBurdenSummary(a);
            return;
        }

        if (a.Classification == "borderline_hypotension")
        {
            var hypoBelow = a.HypoBurden is not null
                ? $" About {a.HypoBurden.ModeratePct + a.HypoBurden.SeverePct:F0}% of readings " +
                  $"fell below 90 mmHg" +
                  (a.HypoBurden.SeverePct >= 5
                      ? $", including {a.HypoBurden.SeverePct:F0}% in the severe range below 80 mmHg."
                      : ".")
                : string.Empty;

            PlainEnglishSummary =
                $"Average blood pressure of {a.Systolic.Avg:F1}/{a.Diastolic?.Avg:F1} mmHg " +
                $"is at the lower end of the normal range. While not critically low, this " +
                $"pattern is worth monitoring — especially for symptoms like lightheadedness " +
                $"or dizziness on standing.{hypoBelow} " +
                $"Recording at consistent times and noting any symptoms will help the care " +
                $"team assess this pattern.";
            BuildBurdenSummary(a);
            return;
        }

        var parts = new List<string>();

        parts.Add(sys.Trend switch
        {
            "rising_significant" =>
                $"Pumping pressure has been rising at a statistically significant rate " +
                $"({sys.SlopeDisplay}) over the past {SelectedDays} days.",
            "rising" =>
                $"Pumping pressure has been gradually rising ({sys.SlopeDisplay}) over " +
                $"the past {SelectedDays} days, though the trend is not yet statistically significant.",
            "falling_significant" =>
                $"Pumping pressure has been falling at a statistically significant rate " +
                $"({sys.SlopeDisplay}) over the past {SelectedDays} days — a positive sign.",
            "falling" =>
                $"Pumping pressure has been gradually improving ({sys.SlopeDisplay}) " +
                $"over the past {SelectedDays} days.",
            "stable" =>
                $"Pumping pressure has been stable over the past {SelectedDays} days.",
            _ => string.Empty
        });

        if (sys.Trend is "rising_significant" or "rising")
        {
            parts.Add(sys.Momentum switch
            {
                "accelerating" => "The rate of increase is accelerating.",
                "decelerating" => "The rate of increase appears to be slowing down.",
                _ => string.Empty
            });
        }
        else if (sys.Trend is "falling_significant" or "falling")
        {
            parts.Add(sys.Momentum switch
            {
                "accelerating" => "The improvement is continuing to accelerate.",
                "decelerating" => "The improvement is slowing down.",
                _ => string.Empty
            });
        }

        parts.Add(sys.Consistency switch
        {
            "high" => "Readings are very consistent, which improves the reliability of this analysis.",
            "moderate" => "Readings show moderate variability. Recording at the same time each day gives a more accurate picture.",
            "low" => "Readings are quite variable. Recording at the same time each day will help identify clearer trends.",
            _ => string.Empty
        });

        if (a.Diastolic is not null && a.Diastolic.Significant && a.Diastolic.Slope > 0.2)
        {
            var diaExtra = a.Diastolic.Momentum == "accelerating"
                ? " The rate of increase is accelerating."
                : a.Diastolic.Momentum == "decelerating"
                    ? " The rate of increase appears to be slowing down."
                    : string.Empty;
            parts.Add(
                $"Notably, resting pressure between beats has been rising at a statistically " +
                $"significant rate ({a.Diastolic.SlopeDisplay} mmHg/day) — worth monitoring " +
                $"even if pumping pressure appears stable.{diaExtra}");
        }
        else if (a.Diastolic is not null && a.Diastolic.Significant && a.Diastolic.Slope < -0.2)
        {
            parts.Add(
                $"Resting pressure between beats has been falling at a statistically " +
                $"significant rate ({a.Diastolic.SlopeDisplay} mmHg/day) — a positive sign.");
        }

        PlainEnglishSummary = string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        BuildBurdenSummary(a);
    }

    private void BuildPcpLine(VitalsAnalysis a)
    {
        if (string.IsNullOrEmpty(a.PcpName))
        {
            PcpLine = string.Empty;
            return;
        }

        bool diastolicRising = a.Diastolic is not null &&
                               a.Diastolic.Significant &&
                               a.Diastolic.Slope > 0.2;

        if (!string.IsNullOrEmpty(a.NextFollowup))
            PcpLine = a.Classification switch
            {
                "hypotension" =>
                    $"Please discuss the low blood pressure readings with {a.PcpName} " +
                    $"at the appointment on {a.NextFollowup}.",
                "borderline_hypotension" when diastolicRising =>
                    $"Consider discussing the borderline-low pumping pressure and the " +
                    $"rising resting pressure trend with {a.PcpName} at the appointment " +
                    $"on {a.NextFollowup}.",
                "borderline_hypotension" =>
                    $"Consider mentioning the borderline-low blood pressure readings to " +
                    $"{a.PcpName} at the appointment on {a.NextFollowup}.",
                _ =>
                    $"Consider sharing this summary with {a.PcpName} at the next " +
                    $"appointment on {a.NextFollowup}."
            };
        else
            PcpLine = a.Classification switch
            {
                "hypotension" =>
                    $"Please discuss the low blood pressure readings with {a.PcpName} " +
                    $"at the next visit.",
                "borderline_hypotension" when diastolicRising =>
                    $"Consider discussing the borderline-low pumping pressure and the " +
                    $"rising resting pressure trend with {a.PcpName} at the next visit.",
                "borderline_hypotension" =>
                    $"Consider mentioning the borderline-low blood pressure readings to " +
                    $"{a.PcpName} at the next visit.",
                _ =>
                    $"You may want to discuss these results with {a.PcpName} during " +
                    $"the next visit."
            };
    }

    private void BuildHrSummary(VitalsAnalysis a)
    {
        if (a.HeartRate is null)
        {
            HrSummary = string.Empty;
            HrPcpLine = string.Empty;
            return;
        }

        var hr = a.HeartRate;
        var parts = new List<string>();

        // -----------------------------------------------------
        // 1. Establish the current/resting picture
        // -----------------------------------------------------
        if (hr.RestingSummary is not null)
        {
            var rs = hr.RestingSummary;

            parts.Add(
                $"Across {rs.N} resting readings recorded on {rs.DistinctDays} days, " +
                $"heart rate averaged {rs.Mean:F0} BPM, with a typical value near " +
                $"{rs.Median:F0} BPM and a recorded range of {rs.Min}\u2013{rs.Max} BPM.");
        }
        else if (hr.Bpm is not null)
        {
            parts.Add(
                $"The latest heart rate was {hr.Bpm} BPM " +
                $"({hr.ActivityContextDisplay.ToLower()}, {hr.PostureDisplay.ToLower()}). " +
                "More resting readings are needed before Vitals can describe a reliable longer-term pattern.");
        }

        // -----------------------------------------------------
        // 2. Longitudinal trend
        // -----------------------------------------------------
        if (hr.Trend is not null)
        {
            var trend = hr.Trend;

            parts.Add(trend.TrendLabel switch
            {
                "rising_significant" =>
                    $"Resting heart rate has been rising over this period. " +
                    $"The modeled rate of change is {trend.SlopeDisplay}, and the trend is " +
                    $"statistically significant.",

                "rising" =>
                    $"Resting heart rate has shown a gradual upward pattern " +
                    $"({trend.SlopeDisplay}), but the available readings do not yet show " +
                    $"a statistically significant trend.",

                "falling_significant" =>
                    $"Resting heart rate has been falling over this period. " +
                    $"The modeled rate of change is {trend.SlopeDisplay}, and the trend is " +
                    $"statistically significant.",

                "falling" =>
                    $"Resting heart rate has shown a gradual downward pattern " +
                    $"({trend.SlopeDisplay}), but the available readings do not yet show " +
                    $"a statistically significant trend.",

                _ =>
                    "Resting heart rate has remained generally stable over this period."
            });

            // R²/consistency describes how closely readings follow the trend,
            // not whether individual readings themselves are "good" or "bad".
            parts.Add(trend.Consistency switch
            {
                "high" =>
                    "The readings follow this overall pattern fairly consistently.",

                "moderate" =>
                    "There is some day-to-day variation around the overall trend.",

                "low" =>
                    "The readings vary considerably around the trend line, so the overall " +
                    "direction should be interpreted cautiously.",

                _ => string.Empty
            });
        }

        // -----------------------------------------------------
        // 3. Personal-baseline comparison
        // -----------------------------------------------------
        if (hr.BaselineDeviation is not null)
        {
            var bd = hr.BaselineDeviation;

            if (Math.Abs(bd.DeltaBpm) >= 3)
            {
                var direction = bd.DeltaBpm > 0 ? "higher" : "lower";

                parts.Add(
                    $"More recently, the typical resting rate has been {bd.RecentMedian:F0} BPM, " +
                    $"which is about {Math.Abs(bd.DeltaBpm):F0} BPM {direction} than the earlier " +
                    $"personal baseline of {bd.BaselineMedian:F0} BPM.");
            }
            else
            {
                parts.Add(
                    $"The recent resting rate is close to the established personal baseline " +
                    $"({bd.RecentMedian:F0} versus {bd.BaselineMedian:F0} BPM).");
            }
        }

        // -----------------------------------------------------
        // 4. High / low resting observations
        // -----------------------------------------------------
        if (hr.RateEvents is not null &&
            hr.RateEvents.NRestingInWindow > 0)
        {
            var events = hr.RateEvents;
            var eventParts = new List<string>();

            if (events.High is not null && events.High.Count > 0)
            {
                eventParts.Add(
                    $"{events.High.Count} resting " +
                    $"{(events.High.Count == 1 ? "reading was" : "readings were")} " +
                    $"above {events.Thresholds?.High} BPM");
            }

            if (events.Low is not null && events.Low.Count > 0)
            {
                eventParts.Add(
                    $"{events.Low.Count} resting " +
                    $"{(events.Low.Count == 1 ? "reading was" : "readings were")} " +
                    $"below {events.Thresholds?.Low} BPM");
            }

            if (eventParts.Count > 0)
            {
                parts.Add(
                    $"{string.Join(" and ", eventParts)}. These are recorded observations, " +
                    "not a diagnosis of a heart rhythm condition.");
            }

            if (events.MarkedLowCount > 0)
            {
                parts.Add(
                    $"{events.MarkedLowCount} " +
                    $"{(events.MarkedLowCount == 1 ? "reading was" : "readings were")} " +
                    $"below {events.Thresholds?.MarkedLow} BPM. Symptoms occurring near these " +
                    "readings are important context for the care team.");
            }
        }

        // -----------------------------------------------------
        // 5. Time-of-day pattern
        // -----------------------------------------------------
        if (hr.TimeOfDay?.PatternSummary is not null &&
            hr.TimeOfDay.Buckets is not null)
        {
            var pattern = hr.TimeOfDay.PatternSummary;

            var highBucket = GetHrTimeBucket(
                hr.TimeOfDay.Buckets, pattern.HighestPeriod);

            var lowBucket = GetHrTimeBucket(
                hr.TimeOfDay.Buckets, pattern.LowestPeriod);

            // Avoid making a dramatic statement about trivial differences.
            // 5 BPM is a Vitals presentation gate, not a clinical threshold.
            if (highBucket is not null &&
                lowBucket is not null &&
                Math.Abs(highBucket.Median - lowBucket.Median) >= 5)
            {
                parts.Add(
                    $"A time-of-day pattern is also visible: resting readings have tended to be " +
                    $"highest during {FormatHrPeriod(pattern.HighestPeriod)} " +
                    $"(median {highBucket.Median:F0} BPM) and lowest during " +
                    $"{FormatHrPeriod(pattern.LowestPeriod)} " +
                    $"(median {lowBucket.Median:F0} BPM).");
            }
        }

        // -----------------------------------------------------
        // 6. Symptom association
        // -----------------------------------------------------
        if (hr.SymptomAssociation is not null &&
            hr.SymptomAssociation.Count > 0)
        {
            // Keep the Plain English section readable. Pick the symptom with
            // the most associated observations and leave full detail in its card.
            var strongestSymptom = hr.SymptomAssociation
                .Where(x => x.Value.AssociatedCount > 0)
                .OrderByDescending(x => x.Value.AssociatedCount)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(strongestSymptom.Key))
            {
                var symptom = strongestSymptom.Value;
                var observations = new List<string>();

                if (symptom.PctHigh is > 0)
                    observations.Add($"{symptom.PctHigh:F0}% occurred with a high-rate reading");

                if (symptom.PctLow is > 0)
                    observations.Add($"{symptom.PctLow:F0}% occurred with a low-rate reading");

                if (observations.Count > 0)
                {
                    parts.Add(
                        $"For readings associated with {FormatSymptomName(strongestSymptom.Key)}, " +
                        $"{string.Join(" and ", observations)}. This shows timing and association only; " +
                        "it does not establish that the heart-rate change caused the symptom.");
                }
            }
        }

        // -----------------------------------------------------
        // 7. Medication-change association
        // -----------------------------------------------------
        if (hr.MedicationAssociations is not null &&
            hr.MedicationAssociations.Count > 0)
        {
            var association = hr.MedicationAssociations
                .OrderByDescending(x => Math.Abs(x.DeltaBpm))
                .First();

            var direction = association.DeltaBpm >= 0 ? "higher" : "lower";

            var medicationSentence =
                $"Around the recorded {association.ChangeTypeDisplay.ToLower()} for " +
                $"{association.MedicationName}, median resting heart rate was " +
                $"{association.PreMedian:F0} BPM before the change and " +
                $"{association.PostMedian:F0} BPM afterward " +
                $"({Math.Abs(association.DeltaBpm):F0} BPM {direction}).";

            if (association.Confounded)
            {
                medicationSentence +=
                    " Other changes occurred during the same period, so this comparison " +
                    "cannot be attributed to that medication alone.";
            }
            else
            {
                medicationSentence +=
                    " This is a timing association and does not establish that the medication " +
                    "caused the change.";
            }

            parts.Add(medicationSentence);
        }

        // -----------------------------------------------------
        // 8. Irregular-pulse observation
        // -----------------------------------------------------
        if (hr.IrregularPulseFlag == true)
        {
            parts.Add(
                "An irregular-pulse indication was recorded with at least one measurement. " +
                "A pulse-rate reading alone cannot identify the heart rhythm, so this finding " +
                "is best reviewed with the care team, particularly if symptoms were present.");
        }

        HrSummary = string.Join(
            " ",
            parts.Where(p => !string.IsNullOrWhiteSpace(p)));

        BuildHrPcpLine(a);
    }

    private static HrBucketStats? GetHrTimeBucket(
        HrTimeOfDayBuckets buckets,
        string period)
    {
        return period switch
        {
            "morning" => buckets.Morning,
            "afternoon" => buckets.Afternoon,
            "evening" => buckets.Evening,
            "overnight" => buckets.Overnight,
            _ => null
        };
    }

    private void BuildHrPcpLine(VitalsAnalysis a)
    {
        if (a.HeartRate is null || string.IsNullOrWhiteSpace(a.PcpName))
        {
            HrPcpLine = string.Empty;
            return;
        }

        var hr = a.HeartRate;

        bool hasNotableEvents =
            (hr.RateEvents?.High?.Count ?? 0) > 0 ||
            (hr.RateEvents?.Low?.Count ?? 0) > 0 ||
            (hr.RateEvents?.MarkedLowCount ?? 0) > 0;

        bool hasSignificantTrend =
            hr.Trend?.TrendLabel is
                "rising_significant" or
                "falling_significant";

        bool hasBaselineChange =
            hr.BaselineDeviation is not null &&
            Math.Abs(hr.BaselineDeviation.DeltaBpm) >= 5;

        bool hasIrregularPulse =
            hr.IrregularPulseFlag == true;

        bool hasSymptomAssociation =
            hr.SymptomAssociation is not null &&
            hr.SymptomAssociation.Count > 0;

        var somethingToDiscuss =
            hasNotableEvents ||
            hasSignificantTrend ||
            hasBaselineChange ||
            hasIrregularPulse ||
            hasSymptomAssociation;

        if (!somethingToDiscuss)
        {
            HrPcpLine = !string.IsNullOrWhiteSpace(a.NextFollowup)
                ? $"Consider sharing this heart-rate summary with {a.PcpName} at the appointment on {a.NextFollowup}."
                : $"Consider sharing this heart-rate summary with {a.PcpName} at the next visit.";

            return;
        }

        HrPcpLine = !string.IsNullOrWhiteSpace(a.NextFollowup)
            ? $"Consider discussing the heart-rate patterns highlighted above with {a.PcpName} at the appointment on {a.NextFollowup}."
            : $"Consider discussing the heart-rate patterns highlighted above with {a.PcpName} at the next visit.";
    }

    private static string FormatHrPeriod(string period)
    {
        return period switch
        {
            "morning" => "the morning",
            "afternoon" => "the afternoon",
            "evening" => "the evening",
            "overnight" => "overnight",
            _ => period.Replace("_", " ")
        };
    }

    private static string FormatSymptomName(string symptom)
    {
        if (string.IsNullOrWhiteSpace(symptom))
            return symptom;

        return symptom.Replace("_", " ").ToLowerInvariant();
    }

    private void BuildSpo2Summary(VitalsAnalysis a)
    {
        if (a.Spo2 is null)
        {
            Spo2Summary = string.Empty;
            Spo2PcpLine = string.Empty;
            return;
        }

        var spo2 = a.Spo2;
        var parts = new List<string>();

        // -----------------------------------------------------
        // 1. Central tendency (all logged readings, not resting-
        //    filtered — there's no spo2_context table yet)
        // -----------------------------------------------------
        if (spo2.SpotSummary is not null)
        {
            var ss = spo2.SpotSummary;
            parts.Add(
                $"Across {ss.N} logged readings on {ss.DistinctDays} days, oxygen " +
                $"saturation averaged {ss.Mean:F1}%, with a typical value near " +
                $"{ss.Median:F1}% and a recorded range of {ss.Min}\u2013{ss.Max}%.");
        }
        else if (spo2.Spo2 is not null)
        {
            parts.Add(
                $"The latest oxygen saturation reading was {spo2.Spo2}%. " +
                "More logged readings are needed before Vitals can describe a reliable pattern.");
        }

        // -----------------------------------------------------
        // 2. Trend
        // -----------------------------------------------------
        if (spo2.Trend is not null)
        {
            var trend = spo2.Trend;

            parts.Add(trend.TrendLabel switch
            {
                "rising_significant" =>
                    $"Oxygen saturation has been rising over this period. The modeled rate " +
                    $"of change is {trend.SlopeDisplay}, and the trend is statistically significant.",
                "rising" =>
                    $"Oxygen saturation has shown a gradual upward pattern ({trend.SlopeDisplay}), " +
                    "but the available readings do not yet show a statistically significant trend.",
                "falling_significant" =>
                    $"Oxygen saturation has been falling over this period. The modeled rate " +
                    $"of change is {trend.SlopeDisplay}, and the trend is statistically significant.",
                "falling" =>
                    $"Oxygen saturation has shown a gradual downward pattern ({trend.SlopeDisplay}), " +
                    "but the available readings do not yet show a statistically significant trend.",
                _ =>
                    "Oxygen saturation has remained generally stable over this period."
            });

            if (trend.TrendLabel != "stable")
            {
                parts.Add(trend.Consistency switch
                {
                    "high" =>
                        "The readings follow this overall direction fairly consistently.",
                    "moderate" =>
                        "The readings show some variation around the overall direction.",
                    "low" =>
                        "The readings do not closely follow a straight-line trend, so the " +
                        "direction should be interpreted cautiously.",
                    _ => string.Empty
                });
            }
        }

        // -----------------------------------------------------
        // 3. Personal-baseline comparison
        // -----------------------------------------------------
        if (spo2.BaselineDeviation is not null)
        {
            var bd = spo2.BaselineDeviation;

            if (Math.Abs(bd.DeltaPctPoints) >= 1)
            {
                var direction = bd.DeltaPctPoints > 0 ? "higher" : "lower";
                parts.Add(
                    $"More recently, the typical reading has been {bd.RecentMedian:F1}%, " +
                    $"which is about {Math.Abs(bd.DeltaPctPoints):F1} points {direction} than " +
                    $"the earlier personal baseline of {bd.BaselineMedian:F1}%.");
            }
            else
            {
                parts.Add(
                    $"The recent readings are close to the established personal baseline " +
                    $"({bd.RecentMedian:F1}% versus {bd.BaselineMedian:F1}%).");
            }
        }

        // -----------------------------------------------------
        // 4. Low observations, confirmed episodes, marked-low
        // -----------------------------------------------------
        if (spo2.LowObservations is not null && spo2.LowObservations.Count > 0)
        {
            var low = spo2.LowObservations;
            var pctNote = low.HasPct ? $" ({low.PctOfLoggedReadings:F0}% of logged readings)" : string.Empty;

            parts.Add(
                $"{low.Count} logged reading{(low.Count == 1 ? " was" : "s were")} below " +
                $"{low.Threshold}%{pctNote}. These are recorded observations, not a diagnosis " +
                "of a breathing or oxygenation condition.");

            if (spo2.HasConfirmedEpisodes)
            {
                var ep = spo2.ConfirmedLowObservations!;
                parts.Add(
                    $"{ep.EpisodeCount} of these {(ep.EpisodeCount == 1 ? "was a repeat-confirmed episode" : "were repeat-confirmed episodes")} " +
                    $"\u2014 at least two below-target readings within {ep.ConfirmationRuleMinutes} minutes of each other. " +
                    "Confirmation improves confidence but does not establish a diagnosis on its own.");
            }

            if (spo2.HasMarkedLow)
            {
                var marked = spo2.MarkedLowObservations!;
                parts.Add(
                    $"{marked.Count} reading{(marked.Count == 1 ? " was" : "s were")} below " +
                    $"{marked.Threshold}%. Symptoms occurring near these readings are important " +
                    "context for the care team.");
            }
        }

        // -----------------------------------------------------
        // 5. Time-of-day pattern
        // -----------------------------------------------------
        if (spo2.HasTimeOfDayPattern)
        {
            var pattern = spo2.TimeOfDay!.PatternSummary!;
            parts.Add(
                $"A time-of-day pattern is also visible: readings have tended to be highest " +
                $"during {FormatSpo2Period(pattern.HighestPeriod)} and lowest during " +
                $"{FormatSpo2Period(pattern.LowestPeriod)}, a difference of about " +
                $"{pattern.MedianDelta:F1} points.");
        }

        Spo2Summary = string.Join(
            " ",
            parts.Where(p => !string.IsNullOrWhiteSpace(p)));

        BuildSpo2PcpLine(a);
    }

    private void BuildSpo2PcpLine(VitalsAnalysis a)
    {
        if (a.Spo2 is null || string.IsNullOrWhiteSpace(a.PcpName))
        {
            Spo2PcpLine = string.Empty;
            return;
        }

        var spo2 = a.Spo2;

        bool hasLowObservations = (spo2.LowObservations?.Count ?? 0) > 0;
        bool hasConfirmedEpisodes = spo2.HasConfirmedEpisodes;
        bool hasMarkedLow = spo2.HasMarkedLow;

        bool hasSignificantTrend =
            spo2.Trend?.TrendLabel is "rising_significant" or "falling_significant";

        bool hasBaselineChange =
            spo2.BaselineDeviation is not null &&
            Math.Abs(spo2.BaselineDeviation.DeltaPctPoints) >= 2;

        var somethingToDiscuss =
            hasLowObservations ||
            hasConfirmedEpisodes ||
            hasMarkedLow ||
            hasSignificantTrend ||
            hasBaselineChange;

        if (!somethingToDiscuss)
        {
            Spo2PcpLine = !string.IsNullOrWhiteSpace(a.NextFollowup)
                ? $"Consider sharing this oxygen-saturation summary with {a.PcpName} at the appointment on {a.NextFollowup}."
                : $"Consider sharing this oxygen-saturation summary with {a.PcpName} at the next visit.";

            return;
        }

        Spo2PcpLine = !string.IsNullOrWhiteSpace(a.NextFollowup)
            ? $"Consider discussing the oxygen-saturation patterns highlighted above with {a.PcpName} at the appointment on {a.NextFollowup}."
            : $"Consider discussing the oxygen-saturation patterns highlighted above with {a.PcpName} at the next visit.";
    }

    private static string FormatSpo2Period(string period)
    {
        return period switch
        {
            "morning" => "the morning",
            "afternoon" => "the afternoon",
            "evening" => "the evening",
            "overnight" => "overnight",
            _ => period.Replace("_", " ")
        };
    }

    private void BuildTempSummary(VitalsAnalysis a)
    {
        var temp = a.Temperature;

        if (temp?.Latest is not { } latest)
        {
            TempSummary = string.Empty;
            TempPcpLine = string.Empty;
            return;
        }

        var range = temp.RangeEvents;
        var parts = new List<string>();

        // -----------------------------------------------------
        // 1. Latest temperature + measurement context
        // -----------------------------------------------------
        if (latest.HasKnownSite)
        {
            parts.Add(
                $"The latest {latest.SiteDisplay.ToLowerInvariant()} temperature was " +
                $"{latest.ValueF:F1}°F ({latest.ValueC:F1}°C).");
        }
        else
        {
            parts.Add(
                $"The latest temperature was {latest.ValueF:F1}°F ({latest.ValueC:F1}°C). " +
                "The measurement site was not recorded, which limits direct comparison " +
                "with site-specific temperature baselines.");
        }

        // -----------------------------------------------------
        // 2. Personal same-site baseline
        // -----------------------------------------------------
        if (temp.Baseline is not null)
        {
            var baseline = temp.Baseline;
            var delta = baseline.DeltaCurrentF;

            var deltaText = Math.Abs(delta) < 0.05
                ? "about the same as"
                : delta > 0
                    ? $"{Math.Abs(delta):F1}°F above"
                    : $"{Math.Abs(delta):F1}°F below";

            parts.Add(
                $"The established {baseline.SiteDisplay.ToLowerInvariant()}-temperature " +
                $"baseline is about {baseline.MedianF:F1}°F, based on {baseline.N} readings " +
                $"across {baseline.DistinctDays} days. The latest reading is {deltaText} " +
                "that personal baseline.");
        }
        else
        {
            var baselineReason = temp.DataSupport?.UnavailableAnalyses
                .FirstOrDefault(x => x.AnalysisName == "personal_baseline");

            parts.Add(baselineReason?.ReasonCode switch
            {
                "measurement_site_unknown" =>
                    "A personal temperature baseline is not available because the measurement " +
                    "site is unknown. Recording future temperatures with the same known method " +
                    "will make comparisons more reliable.",

                "mixed_measurement_sites" =>
                    "A personal temperature baseline is not available because measurement sites " +
                    "are mixed. Same-method readings are more comparable.",

                "insufficient_same_site_baseline" =>
                    "There are not enough same-site readings yet to establish a personal " +
                    "temperature baseline.",

                _ => string.Empty
            });
        }

        // -----------------------------------------------------
        // 3. Fever-range logged observations + febrile days
        // -----------------------------------------------------
        if (range is not null)
        {
            if (range.FeverCount > 0)
            {
                parts.Add(
                    $"{range.FeverCount} of {temp.ReadingCount} logged " +
                    $"{(temp.ReadingCount == 1 ? "reading" : "readings")} " +
                    $"({range.FeverLoggedPct:F1}%) were in the fever range at or above the " +
                    $"configured {range.FeverThresholdF:F1}°F reference. " +
                    $"Fever-range readings were recorded on {range.FebrileDays} distinct " +
                    $"{(range.FebrileDays == 1 ? "day" : "days")}.");
            }
            else
            {
                parts.Add(
                    $"None of the {temp.ReadingCount} logged " +
                    $"{(temp.ReadingCount == 1 ? "reading met" : "readings met")} the configured " +
                    $"{range.FeverThresholdF:F1}°F fever-range reference.");
            }
        }

        // -----------------------------------------------------
        // 4. Recorded fever episodes + latest episode context
        // -----------------------------------------------------
        if (temp.Episodes.Count > 0)
        {
            parts.Add(
                $"The fever-range measurements group into {temp.Episodes.Count} recorded " +
                $"{(temp.Episodes.Count == 1 ? "episode" : "episodes")} in this selected window.");
        }

        if (temp.LatestEpisode is not null)
        {
            var episode = temp.LatestEpisode;

            parts.Add(
                $"The latest recorded fever episode includes {episode.FeverReadingCount} " +
                $"fever-range {(episode.FeverReadingCount == 1 ? "reading" : "readings")}, " +
                $"with a peak of {episode.PeakF:F1}°F " +
                $"{(episode.PeakSite == "unknown" ? string.Empty : $"measured {episode.PeakSiteDisplay.ToLowerInvariant()} ")}" +
                $"and an observed fever-range span of {episode.ObservedSpanHours:F1} hours.");

            if (episode.DeltaLatestFromPeakF is not null)
            {
                var deltaFromPeak = episode.DeltaLatestFromPeakF.Value;

                if (deltaFromPeak < 0)
                {
                    parts.Add(
                        $"The latest same-site reading is {Math.Abs(deltaFromPeak):F1}°F below " +
                        "the highest recorded reading in that episode.");
                }
                else if (deltaFromPeak > 0)
                {
                    parts.Add(
                        $"The latest same-site reading is {deltaFromPeak:F1}°F above the " +
                        "previously recorded episode peak.");
                }
                else
                {
                    parts.Add(
                        "The latest same-site reading matches the recorded episode peak.");
                }
            }
        }

        // -----------------------------------------------------
        // 5. Acute short-window trajectory
        // -----------------------------------------------------
        if (temp.AcuteTrend is not null)
        {
            var trend = temp.AcuteTrend;

            parts.Add(trend.TrendLabel switch
            {
                "rising" =>
                    $"Recent same-site temperatures in the latest episode have generally been " +
                    $"rising across {trend.SpanHours:F1} hours.",

                "falling" =>
                    $"Recent same-site temperatures in the latest episode have generally been " +
                    $"falling across {trend.SpanHours:F1} hours.",

                _ =>
                    $"Recent same-site temperatures in the latest episode have been generally " +
                    $"stable across {trend.SpanHours:F1} hours."
            });
        }

        // -----------------------------------------------------
        // 6. Hypothermia-range observations
        // -----------------------------------------------------
        if (range is not null && range.HypothermiaRangeCount > 0)
        {
            parts.Add(
                $"{range.HypothermiaRangeCount} " +
                $"{(range.HypothermiaRangeCount == 1 ? "hypothermia-range reading was" : "hypothermia-range readings were")} " +
                $"recorded below {range.HypothermiaThresholdF:F1}°F. The lowest recorded " +
                $"temperature was {range.LowestF:F1}°F. A current or confirmed temperature " +
                "below 95°F warrants urgent medical evaluation.");
        }

        // -----------------------------------------------------
        // 7. Cross-vital context. Report pairing only; do not
        //    infer that another vital caused the temperature.
        // -----------------------------------------------------
        if (temp.CrossVitalContext?.PairedCounts is not null &&
            temp.CrossVitalContext.PairedCounts.HasAny)
        {
            var paired = temp.CrossVitalContext.PairedCounts;
            var pairedParts = new List<string>();

            if (paired.HeartRate > 0)
                pairedParts.Add($"heart rate with {paired.HeartRate}");
            if (paired.OxygenSaturation > 0)
                pairedParts.Add($"oxygen saturation with {paired.OxygenSaturation}");
            if (paired.BloodPressure > 0)
                pairedParts.Add($"blood pressure with {paired.BloodPressure}");

            parts.Add(
                $"Other vitals were recorded alongside some fever-range observations: " +
                $"{string.Join(", ", pairedParts)}. These pairings provide context but do " +
                "not establish cause.");
        }

        // -----------------------------------------------------
        // 8. Site consistency / data-confidence limitation
        // -----------------------------------------------------
        if (temp.DataSupport?.MixedMeasurementSites == true)
        {
            parts.Add(
                "Multiple temperature measurement sites were used. Site differences limit " +
                "direct comparison, so same-method readings are preferred.");
        }
        else if (temp.DataSupport is not null &&
                 temp.DataSupport.KnownSitePct > 0 &&
                 temp.DataSupport.KnownSitePct < 100)
        {
            parts.Add(
                "Some readings do not have a recorded measurement site, which limits " +
                "site-sensitive comparisons.");
        }

        TempSummary = string.Join(
            " ",
            parts.Where(p => !string.IsNullOrWhiteSpace(p)));

        BuildTempPcpLine(a);
    }

    private void BuildTempPcpLine(VitalsAnalysis a)
    {
        if (a.Temperature is null || string.IsNullOrWhiteSpace(a.PcpName))
        {
            TempPcpLine = string.Empty;
            return;
        }

        var temp = a.Temperature;
        var range = temp.RangeEvents;

        bool hasFeverRangeReadings = (range?.FeverCount ?? 0) > 0;
        bool hasHypothermiaRangeReadings = (range?.HypothermiaRangeCount ?? 0) > 0;
        bool hasRecordedEpisode = temp.Episodes.Count > 0;
        bool hasAcuteTrajectory = temp.AcuteTrend is not null;

        var somethingToDiscuss =
            hasFeverRangeReadings ||
            hasHypothermiaRangeReadings ||
            hasRecordedEpisode ||
            hasAcuteTrajectory;

        if (!somethingToDiscuss)
        {
            TempPcpLine = !string.IsNullOrWhiteSpace(a.NextFollowup)
                ? $"Consider sharing this temperature summary with {a.PcpName} at the appointment on {a.NextFollowup}."
                : $"Consider sharing this temperature summary with {a.PcpName} at the next visit.";

            return;
        }

        TempPcpLine = !string.IsNullOrWhiteSpace(a.NextFollowup)
            ? $"Consider discussing the recorded temperature findings highlighted above with {a.PcpName} at the appointment on {a.NextFollowup}."
            : $"Consider discussing the recorded temperature findings highlighted above with {a.PcpName} at the next visit.";
    }

    private void BuildWeightSummary(VitalsAnalysis a)
    {
        if (a.Weight?.Latest is not { } latest)
        {
            WeightSummary = string.Empty;
            WeightPcpLine = string.Empty;
            return;
        }

        var weight = a.Weight;
        var parts = new List<string>
        {
            $"The latest recorded weight was {latest.Value:F1} lb."
        };

        if (weight.HasBmi)
        {
            parts.Add(
                $"Using the current profile height, adult BMI is " +
                $"{weight.Anthropometrics!.Bmi!.Value:F1} kg/m² " +
                $"({weight.BmiCategoryDisplay.ToLowerInvariant()} BMI screening category). " +
                "BMI is a screening measure and should be interpreted with other health information.");
        }
        else if (weight.HasBmiUnavailable)
        {
            parts.Add(weight.BmiUnavailableDisplay);
        }

        var summary = weight.DailySummary ?? weight.Summary;
        if (summary is not null && weight.DataSupport is not null)
        {
            parts.Add(
                $"Across {weight.ReadingCount} logged readings on " +
                $"{weight.DataSupport.DistinctDays} distinct days, the daily-median " +
                $"weight was {summary.Median:F1} lb with a range of " +
                $"{summary.Min:F1}–{summary.Max:F1} lb.");
        }

        if (weight.BaselineChange is not null)
        {
            var change = weight.BaselineChange;
            if (Math.Abs(change.AbsoluteChange) < 0.05)
            {
                parts.Add(
                    "The latest daily weight is essentially unchanged from the " +
                    "multi-day baseline in this selected period.");
            }
            else
            {
                var direction = change.AbsoluteChange > 0 ? "higher" : "lower";
                var pct = change.PctChange is null
                    ? string.Empty
                    : $" ({Math.Abs(change.PctChange.Value):F1}%)";

                parts.Add(
                    $"The latest daily weight is {Math.Abs(change.AbsoluteChange):F1} lb " +
                    $"{direction}{pct} than the baseline median built from the first " +
                    $"{change.BaselineDaysUsed} measurement day" +
                    $"{(change.BaselineDaysUsed == 1 ? "" : "s")} in this period.");
            }
        }

        if (weight.RecentChange is not null)
        {
            var recent = weight.RecentChange;
            var direction = recent.AbsoluteChange > 0 ? "higher" :
                            recent.AbsoluteChange < 0 ? "lower" : "the same as";

            parts.Add(
                $"The most recent 7-day median is {Math.Abs(recent.AbsoluteChange):F1} lb " +
                $"{direction} the median from the preceding 7 days.");
        }

        if (weight.Trend is not null)
        {
            var directionText = weight.Trend.Direction switch
            {
                "increasing" => "shows an increasing pattern",
                "decreasing" => "shows a decreasing pattern",
                _ => "does not show a clear directional trend"
            };

            parts.Add(
                $"Using one median weight per day and a robust trend estimator, the data " +
                $"{directionText} at {weight.Trend.SlopePerWeek:+0.00;-0.00;0.00} lb per week " +
                $"across {weight.Trend.SpanDays:F0} days. The 95% slope interval is " +
                $"{weight.Trend.Ci95LowPerWeek:+0.00;-0.00;0.00} to " +
                $"{weight.Trend.Ci95HighPerWeek:+0.00;-0.00;0.00} lb per week.");
        }

        if (weight.Variation is not null)
        {
            parts.Add(
                $"Day-to-day variability is summarized with an IQR of " +
                $"{weight.Variation.Iqr:F1} lb and a median absolute deviation of " +
                $"{weight.Variation.Mad:F1} lb.");
        }

        parts.Add(
            "Vitals treats weight change as a descriptive pattern and does not decide whether " +
            "gain or loss is desirable without goals and clinical context. BMI is screening " +
            "context only and is not a diagnosis or body-composition measurement.");

        WeightSummary = string.Join(
            " ",
            parts.Where(p => !string.IsNullOrWhiteSpace(p)));

        WeightPcpLine = BuildDescriptivePcpLine(
            a,
            "weight",
            weight.ReadingCount > 1);
    }

    private void BuildGlucoseSummary(VitalsAnalysis a)
    {
        if (a.Glucose?.Latest is not { } latest)
        {
            GlucoseSummary = string.Empty;
            GlucosePcpLine = string.Empty;
            return;
        }

        var glucose = a.Glucose;
        var parts = new List<string>
        {
            $"The latest recorded blood glucose was {latest.Value:F0} mg/dL."
        };

        if (glucose.Summary is not null)
        {
            parts.Add(
                $"Across {glucose.ReadingCount} logged readings, the median was " +
                $"{glucose.Summary.Median:F0} mg/dL with a recorded range of " +
                $"{glucose.Summary.Min:F0}–{glucose.Summary.Max:F0} mg/dL.");
        }

        parts.Add(
            "Vitals does not classify these readings against a single glucose target " +
            "because fasting, pre-meal, post-meal, and random measurement context is " +
            "not collected yet, and individual targets may differ.");

        GlucoseSummary = string.Join(
            " ",
            parts.Where(p => !string.IsNullOrWhiteSpace(p)));

        GlucosePcpLine = BuildDescriptivePcpLine(
            a,
            "blood-glucose",
            glucose.ReadingCount > 0);
    }

    private static string BuildDescriptivePcpLine(
        VitalsAnalysis a,
        string metricName,
        bool hasData)
    {
        if (!hasData || string.IsNullOrWhiteSpace(a.PcpName))
            return string.Empty;

        return !string.IsNullOrWhiteSpace(a.NextFollowup)
            ? $"Consider sharing this {metricName} summary with {a.PcpName} at the appointment on {a.NextFollowup}."
            : $"Consider sharing this {metricName} summary with {a.PcpName} at the next visit.";
    }

    private void BuildBurdenSummary(VitalsAnalysis a)
    {
        if (a.Map is not null)
        {
            if (!string.IsNullOrWhiteSpace(PlainEnglishSummary))
                PlainEnglishSummary += $" {a.Map.PlainEnglish}";
            else
                PlainEnglishSummary = a.Map.PlainEnglish;
        }
    }
}