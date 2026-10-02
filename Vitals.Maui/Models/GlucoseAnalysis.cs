using System.Text.Json.Serialization;

namespace Vitals.Maui.Models;

public class GlucoseAnalysis
{
    [JsonPropertyName("analysis_version")]
    public int AnalysisVersion { get; set; }

    [JsonPropertyName("vital_type")]
    public string VitalType { get; set; } = "glucose";

    [JsonPropertyName("unit")]
    public string Unit { get; set; } = "mg/dL";

    [JsonPropertyName("latest")]
    public GlucoseLatest? Latest { get; set; }

    [JsonPropertyName("reading_count")]
    public int ReadingCount { get; set; }

    [JsonPropertyName("summary")]
    public DescriptiveVitalSummary? Summary { get; set; }

    [JsonPropertyName("context_summary")]
    public GlucoseContextCoverage? ContextSummary { get; set; }

    [JsonPropertyName("context_summaries")]
    public Dictionary<string, GlucoseContextStat> ContextSummaries { get; set; } = new();

    [JsonPropertyName("context_trends")]
    public Dictionary<string, GlucoseContextTrend> ContextTrends { get; set; } = new();

    [JsonPropertyName("low_events")]
    public GlucoseLowEvents? LowEvents { get; set; }

    [JsonPropertyName("logged_readings_in_target")]
    public GlucoseLoggedReadingsInTarget? LoggedReadingsInTarget { get; set; }

    [JsonPropertyName("meal_excursions")]
    public GlucoseMealExcursions? MealExcursions { get; set; }

    [JsonPropertyName("gmi")]
    public GlucoseGmi? Gmi { get; set; }

    [JsonPropertyName("data_support")]
    public GlucoseDataSupport? DataSupport { get; set; }

    [JsonPropertyName("limitations")]
    public List<string> Limitations { get; set; } = new();

    public bool HasLatest => Latest is not null;
    public bool HasSummary => Summary is not null;
    public bool HasContextSummary => ContextSummary is not null;
    public bool HasContextSummaries => ContextSummaries.Values.Any(x => x.IsAvailable);
    public bool HasContextTrends => ContextTrends.Values.Any(x => x.IsAvailable);
    public bool HasLowEvents => LowEvents is not null;
    public bool HasGmi => Gmi is not null;
    public bool HasUnavailableAnalyses => DataSupport?.UnavailableAnalyses.Count > 0;
    public bool HasLimitations => Limitations.Count > 0;

    public string LatestDisplay =>
        Latest is null ? string.Empty : $"{Latest.Value:F0} {Unit}";

    public string LatestContextDisplay =>
        Latest is null ? string.Empty : Latest.ContextDisplay;

    public string SummaryDisplay =>
        Summary is null
            ? string.Empty
            : $"Mean {Summary.Mean:F0} {Unit} · Median {Summary.Median:F0} {Unit} · Range {Summary.Min:F0}–{Summary.Max:F0} {Unit}";

    public string ContextCompletenessDisplay =>
        ContextSummary is null
            ? string.Empty
            : $"Measurement context recorded for {ContextSummary.CompletenessPct:F0}% of readings ({ContextSummary.RecordedCount} of {ReadingCount}).";

    public string ContextSummariesDisplay
    {
        get
        {
            var lines = ContextSummaries
                .Where(x => x.Value.IsAvailable)
                .OrderBy(x => ContextOrder(x.Key))
                .Select(x =>
                    $"{HumanizeContext(x.Key)}: median {x.Value.Median:F0} {Unit} · mean {x.Value.Mean:F0} {Unit} · n={x.Value.SampleCount}");

            return string.Join(Environment.NewLine, lines);
        }
    }

    public string ContextTrendsDisplay
    {
        get
        {
            var lines = ContextTrends
                .Where(x => x.Value.IsAvailable)
                .OrderBy(x => ContextOrder(x.Key))
                .Select(x =>
                {
                    var trend = x.Value;
                    var direction = trend.Direction switch
                    {
                        "rising" => "rising",
                        "falling" => "falling",
                        _ => "stable"
                    };
                    var significance = trend.SignificanceAvailable && trend.PValue is not null
                        ? $" · p={trend.PValue:F3}"
                        : string.Empty;
                    return $"{HumanizeContext(x.Key)}: {direction} · {trend.SlopeMgDlPerDay:+0.0;-0.0;0.0} {Unit}/day across {trend.SpanDays:F0} days{significance}";
                });

            return string.Join(Environment.NewLine, lines);
        }
    }

    public string LowEventsDisplay
    {
        get
        {
            if (LowEvents is null)
                return string.Empty;

            if (LowEvents.TotalLowCount == 0)
                return "No logged readings below 70 mg/dL in the selected period.";

            return $"{LowEvents.TotalLowCount} low reading(s): {LowEvents.Level1Count} between 54–69 mg/dL and {LowEvents.Level2Count} below 54 mg/dL.";
        }
    }

    public string GmiValueDisplay =>
        Gmi?.IsAvailable == true && Gmi.ValuePct is not null
            ? $"{Gmi.ValuePct:F1}%"
            : "Not enough CGM data yet";

    public string GmiMessageDisplay =>
        Gmi?.DisplayMessage ?? string.Empty;

    public string GmiRequirementDisplay
    {
        get
        {
            var requirement = Gmi?.QualificationRequirement;
            if (requirement is null)
                return string.Empty;

            return $"Requires at least {requirement.MinimumDays} days of CGM data with {requirement.MinimumActiveCoveragePct:F0}% or greater active coverage.";
        }
    }

    public string UnavailableAnalysesDisplay =>
        DataSupport is null || DataSupport.UnavailableAnalyses.Count == 0
            ? string.Empty
            : string.Join(
                Environment.NewLine,
                DataSupport.UnavailableAnalyses
                    .Where(x => x.Analysis != "gmi")
                    .Select(x => $"• {x.DisplayText}"));

    public string LimitationsDisplay =>
        string.Join(Environment.NewLine, Limitations.Select(x => $"• {x}"));

    private static string HumanizeContext(string value) => value switch
    {
        "fasting" => "Fasting",
        "pre_meal" => "Before meal",
        "post_meal" => "After meal",
        "bedtime" => "Bedtime",
        "random" => "Random",
        "other" => "Other",
        _ => "Unknown"
    };

    private static int ContextOrder(string value) => value switch
    {
        "fasting" => 0,
        "pre_meal" => 1,
        "post_meal" => 2,
        "bedtime" => 3,
        "random" => 4,
        "other" => 5,
        _ => 6
    };
}

public class GlucoseLatest
{
    [JsonPropertyName("value")]
    public double Value { get; set; }

    [JsonPropertyName("recorded_at")]
    public DateTime? RecordedAt { get; set; }

    [JsonPropertyName("measurement_context")]
    public string MeasurementContext { get; set; } = "unknown";

    [JsonPropertyName("meal_type")]
    public string? MealType { get; set; }

    [JsonPropertyName("minutes_after_meal")]
    public int? MinutesAfterMeal { get; set; }

    [JsonPropertyName("source_type")]
    public string SourceType { get; set; } = "unknown";

    [JsonPropertyName("source_device")]
    public string? SourceDevice { get; set; }

    public string ContextDisplay
    {
        get
        {
            var parts = new List<string>
            {
                MeasurementContext switch
                {
                    "fasting" => "Fasting",
                    "pre_meal" => "Before meal",
                    "post_meal" => "After meal",
                    "bedtime" => "Bedtime",
                    "random" => "Random",
                    "other" => "Other",
                    _ => "Context not recorded"
                }
            };

            if (!string.IsNullOrWhiteSpace(MealType))
            {
                parts.Add(MealType switch
                {
                    "breakfast" => "Breakfast",
                    "lunch" => "Lunch",
                    "dinner" => "Dinner",
                    "snack" => "Snack",
                    _ => "Other meal"
                });
            }

            if (MinutesAfterMeal is not null)
                parts.Add($"{MinutesAfterMeal} min after meal");

            if (SourceType == "manual_bgm")
                parts.Add("Manual/BGM");
            else if (SourceType == "cgm")
                parts.Add("CGM");

            return string.Join(" · ", parts);
        }
    }
}

public class GlucoseContextCoverage
{
    [JsonPropertyName("recorded_count")]
    public int RecordedCount { get; set; }

    [JsonPropertyName("unknown_count")]
    public int UnknownCount { get; set; }

    [JsonPropertyName("completeness_pct")]
    public double CompletenessPct { get; set; }
}

public class GlucoseContextStat
{
    [JsonPropertyName("is_available")]
    public bool IsAvailable { get; set; }

    [JsonPropertyName("reason_code")]
    public string? ReasonCode { get; set; }

    [JsonPropertyName("sample_count")]
    public int SampleCount { get; set; }

    [JsonPropertyName("distinct_days")]
    public int DistinctDays { get; set; }

    [JsonPropertyName("span_days")]
    public double SpanDays { get; set; }

    [JsonPropertyName("mean")]
    public double? Mean { get; set; }

    [JsonPropertyName("median")]
    public double? Median { get; set; }

    [JsonPropertyName("min")]
    public double? Min { get; set; }

    [JsonPropertyName("max")]
    public double? Max { get; set; }
}

public class GlucoseContextTrend
{
    [JsonPropertyName("is_available")]
    public bool IsAvailable { get; set; }

    [JsonPropertyName("sample_count")]
    public int SampleCount { get; set; }

    [JsonPropertyName("span_days")]
    public double SpanDays { get; set; }

    [JsonPropertyName("direction")]
    public string? Direction { get; set; }

    [JsonPropertyName("slope_mg_dl_per_day")]
    public double? SlopeMgDlPerDay { get; set; }

    [JsonPropertyName("p_value")]
    public double? PValue { get; set; }

    [JsonPropertyName("significance_available")]
    public bool SignificanceAvailable { get; set; }
}

public class GlucoseLowEvents
{
    [JsonPropertyName("level_1_count")]
    public int Level1Count { get; set; }

    [JsonPropertyName("level_2_count")]
    public int Level2Count { get; set; }

    [JsonPropertyName("total_low_count")]
    public int TotalLowCount { get; set; }
}

public class GlucoseLoggedReadingsInTarget
{
    [JsonPropertyName("is_available")]
    public bool IsAvailable { get; set; }

    [JsonPropertyName("reason_code")]
    public string? ReasonCode { get; set; }

    [JsonPropertyName("percentage")]
    public double? Percentage { get; set; }
}

public class GlucoseMealExcursions
{
    [JsonPropertyName("is_available")]
    public bool IsAvailable { get; set; }

    [JsonPropertyName("pair_count")]
    public int PairCount { get; set; }
}

public class GlucoseGmi
{
    [JsonPropertyName("is_available")]
    public bool IsAvailable { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("reason_code")]
    public string ReasonCode { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("display_message")]
    public string DisplayMessage { get; set; } = string.Empty;

    [JsonPropertyName("qualification_requirement")]
    public GlucoseCgmQualificationRequirement? QualificationRequirement { get; set; }

    [JsonPropertyName("value_pct")]
    public double? ValuePct { get; set; }

    [JsonPropertyName("disclosure")]
    public string Disclosure { get; set; } = string.Empty;
}

public class GlucoseCgmQualificationRequirement
{
    [JsonPropertyName("minimum_days")]
    public int MinimumDays { get; set; }

    [JsonPropertyName("minimum_active_coverage_pct")]
    public double MinimumActiveCoveragePct { get; set; }
}

public class GlucoseDataSupport
{
    [JsonPropertyName("n")]
    public int N { get; set; }

    [JsonPropertyName("distinct_days")]
    public int DistinctDays { get; set; }

    [JsonPropertyName("span_days")]
    public double SpanDays { get; set; }

    [JsonPropertyName("support_state")]
    public string SupportState { get; set; } = "snapshot";

    [JsonPropertyName("context_completeness_pct")]
    public double ContextCompletenessPct { get; set; }

    [JsonPropertyName("unavailable_analyses")]
    public List<DescriptiveVitalUnavailableAnalysis> UnavailableAnalyses { get; set; } = new();

    public string SupportStateDisplay => SupportState switch
    {
        "trend" => "Context-specific trend available",
        "descriptive" => "Descriptive glucose summary available",
        _ => "Snapshot only"
    };

    public string CoverageDisplay =>
        $"{N} readings · {DistinctDays} distinct days · {SpanDays:F1} day span";
}
