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

    public string SummaryDisplay
    {
        get
        {
            var summary = DailySummary ?? Summary;
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
