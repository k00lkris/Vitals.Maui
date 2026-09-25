using System.Text.Json.Serialization;

namespace Vitals.Maui.Models;

// =====================================================
// SPO2 ANALYSIS — matches run_spo2_analysis's JSON output
// exactly (main.py). Same overall shape/conventions as
// HeartRateAnalysis.cs: Has* bool helpers for every
// nullable/optional field an IsVisible binding needs, and
// every combined-string display lives on the model as a
// single computed property, never as FormattedString/Span
// or MultiBinding inside a CollectionView item template.
// =====================================================
public class SpO2Analysis
{
    [JsonPropertyName("spo2")]
    public int? Spo2 { get; set; }

    [JsonPropertyName("recorded_at")]
    public DateTime? RecordedAt { get; set; }

    // Both hardcoded "unknown" server-side today — there's no
    // spo2_context table yet, so these are placeholders, not real data.
    [JsonPropertyName("measurement_context")]
    public string MeasurementContext { get; set; } = "unknown";

    [JsonPropertyName("source_type")]
    public string SourceType { get; set; } = "unknown";

    [JsonPropertyName("linked_context")]
    public Spo2LinkedContext? LinkedContext { get; set; }

    [JsonPropertyName("reading_count")]
    public int ReadingCount { get; set; }

    [JsonPropertyName("target_profile")]
    public Spo2TargetProfile? TargetProfile { get; set; }

    [JsonPropertyName("spot_summary")]
    public Spo2SpotSummary? SpotSummary { get; set; }

    [JsonPropertyName("dispersion")]
    public Spo2Dispersion? Dispersion { get; set; }

    [JsonPropertyName("trend")]
    public Spo2Trend? Trend { get; set; }

    [JsonPropertyName("loess")]
    public List<Spo2LoessPoint>? Loess { get; set; }

    [JsonPropertyName("baseline_deviation")]
    public Spo2BaselineDeviation? BaselineDeviation { get; set; }

    [JsonPropertyName("low_observations")]
    public Spo2ThresholdObservations? LowObservations { get; set; }

    [JsonPropertyName("reference_bands")]
    public Spo2ReferenceBands? ReferenceBands { get; set; }

    [JsonPropertyName("confirmed_low_observations")]
    public Spo2ConfirmedLowObservations? ConfirmedLowObservations { get; set; }

    [JsonPropertyName("marked_low_observations")]
    public Spo2ThresholdObservations? MarkedLowObservations { get; set; }

    [JsonPropertyName("time_of_day")]
    public Spo2TimeOfDay? TimeOfDay { get; set; }

    [JsonPropertyName("cross_vital_context")]
    public Spo2CrossVitalContext? CrossVitalContext { get; set; }

    [JsonPropertyName("cross_vital_correlations")]
    public Dictionary<string, double>? CrossVitalCorrelations { get; set; }

    // Always null today — schema doesn't collect either yet. Kept as
    // real fields (not omitted) so the UI/PDF just work once the
    // backend starts populating them, no model change needed then.
    [JsonPropertyName("symptom_association")]
    public object? SymptomAssociation { get; set; }

    [JsonPropertyName("oxygen_context")]
    public object? OxygenContext { get; set; }

    [JsonPropertyName("data_support")]
    public Spo2DataSupport? DataSupport { get; set; }

    // ---- Display helpers ----

    public string MeasurementContextDisplay => MeasurementContext switch
    {
        "resting" => "Resting",
        "during_activity" => "During activity",
        "recovery" => "Recovery",
        "asleep" => "Asleep",
        _ => "Unknown"
    };

    // ---- Visibility-gate helpers ----
    public bool HasSpotSummary => SpotSummary is not null;
    public bool HasDispersion => Dispersion is not null;
    public bool HasTrend => Trend is not null;
    public bool HasLoess => Loess is not null && Loess.Count > 0;
    public bool HasBaselineDeviation => BaselineDeviation is not null;
    public bool HasConfirmedEpisodes => ConfirmedLowObservations is not null && ConfirmedLowObservations.EpisodeCount > 0;
    public bool HasMarkedLow => MarkedLowObservations is not null && MarkedLowObservations.Count > 0;
    public bool HasTimeOfDayPattern => TimeOfDay?.PatternSummary is not null;
    public bool HasCrossVitalLowObservations => CrossVitalContext is not null && CrossVitalContext.LowObservations.Count > 0;
    public bool HasCrossVitalCorrelations => CrossVitalCorrelations is not null && CrossVitalCorrelations.Count > 0;
    public bool HasUnavailableAnalyses => DataSupport is not null && DataSupport.UnavailableAnalyses.Count > 0;
    public bool HasReferenceBands => ReferenceBands is not null;
}

public class Spo2LinkedContext
{
    [JsonPropertyName("heart_rate")]
    public int? HeartRate { get; set; }

    [JsonPropertyName("blood_pressure")]
    public LinkedBp? BloodPressure { get; set; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("weight")]
    public double? Weight { get; set; }

    [JsonPropertyName("blood_glucose")]
    public int? BloodGlucose { get; set; }

    public bool HasHeartRate => HeartRate is not null;
    public bool HasBloodPressure => BloodPressure is not null;
    public bool HasTemperature => Temperature is not null;
    public bool HasWeight => Weight is not null;
    public bool HasBloodGlucose => BloodGlucose is not null;
    public bool HasAny => HasHeartRate || HasBloodPressure || HasTemperature || HasWeight || HasBloodGlucose;
}

public class Spo2TargetProfile
{
    [JsonPropertyName("low_threshold")]
    public int LowThreshold { get; set; }

    [JsonPropertyName("moderate_threshold")]
    public int ModerateThreshold { get; set; }

    [JsonPropertyName("marked_low_threshold")]
    public int MarkedLowThreshold { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("patient_specific")]
    public bool PatientSpecific { get; set; }

    public string SourceDisplay => PatientSpecific ? "Patient-specific target" : "Reference default (not individualized)";
}

// §6.2 — spot_summary. Named to reflect that, without a spo2_context
// table, these are ALL logged readings, not filtered to resting ones —
// see the note on run_spo2_analysis itself for why this can't be
// called "resting_summary" yet.
public class Spo2SpotSummary
{
    [JsonPropertyName("n")]
    public int N { get; set; }

    [JsonPropertyName("distinct_days")]
    public int DistinctDays { get; set; }

    [JsonPropertyName("mean")]
    public double Mean { get; set; }

    [JsonPropertyName("median")]
    public double Median { get; set; }

    [JsonPropertyName("min")]
    public int Min { get; set; }

    [JsonPropertyName("max")]
    public int Max { get; set; }

    [JsonPropertyName("range")]
    public int Range { get; set; }
}

// §6.3 — dispersion
public class Spo2Dispersion
{
    [JsonPropertyName("n")]
    public int N { get; set; }

    [JsonPropertyName("sd")]
    public double Sd { get; set; }

    [JsonPropertyName("iqr")]
    public double Iqr { get; set; }

    [JsonPropertyName("q1")]
    public double Q1 { get; set; }

    [JsonPropertyName("q3")]
    public double Q3 { get; set; }
}

// §6.4/§6.5 — trend
public class Spo2Trend
{
    [JsonPropertyName("slope_pct_points_per_day")]
    public double SlopePctPointsPerDay { get; set; }

    [JsonPropertyName("modeled_change")]
    public double ModeledChange { get; set; }

    [JsonPropertyName("span_days")]
    public double SpanDays { get; set; }

    [JsonPropertyName("r2")]
    public double? R2 { get; set; }

    [JsonPropertyName("p_value")]
    public double? PValue { get; set; }

    [JsonPropertyName("trend_label")]
    public string TrendLabel { get; set; } = "stable";

    [JsonPropertyName("consistency")]
    public string? Consistency { get; set; }

    public string TrendArrow => TrendLabel switch
    {
        "rising_significant" => "\u2191",
        "rising" => "\u2197",
        "falling_significant" => "\u2193",
        "falling" => "\u2198",
        _ => "\u2192"
    };

    public string TrendColor => TrendLabel switch
    {
        "rising_significant" => "#388e3c",
        "rising" => "#66bb6a",
        "falling_significant" => "#d32f2f",
        "falling" => "#f57c00",
        _ => "#90caf9"
    };

    public string TrendLabelDisplay => TrendLabel switch
    {
        "rising_significant" => "Rising (significant)",
        "rising" => "Rising",
        "falling_significant" => "Falling (significant)",
        "falling" => "Falling",
        _ => "Stable"
    };

    public string SlopeDisplay =>
        SlopePctPointsPerDay >= 0 ? $"+{SlopePctPointsPerDay:F2} pts/day" : $"{SlopePctPointsPerDay:F2} pts/day";

    public string PValueDisplay =>
        PValue is null ? "Not enough data yet for significance" : $"p = {PValue:F3}";

    public string ConsistencyDisplay => Consistency switch
    {
        "high" => $"High (R\u00b2={R2:F2})",
        "moderate" => $"Moderate (R\u00b2={R2:F2})",
        "low" => $"Low (R\u00b2={R2:F2})",
        _ => R2 is not null ? $"R\u00b2={R2:F2}" : "\u2014"
    };
}

public class Spo2LoessPoint
{
    [JsonPropertyName("recorded_at")]
    public DateTime RecordedAt { get; set; }

    [JsonPropertyName("smoothed_spo2")]
    public double SmoothedSpo2 { get; set; }
}

// §6.6 — baseline deviation
public class Spo2BaselineDeviation
{
    [JsonPropertyName("baseline_median")]
    public double BaselineMedian { get; set; }

    [JsonPropertyName("recent_median")]
    public double RecentMedian { get; set; }

    [JsonPropertyName("baseline_n")]
    public int BaselineN { get; set; }

    [JsonPropertyName("recent_n")]
    public int RecentN { get; set; }

    [JsonPropertyName("delta_pct_points")]
    public double DeltaPctPoints { get; set; }

    public string DeltaDisplay =>
        DeltaPctPoints >= 0 ? $"+{DeltaPctPoints:F1} pts" : $"{DeltaPctPoints:F1} pts";
}

// Shared shape for both §6.7 (low_observations) and the marked-low
// block — identical fields, one reusable class instead of two.
public class Spo2ThresholdObservations
{
    [JsonPropertyName("threshold")]
    public int Threshold { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("pct_of_logged_readings")]
    public double? PctOfLoggedReadings { get; set; }

    [JsonPropertyName("readings")]
    public List<Spo2Reading> Readings { get; set; } = new();

    public bool HasPct => PctOfLoggedReadings is not null;
    public bool HasReadings => Readings.Count > 0;
}

public class Spo2Reading
{
    [JsonPropertyName("recorded_at")]
    public DateTime RecordedAt { get; set; }

    [JsonPropertyName("spo2")]
    public int Spo2 { get; set; }
}

// Reference bands — four fixed, known keys (not arbitrary), so a
// named-property class reads more naturally than a dictionary, same
// reasoning as HrTimeOfDayBuckets.
public class Spo2ReferenceBands
{
    [JsonPropertyName("at_or_above_95")]
    public Spo2Band? AtOrAbove95 { get; set; }

    [JsonPropertyName("92_to_94")]
    public Spo2Band? Band92To94 { get; set; }

    [JsonPropertyName("88_to_91")]
    public Spo2Band? Band88To91 { get; set; }

    [JsonPropertyName("below_88")]
    public Spo2Band? Below88 { get; set; }
}

public class Spo2Band
{
    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("pct_of_logged_readings")]
    public double? PctOfLoggedReadings { get; set; }

    public bool HasPct => PctOfLoggedReadings is not null;
}

// §6.8 — confirmed low-observation events
public class Spo2ConfirmedLowObservations
{
    [JsonPropertyName("confirmation_rule_minutes")]
    public int ConfirmationRuleMinutes { get; set; }

    [JsonPropertyName("episode_count")]
    public int EpisodeCount { get; set; }

    [JsonPropertyName("episodes")]
    public List<Spo2Episode> Episodes { get; set; } = new();
}

public class Spo2Episode
{
    [JsonPropertyName("first_recorded_at")]
    public DateTime FirstRecordedAt { get; set; }

    [JsonPropertyName("last_recorded_at")]
    public DateTime LastRecordedAt { get; set; }

    [JsonPropertyName("reading_count")]
    public int ReadingCount { get; set; }

    [JsonPropertyName("nadir")]
    public int Nadir { get; set; }

    [JsonPropertyName("readings")]
    public List<Spo2Reading> Readings { get; set; } = new();

    // Combines fields into one string here, in C# — same reasoning as
    // HrUnavailableAnalysis.DisplayText — for a single plain Label.Text
    // binding inside this episode's own CollectionView item template.
    public string SummaryDisplay =>
        $"Nadir {Nadir}% \u2014 {ReadingCount} reading(s), {FirstRecordedAt:MMM d h:mm tt}\u2013{LastRecordedAt:h:mm tt}";
}

// §6.9 — time-of-day pattern. Same fixed-bucket shape as Heart Rate's
// own HrTimeOfDayBuckets.
public class Spo2TimeOfDay
{
    [JsonPropertyName("buckets")]
    public Spo2TimeOfDayBuckets? Buckets { get; set; }

    [JsonPropertyName("pattern_summary")]
    public Spo2PatternSummary? PatternSummary { get; set; }
}

public class Spo2TimeOfDayBuckets
{
    [JsonPropertyName("morning")]
    public Spo2BucketStats? Morning { get; set; }

    [JsonPropertyName("afternoon")]
    public Spo2BucketStats? Afternoon { get; set; }

    [JsonPropertyName("evening")]
    public Spo2BucketStats? Evening { get; set; }

    [JsonPropertyName("overnight")]
    public Spo2BucketStats? Overnight { get; set; }

    public bool HasMorning => Morning is not null;
    public bool HasAfternoon => Afternoon is not null;
    public bool HasEvening => Evening is not null;
    public bool HasOvernight => Overnight is not null;
    public bool HasAny => HasMorning || HasAfternoon || HasEvening || HasOvernight;
}

public class Spo2BucketStats
{
    [JsonPropertyName("n")]
    public int N { get; set; }

    [JsonPropertyName("distinct_days")]
    public int DistinctDays { get; set; }

    [JsonPropertyName("mean")]
    public double Mean { get; set; }

    [JsonPropertyName("median")]
    public double Median { get; set; }

    [JsonPropertyName("min")]
    public int Min { get; set; }

    [JsonPropertyName("max")]
    public int Max { get; set; }
}

public class Spo2PatternSummary
{
    [JsonPropertyName("highest_period")]
    public string HighestPeriod { get; set; } = string.Empty;

    [JsonPropertyName("lowest_period")]
    public string LowestPeriod { get; set; } = string.Empty;

    [JsonPropertyName("median_delta")]
    public double MedianDelta { get; set; }
}

// §6.12 — cross-vital same-event context (low observations only, per
// the current implementation)
public class Spo2CrossVitalContext
{
    [JsonPropertyName("low_observations")]
    public List<Spo2CrossVitalReading> LowObservations { get; set; } = new();
}

public class Spo2CrossVitalReading
{
    [JsonPropertyName("recorded_at")]
    public DateTime RecordedAt { get; set; }

    [JsonPropertyName("spo2")]
    public int Spo2 { get; set; }

    [JsonPropertyName("context")]
    public Dictionary<string, object>? Context { get; set; }
}

// §6.13 — data support and density
public class Spo2DataSupport
{
    [JsonPropertyName("support_state")]
    public string SupportState { get; set; } = "none";

    [JsonPropertyName("n")]
    public int N { get; set; }

    [JsonPropertyName("distinct_days")]
    public int DistinctDays { get; set; }

    [JsonPropertyName("span_days")]
    public double? SpanDays { get; set; }

    [JsonPropertyName("distinct_day_coverage_pct")]
    public double? DistinctDayCoveragePct { get; set; }

    [JsonPropertyName("unavailable_analyses")]
    public List<Spo2UnavailableAnalysis> UnavailableAnalyses { get; set; } = new();

    public string SupportStateDisplay => SupportState switch
    {
        "none" => "No data yet",
        "snapshot" => "A single reading only",
        "descriptive" => "Basic averages available",
        "trend" => "Trend analysis available",
        "loess" => "Smoothed trend available",
        "significance" => "Full statistical analysis available",
        _ => SupportState
    };

    public bool HasCoverage => DistinctDayCoveragePct is not null;
}

public class Spo2UnavailableAnalysis
{
    [JsonPropertyName("analysis")]
    public string AnalysisName { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonIgnore]
    public string AnalysisNameDisplay => AnalysisName switch
    {
        "spot_summary" => "Central tendency",
        "baseline_deviation" => "Baseline comparison",
        "time_of_day" => "Time-of-day analysis",
        "symptom_association" => "Symptom association",
        "oxygen_context" => "Supplemental oxygen context",
        _ => Humanize(AnalysisName)
    };

    [JsonIgnore]
    public string DisplayText =>
        string.IsNullOrWhiteSpace(Reason)
            ? AnalysisNameDisplay
            : $"{AnalysisNameDisplay}: {Reason}";

    private static string Humanize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var text = value.Replace("_", " ");
        return char.ToUpperInvariant(text[0]) + text[1..];
    }
}
