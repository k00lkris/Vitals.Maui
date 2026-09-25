using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vitals.Maui.Models;
using Vitals.Maui.Services;

namespace Vitals.Maui.ViewModels;

public partial class VitalsAnalysisViewModel : ObservableObject
{
    private readonly ApiService _api;
    private readonly PatientStateService _patientState;

    public Patient? SelectedPatient => _patientState.SelectedPatient;

    [ObservableProperty] private VitalsAnalysis? _analysis;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isInsufficient;
    [ObservableProperty] private bool _isOk;

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

    // Tab buttons — text colors
    private Color _tabBpTextColor = Colors.White;
    public Color TabBpTextColor { get => _tabBpTextColor; set { _tabBpTextColor = value; OnPropertyChanged(); } }

    private Color _tabHrTextColor = Colors.White;
    public Color TabHrTextColor { get => _tabHrTextColor; set { _tabHrTextColor = value; OnPropertyChanged(); } }

    private Color _tabSpo2TextColor = Colors.White;
    public Color TabSpo2TextColor { get => _tabSpo2TextColor; set { _tabSpo2TextColor = value; OnPropertyChanged(); } }

    private Color _tabTempTextColor = Colors.White;
    public Color TabTempTextColor { get => _tabTempTextColor; set { _tabTempTextColor = value; OnPropertyChanged(); } }

    [ObservableProperty] private bool _showBp = true;
    [ObservableProperty] private bool _showHr = false;
    [ObservableProperty] private bool _showSpo2 = false;
    [ObservableProperty] private bool _showTemp = false;

    // Secondary plain English
    [ObservableProperty] private string _hrSummary = string.Empty;
    [ObservableProperty] private string _hrPcpLine = string.Empty;
    [ObservableProperty] private string _spo2Summary = string.Empty;
    [ObservableProperty] private string _tempSummary = string.Empty;

    [ObservableProperty] private bool _showDiastolicWarning = false;
    [ObservableProperty] private string _diastolicWarningText = string.Empty;

    public VitalsAnalysisViewModel(ApiService api, PatientStateService patientState)
    {
        _api = api;
        _patientState = patientState;
    }

    public async Task LoadAsync(int days = 30)
    {
        SelectedDays = days;
        UpdateButtonColors(days);
        SelectTab("bp");
        await RunAnalysisAsync();
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

            if (result is null)
            {
                StatusMessage = "Could not load analysis. Check your connection.";
                IsInsufficient = false;
                IsOk = false;
                return;
            }

            IsInsufficient = result.IsInsufficient;
            IsOk = result.IsOk;

            if (result.IsOk)
            {
                BuildPlainEnglish(result);
                BuildPcpLine(result);
                BuildHrSummary(result);
                BuildSpo2Summary(result);
                BuildTempSummary(result);
            }
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

        ShowBp = tab == "bp";
        ShowHr = tab == "hr";
        ShowSpo2 = tab == "spo2";
        ShowTemp = tab == "temp";

        TabBpColor = tab == "bp" ? active : inactive;
        TabHrColor = tab == "hr" ? active : inactive;
        TabSpo2Color = tab == "spo2" ? active : inactive;
        TabTempColor = tab == "temp" ? active : inactive;

        TabBpTextColor = tab == "bp" ? activeTxt : inactiveTxt;
        TabHrTextColor = tab == "hr" ? activeTxt : inactiveTxt;
        TabSpo2TextColor = tab == "spo2" ? activeTxt : inactiveTxt;
        TabTempTextColor = tab == "temp" ? activeTxt : inactiveTxt;
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
        if (a.Spo2 is null) { Spo2Summary = string.Empty; return; }
        var spo2 = a.Spo2;
        var parts = new List<string>();

        parts.Add(spo2.Classification switch
        {
            "mild_hypoxemia" =>
                $"Average oxygen saturation of {spo2.Avg:F1}% is mildly below normal. " +
                "Values between 92–94% may warrant supplemental oxygen evaluation.",
            "moderate_hypoxemia" =>
                $"Average oxygen saturation of {spo2.Avg:F1}% is moderately low. " +
                "This range is associated with significant breathing difficulty and should be evaluated promptly.",
            "severe_hypoxemia" =>
                $"Average oxygen saturation of {spo2.Avg:F1}% is critically low. " +
                "Readings below 88% require immediate medical attention.",
            _ =>
                $"Average oxygen saturation of {spo2.Avg:F1}% is within the normal range (≥95%)."
        });

        parts.Add(spo2.Trend switch
        {
            "rising_significant" => "Oxygenation has been improving significantly — a positive sign.",
            "rising" => "Oxygenation has been gradually improving.",
            "falling_significant" => "Oxygenation has been declining significantly. This warrants medical attention.",
            "falling" => "Oxygenation has been gradually declining.",
            _ => "Oxygenation has been stable over this period."
        });

        var burden = spo2.Spo2Burden;
        if (burden is not null)
        {
            if (burden.SevereHypoxemiaPct >= 5)
                parts.Add($"About {burden.SevereHypoxemiaPct:F0}% of readings were critically low (below 88%) — this requires clinical review.");
            else if (burden.ModerateHypoxemiaPct >= 10)
                parts.Add($"About {burden.ModerateHypoxemiaPct:F0}% of readings were in the moderate hypoxemia range (88–91%).");
            else if (burden.MildHypoxemiaPct >= 15)
                parts.Add($"About {burden.MildHypoxemiaPct:F0}% of readings were mildly low (92–94%).");
            else if (burden.NormalPct >= 80)
                parts.Add($"{burden.NormalPct:F0}% of readings were in the normal range (≥95%).");
        }

        Spo2Summary = string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private void BuildTempSummary(VitalsAnalysis a)
    {
        if (a.Temperature is null) { TempSummary = string.Empty; return; }
        var temp = a.Temperature;
        var parts = new List<string>();

        parts.Add(temp.Classification switch
        {
            "hypothermia" =>
                $"Average temperature of {temp.Avg:F1}°F is below normal range. " +
                "Temperatures below 96.8°F can indicate hypothermia and should be evaluated.",
            "slightly_elevated" =>
                $"Average temperature of {temp.Avg:F1}°F is slightly elevated. " +
                "This may indicate early illness or mild inflammation.",
            "fever" =>
                $"Average temperature of {temp.Avg:F1}°F indicates a fever. " +
                "Persistent fever above 100.4°F should be evaluated by your care team.",
            "high_fever" =>
                $"Average temperature of {temp.Avg:F1}°F indicates a high fever. " +
                "Temperatures above 103°F require prompt medical attention.",
            _ =>
                $"Average temperature of {temp.Avg:F1}°F is within the normal range (96.8–98.9°F)."
        });

        parts.Add(temp.Trend switch
        {
            "rising_significant" => "Temperature has been rising significantly over this period.",
            "rising" => "Temperature has been gradually rising.",
            "falling_significant" => "Temperature has been falling significantly.",
            "falling" => "Temperature has been gradually falling.",
            _ => "Temperature has been stable over this period."
        });

        var burden = temp.TempBurden;
        if (burden is not null)
        {
            if (burden.HighFeverPct >= 5)
                parts.Add($"About {burden.HighFeverPct:F0}% of readings were above 103°F (high fever range).");
            else if (burden.FeverPct >= 10)
                parts.Add($"About {burden.FeverPct:F0}% of readings were in the fever range (100.4–103°F).");
            else if (burden.ElevatedPct >= 15)
                parts.Add($"About {burden.ElevatedPct:F0}% of readings were slightly elevated (99–100.3°F).");
            else if (burden.HypothermiaPct >= 10)
                parts.Add($"About {burden.HypothermiaPct:F0}% of readings were below 96.8°F (hypothermia range).");
            else if (burden.NormalPct >= 80)
                parts.Add($"{burden.NormalPct:F0}% of readings were in the normal range — temperature is well controlled.");
        }

        TempSummary = string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
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