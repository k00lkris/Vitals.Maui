using System.Text.Json.Serialization;

namespace Vitals.Maui.Models;

/// <summary>
/// Dedicated Weight analysis contract. The backend keeps raw-reading summary
/// values for transparency, but uses local-day medians for baseline change,
/// recent-period comparison, variability, and robust longitudinal trend.
/// </summary>
public class WeightAnalysis
{
    [JsonPropertyName("analysis_version")]
    public int AnalysisVersion { get; set; }

    [JsonPropertyName("vital_type")]
    public string VitalType { get; set; } = "weight";

    [JsonPropertyName("unit")]
    public string Unit { get; set; } = "lb";

    [JsonPropertyName("latest")]
    public DescriptiveVitalLatest? Latest { get; set; }

    [JsonPropertyName("reading_count")]
    public int ReadingCount { get; set; }

    [JsonPropertyName("anthropometrics")]
    public WeightAnthropometrics? Anthropometrics { get; set; }

    [JsonPropertyName("historical_bmi")]
    public WeightHistoricalBmi? HistoricalBmi { get; set; }

    [JsonPropertyName("summary")]
    public DescriptiveVitalSummary? Summary { get; set; }

    [JsonPropertyName("daily_summary")]
    public DescriptiveVitalSummary? DailySummary { get; set; }

    // Compatibility with the first descriptive Weight/PDF pass.
    [JsonPropertyName("change_from_first")]
    public DescriptiveVitalChange? ChangeFromFirst { get; set; }

    [JsonPropertyName("baseline_change")]
    public WeightBaselineChange? BaselineChange { get; set; }

    [JsonPropertyName("recent_change")]
    public WeightRecentChange? RecentChange { get; set; }

    [JsonPropertyName("variation")]
    public WeightVariation? Variation { get; set; }

    [JsonPropertyName("trend")]
    public WeightTrend? Trend { get; set; }

    [JsonPropertyName("data_support")]
    public WeightDataSupport? DataSupport { get; set; }

    [JsonPropertyName("limitations")]
    public List<string> Limitations { get; set; } = new();

    public bool HasLatest => Latest is not null;
    public bool HasBmi => Anthropometrics?.BmiAvailable == true && Anthropometrics.Bmi is not null;
    public bool HasBmiUnavailable => Latest is not null && Anthropometrics is not null && !Anthropometrics.BmiAvailable;
    public bool HasHistoricalBmi =>
        HistoricalBmi?.Available == true && HistoricalBmi.Points.Count > 0;
    public bool HasHistoricalBmiUnavailable =>
        Latest is not null && HistoricalBmi is not null && !HistoricalBmi.Available;
    public bool HasSummary => (DailySummary ?? Summary) is not null;
    public bool HasBaselineChange => BaselineChange is not null;
    public bool HasChange => HasBaselineChange;
    public bool HasRecentChange => RecentChange is not null;
    public bool HasVariation => Variation is not null;
    public bool HasTrend => Trend is not null;
    public bool HasUnavailableAnalyses =>
        DataSupport?.UnavailableAnalyses.Count > 0;
    public bool HasLimitations => Limitations.Count > 0;

    public string LatestDisplay =>
        Latest is null ? string.Empty : $"{Latest.Value:F1} {Unit}";

    public string BmiDisplay =>
        !HasBmi
            ? string.Empty
            : $"{Anthropometrics!.Bmi!.Value:F1} kg/m²";

    public string BmiCategoryDisplay =>
        Anthropometrics?.AdultCategory switch
        {
            "underweight" => "Underweight",
            "healthy_weight" => "Healthy weight",
            "overweight" => "Overweight",
            "obesity_class_1" => "Obesity — Class 1",
            "obesity_class_2" => "Obesity — Class 2",
            "obesity_class_3" => "Obesity — Class 3",
            _ => string.Empty
        };

    public string BmiContextDisplay
    {
        get
        {
            if (!HasBmi || Anthropometrics?.HeightInches is not int totalInches)
                return string.Empty;

            var feet = totalInches / 12;
            var inches = totalInches % 12;
            var measured = string.Empty;
            if (!string.IsNullOrWhiteSpace(Anthropometrics.HeightMeasuredAt) &&
                DateTime.TryParse(Anthropometrics.HeightMeasuredAt, out var measuredAt))
            {
                measured = $" · height effective {measuredAt:MMM d, yyyy}";
            }

            var age = Anthropometrics.AgeYears is int years
                ? $" · age {years} on weight date"
                : string.Empty;

            return $"Height used: {feet}' {inches}\"{measured}{age}";
        }
    }

    public string BmiUnavailableDisplay =>
        Anthropometrics?.ReasonUnavailable switch
        {
            "missing_height" => "Add a current height in Settings → Patient Profile to calculate adult BMI.",
            "missing_date_of_birth" => "A date of birth is required before adult BMI can be age-gated.",
            "pediatric_strategy_required" => "Adult BMI screening categories are not shown for patients under age 20. Pediatric BMI-for-age is a separate analysis.",
            "invalid_height" => "The stored height is not usable for BMI calculation.",
            _ => "Adult BMI is unavailable for the current patient profile."
        };

    public string HistoricalBmiSummaryDisplay
    {
        get
        {
            if (!HasHistoricalBmi || HistoricalBmi is null)
                return string.Empty;

            if (HistoricalBmi.PointCount == 1)
            {
                var bmi = HistoricalBmi.LatestBmi ?? HistoricalBmi.FirstBmi;
                return bmi is double singleBmi
                    ? $"1 historically valid adult BMI point · {singleBmi:F1} kg/m²"
                    : "1 historically valid adult BMI point";
            }

            var text =
                $"{HistoricalBmi.PointCount} historically valid adult BMI points";

            if (HistoricalBmi.FirstBmi is double first &&
                HistoricalBmi.LatestBmi is double latest)
            {
                text += $" · {first:F1} → {latest:F1} kg/m²";
            }

            if (HistoricalBmi.AbsoluteChange is double change)
                text += $" · change {change:+0.0;-0.0;0.0} kg/m²";

            return text;
        }
    }

    public string HistoricalBmiRecentPointsDisplay
    {
        get
        {
            if (!HasHistoricalBmi || HistoricalBmi is null)
                return string.Empty;

            var lines = HistoricalBmi.Points
                .OrderByDescending(p => p.LocalDate)
                .Take(6)
                .Select(p => p.DisplayLine);

            var text = string.Join(Environment.NewLine, lines);
            if (HistoricalBmi.PointCount > 6)
            {
                text +=
                    $"{Environment.NewLine}… {HistoricalBmi.PointCount - 6} earlier " +
                    "valid point(s) not shown here";
            }

            return text;
        }
    }

    public string HistoricalBmiCoverageDisplay
    {
        get
        {
            if (HistoricalBmi is null)
                return string.Empty;

            var parts = new List<string>();
            if (HistoricalBmi.SkippedMissingHistoricalHeight > 0)
            {
                parts.Add(
                    $"{HistoricalBmi.SkippedMissingHistoricalHeight} Weight day(s) " +
                    "omitted because no height was yet effective");
            }
            if (HistoricalBmi.SkippedPediatric > 0)
            {
                parts.Add(
                    $"{HistoricalBmi.SkippedPediatric} Weight day(s) occurred before age 20");
            }

            return string.Join(" · ", parts);
        }
    }

    public string HistoricalBmiUnavailableDisplay =>
        HistoricalBmi?.ReasonUnavailable switch
        {
            "missing_date_of_birth" =>
                "A date of birth is required before historical adult BMI can be age-gated.",
            "pediatric_strategy_required" =>
                "No Weight day in this period occurred at age 20 or older. Pediatric BMI-for-age requires a separate strategy.",
            "no_historically_valid_height" =>
                "No active height observation was effective on or before the eligible Weight date(s). Newer heights are not applied backward in time.",
            _ => HistoricalBmi?.Reason ??
                 "Historical BMI is unavailable for the selected Weight dates."
        };

    private DescriptiveVitalSummary? DisplaySummary =>
        DailySummary ?? Summary;

    public string SummarySourceDisplay =>
        DailySummary is not null ? "Daily median values" : "Logged readings";

    public string SummaryMeanDisplay =>
        DisplaySummary is null ? string.Empty : $"{DisplaySummary.Mean:F1} {Unit}";

    public string SummaryMedianDisplay =>
        DisplaySummary is null ? string.Empty : $"{DisplaySummary.Median:F1} {Unit}";

    public string SummaryMinDisplay =>
        DisplaySummary is null ? string.Empty : $"{DisplaySummary.Min:F1} {Unit}";

    public string SummaryMaxDisplay =>
        DisplaySummary is null ? string.Empty : $"{DisplaySummary.Max:F1} {Unit}";

    // Retained for any existing non-table bindings/reports.
    public string SummaryDisplay
    {
        get
        {
            var summary = DisplaySummary;
            if (summary is null)
                return string.Empty;

            var prefix = DailySummary is not null ? "Daily median summary" : "Logged reading summary";
            return $"{prefix}: mean {summary.Mean:F1} {Unit} · median {summary.Median:F1} {Unit} · range {summary.Min:F1}–{summary.Max:F1} {Unit}";
        }
    }

    // Kept so the existing binding remains valid while the Weight tab moves
    // from "first reading" change to a more robust multi-day baseline.
    public string ChangeDisplay => BaselineChangeDisplay;

    public string BaselineChangeDisplay
    {
        get
        {
            if (BaselineChange is null)
                return string.Empty;

            var pct = BaselineChange.PctChange is null
                ? string.Empty
                : $" ({BaselineChange.PctChange:+0.0;-0.0;0.0}%)";

            return
                $"{BaselineChange.AbsoluteChange:+0.0;-0.0;0.0} {Unit}{pct} versus baseline " +
                $"({BaselineChange.BaselineDaysUsed} day{(BaselineChange.BaselineDaysUsed == 1 ? "" : "s")}, " +
                $"{BaselineChange.BaselineValue:F1} {Unit}).";
        }
    }

    public string RecentChangeDisplay
    {
        get
        {
            if (RecentChange is null)
                return string.Empty;

            var pct = RecentChange.PctChange is null
                ? string.Empty
                : $" ({RecentChange.PctChange:+0.0;-0.0;0.0}%)";

            return
                $"Recent 7-day median: {RecentChange.RecentMedian:F1} {Unit}; " +
                $"previous 7-day median: {RecentChange.PriorMedian:F1} {Unit}. " +
                $"Change {RecentChange.AbsoluteChange:+0.0;-0.0;0.0} {Unit}{pct}.";
        }
    }

    public string VariationDisplay =>
        Variation is null
            ? string.Empty
            : $"Daily-median variability: SD {Variation.Sd:F1} {Unit} · IQR {Variation.Iqr:F1} {Unit} · MAD {Variation.Mad:F1} {Unit}.";

    public string TrendDisplay
    {
        get
        {
            if (Trend is null)
                return string.Empty;

            var direction = Trend.Direction switch
            {
                "increasing" => "Increasing pattern",
                "decreasing" => "Decreasing pattern",
                _ => "No clear directional trend"
            };

            return
                $"{direction}: {Trend.SlopePerWeek:+0.00;-0.00;0.00} {Unit}/week " +
                $"(95% slope interval {Trend.Ci95LowPerWeek:+0.00;-0.00;0.00} to " +
                $"{Trend.Ci95HighPerWeek:+0.00;-0.00;0.00} {Unit}/week) across {Trend.SpanDays:F0} days.";
        }
    }

    public string UnavailableAnalysesDisplay =>
        DataSupport is null || DataSupport.UnavailableAnalyses.Count == 0
            ? string.Empty
            : string.Join(
                Environment.NewLine,
                DataSupport.UnavailableAnalyses.Select(x => $"• {x.DisplayText}"));

    public string LimitationsDisplay =>
        Limitations.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, Limitations.Select(x => $"• {x}"));
}

public class WeightAnthropometrics
{
    [JsonPropertyName("bmi_available")]
    public bool BmiAvailable { get; set; }

    [JsonPropertyName("reason_unavailable")]
    public string? ReasonUnavailable { get; set; }

    [JsonPropertyName("height_inches")]
    public int? HeightInches { get; set; }

    [JsonPropertyName("height_cm")]
    public double? HeightCm { get; set; }

    [JsonPropertyName("height_source")]
    public string? HeightSource { get; set; }

    // Effective date/source for the current profile height used by the
    // current-BMI snapshot. Historical BMI uses per-point height provenance.
    [JsonPropertyName("height_measured_at")]
    public string? HeightMeasuredAt { get; set; }

    [JsonPropertyName("age_years")]
    public int? AgeYears { get; set; }

    [JsonPropertyName("bmi")]
    public double? Bmi { get; set; }

    [JsonPropertyName("adult_category")]
    public string? AdultCategory { get; set; }
}

public class WeightHistoricalBmi
{
    [JsonPropertyName("available")]
    public bool Available { get; set; }

    [JsonPropertyName("reason_unavailable")]
    public string? ReasonUnavailable { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("point_count")]
    public int PointCount { get; set; }

    [JsonPropertyName("candidate_weight_days")]
    public int CandidateWeightDays { get; set; }

    [JsonPropertyName("adult_candidate_days")]
    public int AdultCandidateDays { get; set; }

    [JsonPropertyName("skipped_missing_historical_height")]
    public int SkippedMissingHistoricalHeight { get; set; }

    [JsonPropertyName("skipped_pediatric")]
    public int SkippedPediatric { get; set; }

    [JsonPropertyName("skipped_missing_date_of_birth")]
    public int SkippedMissingDateOfBirth { get; set; }

    [JsonPropertyName("points")]
    public List<WeightHistoricalBmiPoint> Points { get; set; } = new();

    [JsonPropertyName("first_bmi")]
    public double? FirstBmi { get; set; }

    [JsonPropertyName("latest_bmi")]
    public double? LatestBmi { get; set; }

    [JsonPropertyName("absolute_change")]
    public double? AbsoluteChange { get; set; }

    [JsonPropertyName("span_days")]
    public double SpanDays { get; set; }
}

public class WeightHistoricalBmiPoint
{
    [JsonPropertyName("local_date")]
    public string LocalDate { get; set; } = string.Empty;

    [JsonPropertyName("weight_lb")]
    public double WeightLb { get; set; }

    [JsonPropertyName("source_reading_count")]
    public int SourceReadingCount { get; set; }

    [JsonPropertyName("height_inches")]
    public int HeightInches { get; set; }

    [JsonPropertyName("height_effective_date")]
    public string HeightEffectiveDate { get; set; } = string.Empty;

    [JsonPropertyName("height_source")]
    public string? HeightSource { get; set; }

    [JsonPropertyName("height_entry_type")]
    public string? HeightEntryType { get; set; }

    [JsonPropertyName("age_years")]
    public int AgeYears { get; set; }

    [JsonPropertyName("bmi")]
    public double Bmi { get; set; }

    [JsonPropertyName("adult_category")]
    public string? AdultCategory { get; set; }

    public string DisplayLine
    {
        get
        {
            var dateText = DateTime.TryParse(LocalDate, out var date)
                ? date.ToString("MMM d, yyyy")
                : LocalDate;
            var heightDateText = DateTime.TryParse(HeightEffectiveDate, out var heightDate)
                ? heightDate.ToString("MMM d, yyyy")
                : HeightEffectiveDate;
            var feet = HeightInches / 12;
            var inches = HeightInches % 12;

            return
                $"{dateText} · {WeightLb:F1} lb · BMI {Bmi:F1} · " +
                $"height {feet}' {inches}\" effective {heightDateText}";
        }
    }
}

public class WeightBaselineChange
{
    [JsonPropertyName("baseline_value")]
    public double BaselineValue { get; set; }

    [JsonPropertyName("baseline_days_used")]
    public int BaselineDaysUsed { get; set; }

    [JsonPropertyName("baseline_start_date")]
    public string BaselineStartDate { get; set; } = string.Empty;

    [JsonPropertyName("baseline_end_date")]
    public string BaselineEndDate { get; set; } = string.Empty;

    [JsonPropertyName("latest_daily_value")]
    public double LatestDailyValue { get; set; }

    [JsonPropertyName("latest_date")]
    public string LatestDate { get; set; } = string.Empty;

    [JsonPropertyName("absolute_change")]
    public double AbsoluteChange { get; set; }

    [JsonPropertyName("pct_change")]
    public double? PctChange { get; set; }
}

public class WeightRecentChange
{
    [JsonPropertyName("recent_median")]
    public double RecentMedian { get; set; }

    [JsonPropertyName("prior_median")]
    public double PriorMedian { get; set; }

    [JsonPropertyName("recent_days_with_readings")]
    public int RecentDaysWithReadings { get; set; }

    [JsonPropertyName("prior_days_with_readings")]
    public int PriorDaysWithReadings { get; set; }

    [JsonPropertyName("absolute_change")]
    public double AbsoluteChange { get; set; }

    [JsonPropertyName("pct_change")]
    public double? PctChange { get; set; }
}

public class WeightVariation
{
    [JsonPropertyName("sd")]
    public double Sd { get; set; }

    [JsonPropertyName("iqr")]
    public double Iqr { get; set; }

    [JsonPropertyName("q1")]
    public double Q1 { get; set; }

    [JsonPropertyName("q3")]
    public double Q3 { get; set; }

    [JsonPropertyName("mad")]
    public double Mad { get; set; }
}

public class WeightTrend
{
    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("n")]
    public int N { get; set; }

    [JsonPropertyName("span_days")]
    public double SpanDays { get; set; }

    [JsonPropertyName("slope_per_day")]
    public double SlopePerDay { get; set; }

    [JsonPropertyName("slope_per_week")]
    public double SlopePerWeek { get; set; }

    [JsonPropertyName("ci95_low_per_week")]
    public double Ci95LowPerWeek { get; set; }

    [JsonPropertyName("ci95_high_per_week")]
    public double Ci95HighPerWeek { get; set; }

    [JsonPropertyName("direction")]
    public string Direction { get; set; } = "no_clear_trend";

    // Secondary OLS diagnostics. Direction is based on the robust Theil-Sen
    // interval above, not these values.
    [JsonPropertyName("r2")]
    public double R2 { get; set; }

    [JsonPropertyName("p_value")]
    public double PValue { get; set; }

    public string FitDisplay =>
        $"Robust Theil–Sen trend · OLS diagnostic R² {R2:F2}, p={PValue:F3}";
}

public class WeightDataSupport
{
    [JsonPropertyName("n")]
    public int N { get; set; }

    [JsonPropertyName("distinct_days")]
    public int DistinctDays { get; set; }

    [JsonPropertyName("span_days")]
    public double SpanDays { get; set; }

    [JsonPropertyName("daily_span_days")]
    public double DailySpanDays { get; set; }

    [JsonPropertyName("support_state")]
    public string SupportState { get; set; } = "snapshot";

    [JsonPropertyName("unavailable_analyses")]
    public List<DescriptiveVitalUnavailableAnalysis> UnavailableAnalyses { get; set; } = new();

    public string SupportStateDisplay => SupportState switch
    {
        "trend" => "Robust longitudinal trend available",
        "descriptive" => "Descriptive weight summary available",
        _ => "Snapshot only"
    };

    public string CoverageDisplay =>
        $"{N} readings · {DistinctDays} distinct days · {DailySpanDays:F0} day calendar span";
}
