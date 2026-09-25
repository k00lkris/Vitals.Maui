using System.Text.Json.Serialization;
using System.Linq;

namespace Vitals.Maui.Models;

// =====================================================
// HEART RATE ANALYSIS — matches run_hr_analysis's JSON
// output exactly (main.py). Replaces the old flat
// SecondaryAnalysis shape, which was built for
// analyze_vital_series and can't represent §6.1–§6.12.
// =====================================================
public class HeartRateAnalysis
{
    // §6.1 — Latest rate + context. Always present if there's at
    // least one reading; everything below this can be null if its
    // own gate isn't met yet.
    [JsonPropertyName("bpm")]
    public int? Bpm { get; set; }

    [JsonPropertyName("recorded_at")]
    public DateTime? RecordedAt { get; set; }

    [JsonPropertyName("activity_context")]
    public string ActivityContext { get; set; } = "unknown";

    [JsonPropertyName("posture")]
    public string Posture { get; set; } = "unknown";

    [JsonPropertyName("symptom_tags")]
    public List<string> SymptomTags { get; set; } = new();

    [JsonPropertyName("source_type")]
    public string SourceType { get; set; } = "unknown";

    [JsonPropertyName("irregular_pulse_flag")]
    public bool? IrregularPulseFlag { get; set; }

    [JsonPropertyName("linked_context")]
    public LinkedContext? LinkedContext { get; set; }

    [JsonPropertyName("reading_count")]
    public int ReadingCount { get; set; }

    [JsonPropertyName("resting_summary")]
    public HrRestingSummary? RestingSummary { get; set; }

    [JsonPropertyName("dispersion")]
    public HrDispersion? Dispersion { get; set; }

    [JsonPropertyName("trend")]
    public HrTrend? Trend { get; set; }

    [JsonPropertyName("loess")]
    public List<HrLoessPoint>? Loess { get; set; }

    [JsonPropertyName("baseline_deviation")]
    public HrBaselineDeviation? BaselineDeviation { get; set; }

    [JsonPropertyName("rate_events")]
    public HrRateEvents? RateEvents { get; set; }

    [JsonPropertyName("time_of_day")]
    public HrTimeOfDay? TimeOfDay { get; set; }

    [JsonPropertyName("symptom_association")]
    public Dictionary<string, HrSymptomAssociationEntry>? SymptomAssociation { get; set; }

    [JsonPropertyName("cross_vital_context")]
    public HrCrossVitalContext? CrossVitalContext { get; set; }

    [JsonPropertyName("cross_vital_correlations")]
    public Dictionary<string, double>? CrossVitalCorrelations { get; set; }

    [JsonPropertyName("medication_associations")]
    public List<HrMedicationAssociation>? MedicationAssociations { get; set; }

    [JsonPropertyName("dispersion_trend")]
    public HrDispersionTrend? DispersionTrend { get; set; }

    [JsonPropertyName("data_support")]
    public HrDataSupport? DataSupport { get; set; }

    // ---- Display helpers ----

    public string ActivityContextDisplay => ActivityContext switch
    {
        "resting" => "Resting",
        "post_activity" => "Just after activity",
        "during_activity" => "During activity",
        _ => "Unknown"
    };

    public string PostureDisplay => Posture switch
    {
        "seated" => "Seated",
        "supine" => "Lying down",
        "standing" => "Standing",
        _ => "Unknown"
    };

    public string SymptomTagsDisplay =>
        SymptomTags.Count > 0 ? string.Join(", ", SymptomTags) : "None reported";

    public string IrregularPulseDisplay => IrregularPulseFlag switch
    {
        true => "Irregular pulse reported",
        false => "No irregular pulse reported",
        null => "Not reported"
    };

    // Visibility-gate helpers — a bool property per nested section is
    // simpler and safer than introducing a new null-check converter for
    // each one; XAML IsVisible bindings read these directly.
    public bool HasRestingSummary => RestingSummary is not null;
    public bool HasDispersion => Dispersion is not null;
    public bool HasTrend => Trend is not null;
    public bool HasLoess => Loess is not null && Loess.Count > 0;
    public bool HasBaselineDeviation => BaselineDeviation is not null;
    public bool HasRateEvents => RateEvents is not null;
    public bool HasTimeOfDayPattern => TimeOfDay?.PatternSummary is not null;
    public bool HasSymptomAssociation => SymptomAssociation is not null && SymptomAssociation.Count > 0;

    // The XAML CollectionView binds to this, not the raw dictionary —
    // each item carries its own tag name alongside the entry's fields,
    // which the dictionary form doesn't support on its own.
    [JsonIgnore]
    public List<HrSymptomAssociationItem> SymptomAssociationList =>
        SymptomAssociation?.Select(kv => new HrSymptomAssociationItem
        {
            Tag = kv.Key,
            AssociatedCount = kv.Value.AssociatedCount,
            Events = kv.Value.Events,
            PctLow = kv.Value.PctLow,
            PctHigh = kv.Value.PctHigh,
        }).ToList() ?? new();
    public bool HasCrossVitalCorrelations => CrossVitalCorrelations is not null && CrossVitalCorrelations.Count > 0;
    public bool HasMedicationAssociations => MedicationAssociations is not null && MedicationAssociations.Count > 0;
    public bool HasDispersionTrendComparison => DispersionTrend?.Status == "ok";
    public bool HasUnavailableAnalyses => DataSupport is not null && DataSupport.UnavailableAnalyses.Count > 0;
}

public class LinkedContext
{
    [JsonPropertyName("blood_pressure")]
    public LinkedBp? BloodPressure { get; set; }

    [JsonPropertyName("oxygen_saturation")]
    public int? OxygenSaturation { get; set; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("weight")]
    public double? Weight { get; set; }

    [JsonPropertyName("blood_glucose")]
    public int? BloodGlucose { get; set; }

    public bool HasAny =>
        BloodPressure is not null || OxygenSaturation is not null ||
        Temperature is not null || Weight is not null || BloodGlucose is not null;

    // Individual presence checks — needed because each field gets its
    // own display line; HasAny alone only gates the section header.
    public bool HasBloodPressure => BloodPressure is not null;
    public bool HasOxygenSaturation => OxygenSaturation is not null;
    public bool HasTemperature => Temperature is not null;
    public bool HasWeight => Weight is not null;
    public bool HasBloodGlucose => BloodGlucose is not null;
}

public class LinkedBp
{
    [JsonPropertyName("systolic")]
    public int Systolic { get; set; }

    [JsonPropertyName("diastolic")]
    public int Diastolic { get; set; }

    public string Display => $"{Systolic}/{Diastolic} mmHg";
}

// §6.2 — Resting Central Tendency and Range
public class HrRestingSummary
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

// §6.3 — Between-Reading Dispersion. cv_spot is retained for
// completeness but deliberately never labeled or displayed as HRV
// anywhere in this app — spot-reading dispersion between separate
// measurements is not beat-to-beat heart rate variability.
public class HrDispersion
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

    [JsonPropertyName("cv_spot")]
    public double? CvSpot { get; set; }
}

// §6.4 — Resting Trend: Ordinary Least Squares
public class HrTrend
{
    [JsonPropertyName("slope_bpm_per_day")]
    public double SlopeBpmPerDay { get; set; }

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
        "rising_significant" => "↑",
        "rising" => "↗",
        "falling_significant" => "↓",
        "falling" => "↘",
        _ => "→"
    };

    public string TrendColor => TrendLabel switch
    {
        "rising_significant" => "#d32f2f",
        "rising" => "#f57c00",
        "falling_significant" => "#388e3c",
        "falling" => "#66bb6a",
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
        SlopeBpmPerDay >= 0 ? $"+{SlopeBpmPerDay:F2} BPM/day" : $"{SlopeBpmPerDay:F2} BPM/day";

    public string PValueDisplay =>
        PValue is null ? "Not enough data yet for significance" : $"p = {PValue:F3}";

    public string ConsistencyDisplay => Consistency switch
    {
        "high" => $"High (R²={R2:F2})",
        "moderate" => $"Moderate (R²={R2:F2})",
        "low" => $"Low (R²={R2:F2})",
        _ => R2 is not null ? $"R²={R2:F2}" : "—"
    };
}

public class HrLoessPoint
{
    [JsonPropertyName("recorded_at")]
    public DateTime RecordedAt { get; set; }

    [JsonPropertyName("smoothed_bpm")]
    public double SmoothedBpm { get; set; }
}

// §6.6 — Personal Baseline Deviation. z_score is internal/clinician
// use only — never surfaced as a consumer-facing "abnormal" claim.
public class HrBaselineDeviation
{
    [JsonPropertyName("baseline_median")]
    public double BaselineMedian { get; set; }

    [JsonPropertyName("recent_median")]
    public double RecentMedian { get; set; }

    [JsonPropertyName("baseline_n")]
    public int BaselineN { get; set; }

    [JsonPropertyName("recent_n")]
    public int RecentN { get; set; }

    [JsonPropertyName("delta_bpm")]
    public double DeltaBpm { get; set; }

    [JsonPropertyName("delta_pct")]
    public double? DeltaPct { get; set; }

    [JsonPropertyName("z_score")]
    public double? ZScore { get; set; }

    public string DeltaDisplay =>
        DeltaBpm >= 0 ? $"+{DeltaBpm:F1} BPM" : $"{DeltaBpm:F1} BPM";
}

// §6.7 — High/Low Resting-Rate Event Analysis
public class HrRateEvents
{
    [JsonPropertyName("n_resting_in_window")]
    public int NRestingInWindow { get; set; }

    [JsonPropertyName("thresholds")]
    public HrThresholds? Thresholds { get; set; }

    [JsonPropertyName("high")]
    public HrEventBucket? High { get; set; }

    [JsonPropertyName("low")]
    public HrEventBucket? Low { get; set; }

    [JsonPropertyName("marked_low_count")]
    public int MarkedLowCount { get; set; }

    public bool HasMarkedLow => MarkedLowCount > 0;
}

public class HrThresholds
{
    [JsonPropertyName("high")]
    public int High { get; set; }

    [JsonPropertyName("low")]
    public int Low { get; set; }

    [JsonPropertyName("marked_low")]
    public int MarkedLow { get; set; }
}

public class HrEventBucket
{
    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("pct")]
    public double? Pct { get; set; }

    // StringToBoolConverter is built for strings — a nullable double
    // needs its own null check, since 0.0% is a real, valid value, not
    // an empty one that should be treated as falsy.
    public bool HasPct => Pct is not null;

    [JsonPropertyName("episode_count")]
    public int EpisodeCount { get; set; }

    [JsonPropertyName("readings")]
    public List<HrEventReading> Readings { get; set; } = new();

    public bool HasReadings => Readings.Count > 0;
}

public class HrEventReading
{
    [JsonPropertyName("recorded_at")]
    public DateTime RecordedAt { get; set; }

    [JsonPropertyName("bpm")]
    public int Bpm { get; set; }

    [JsonPropertyName("symptom_tags")]
    public List<string> SymptomTags { get; set; } = new();
}

// §6.8 — Time-of-Day Pattern Analysis. Buckets are fixed/known (not
// arbitrary keys), so a named-property class reads more naturally
// here — and in XAML bindings — than a dictionary would.
public class HrTimeOfDay
{
    [JsonPropertyName("buckets")]
    public HrTimeOfDayBuckets? Buckets { get; set; }

    [JsonPropertyName("pattern_summary")]
    public HrPatternSummary? PatternSummary { get; set; }
}

public class HrTimeOfDayBuckets
{
    [JsonPropertyName("morning")]
    public HrBucketStats? Morning { get; set; }

    [JsonPropertyName("afternoon")]
    public HrBucketStats? Afternoon { get; set; }

    [JsonPropertyName("evening")]
    public HrBucketStats? Evening { get; set; }

    [JsonPropertyName("overnight")]
    public HrBucketStats? Overnight { get; set; }

    public bool HasMorning => Morning is not null;
    public bool HasAfternoon => Afternoon is not null;
    public bool HasEvening => Evening is not null;
    public bool HasOvernight => Overnight is not null;
    public bool HasAny => HasMorning || HasAfternoon || HasEvening || HasOvernight;
}

public class HrBucketStats
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

    [JsonPropertyName("delta_from_overall_median")]
    public double DeltaFromOverallMedian { get; set; }
}

public class HrPatternSummary
{
    [JsonPropertyName("highest_period")]
    public string HighestPeriod { get; set; } = string.Empty;

    [JsonPropertyName("lowest_period")]
    public string LowestPeriod { get; set; } = string.Empty;
}

// §6.9 — Symptom Association. Tags are genuinely dynamic (whatever
// the user picked), so a dictionary is the right shape here, unlike
// the fixed time-of-day buckets above.
public class HrSymptomAssociationEntry
{
    [JsonPropertyName("associated_count")]
    public int AssociatedCount { get; set; }

    [JsonPropertyName("events")]
    public List<HrSymptomEvent> Events { get; set; } = new();

    [JsonPropertyName("pct_low")]
    public double? PctLow { get; set; }

    [JsonPropertyName("pct_high")]
    public double? PctHigh { get; set; }

    public bool HasPctLow => PctLow is not null;
    public bool HasPctHigh => PctHigh is not null;
}

// A flat, list-friendly view of one symptom-association entry — the
// server sends symptom_association as a dictionary keyed by tag name
// (see HeartRateAnalysis.SymptomAssociation), but a CollectionView item
// template needs the tag AND the entry's own fields on the SAME object
// to build one combined display string in C#, the same pattern used
// for HrUnavailableAnalysis and HrMedicationAssociation above.
public class HrSymptomAssociationItem
{
    public string Tag { get; set; } = string.Empty;
    public int AssociatedCount { get; set; }
    public List<HrSymptomEvent> Events { get; set; } = new();
    public double? PctLow { get; set; }
    public double? PctHigh { get; set; }

    public string TagDisplay =>
        string.IsNullOrWhiteSpace(Tag) ? "Unknown" : char.ToUpperInvariant(Tag[0]) + Tag[1..];

    public string HeaderDisplay => $"{TagDisplay} \u2014 {AssociatedCount} reading(s)";

    public bool HasPctLow => PctLow is not null;
    public bool HasPctHigh => PctHigh is not null;
}

public class HrSymptomEvent
{
    [JsonPropertyName("recorded_at")]
    public DateTime RecordedAt { get; set; }

    [JsonPropertyName("bpm")]
    public int Bpm { get; set; }

    [JsonPropertyName("time_diff_minutes")]
    public int TimeDiffMinutes { get; set; }

    [JsonPropertyName("posture")]
    public string Posture { get; set; } = "unknown";

    [JsonPropertyName("activity_context")]
    public string ActivityContext { get; set; } = "unknown";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "normal";
}

// §6.11 — Cross-Vital Same-Event Context. Reuses each OTHER vital's
// own classification (classify_bp/classify_spo2/classify_temp on the
// backend) rather than inventing heart-rate-specific thresholds for
// them.
public class HrCrossVitalContext
{
    [JsonPropertyName("blood_pressure")]
    public HrCooccurrence? BloodPressure { get; set; }

    [JsonPropertyName("oxygen_saturation")]
    public HrCooccurrence? OxygenSaturation { get; set; }

    [JsonPropertyName("temperature")]
    public HrCooccurrence? Temperature { get; set; }

    public bool HasBloodPressure => BloodPressure is not null;
    public bool HasOxygenSaturation => OxygenSaturation is not null;
    public bool HasTemperature => Temperature is not null;
    public bool HasAny => HasBloodPressure || HasOxygenSaturation || HasTemperature;
}

public class HrCooccurrence
{
    [JsonPropertyName("n_hr_condition_events_with_pair")]
    public int NHrConditionEventsWithPair { get; set; }

    [JsonPropertyName("co_occurrence_high")]
    public int CoOccurrenceHigh { get; set; }

    [JsonPropertyName("co_occurrence_low")]
    public int CoOccurrenceLow { get; set; }

    [JsonPropertyName("pct_high")]
    public double? PctHigh { get; set; }

    [JsonPropertyName("pct_low")]
    public double? PctLow { get; set; }
}

// §6.10 — Medication-Change Association
public class HrMedicationAssociation
{
    [JsonPropertyName("medication_id")]
    public string MedicationId { get; set; } = string.Empty;

    [JsonPropertyName("medication_name")]
    public string MedicationName { get; set; } = string.Empty;

    [JsonPropertyName("change_type")]
    public string ChangeType { get; set; } = string.Empty;

    [JsonPropertyName("effective_date")]
    public DateTime EffectiveDate { get; set; }

    [JsonPropertyName("pre_n")]
    public int PreN { get; set; }

    [JsonPropertyName("post_n")]
    public int PostN { get; set; }

    [JsonPropertyName("pre_median")]
    public double PreMedian { get; set; }

    [JsonPropertyName("post_median")]
    public double PostMedian { get; set; }

    [JsonPropertyName("delta_bpm")]
    public double DeltaBpm { get; set; }

    [JsonPropertyName("delta_pct")]
    public double? DeltaPct { get; set; }

    [JsonPropertyName("confounded")]
    public bool Confounded { get; set; }

    public string ChangeTypeDisplay => ChangeType switch
    {
        "started" => "Started",
        "stopped" => "Stopped",
        "restarted" => "Restarted",
        "dosage_changed" => "Dosage changed",
        "frequency_changed" => "Schedule changed",
        _ => ChangeType
    };

    public string DeltaDisplay =>
        DeltaBpm >= 0 ? $"+{DeltaBpm:F1} BPM" : $"{DeltaBpm:F1} BPM";

    // Both combine multiple fields into one string here, in C#, rather
    // than via FormattedString/Span or MultiBinding in XAML — same
    // reasoning as HrUnavailableAnalysis.DisplayText: a single plain
    // Label.Text binding to a model property is the pattern already
    // confirmed reliable on this page.
    public string NameAndChangeDisplay => $"{MedicationName} \u2014 {ChangeTypeDisplay}";

    public string BpmChangeDisplay =>
        $"{PreMedian:F0} BPM before \u2192 {PostMedian:F0} BPM after ({DeltaDisplay})";
}

// §6.3's prior-period comparison — fixed "most recent 30 days vs the
// 30 days before that," independent of whichever window is being
// viewed.
public class HrDispersionTrend
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("current")]
    public HrDispersionTrendBlock? Current { get; set; }

    [JsonPropertyName("prior")]
    public HrDispersionTrendBlock? Prior { get; set; }

    [JsonPropertyName("sd_delta")]
    public double? SdDelta { get; set; }
}

public class HrDispersionTrendBlock
{
    [JsonPropertyName("n")]
    public int N { get; set; }

    [JsonPropertyName("sd")]
    public double Sd { get; set; }

    [JsonPropertyName("iqr")]
    public double Iqr { get; set; }
}

// §6.12 — Data Support and Density
public class HrDataSupport
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

    public bool HasCoverage => DistinctDayCoveragePct is not null;

    [JsonPropertyName("unavailable_analyses")]
    public List<HrUnavailableAnalysis> UnavailableAnalyses { get; set; } = new();

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
}

public class HrUnavailableAnalysis
{
    // Named AnalysisName, not Analysis — the page-level
    // VitalsAnalysisViewModel already has a property called Analysis,
    // and a same-named property here risks silently resolving to the
    // wrong one inside this item's own binding context.
    [JsonPropertyName("analysis")]
    public string AnalysisName { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonIgnore]
    public string AnalysisNameDisplay => AnalysisName switch
    {
        "baseline_deviation" => "Baseline comparison",
        "dispersion_trend" => "Consistency comparison",
        "time_of_day" => "Time-of-day analysis",
        "symptom_association" => "Symptom association",
        "medication_association" => "Medication association",
        _ => Humanize(AnalysisName)
    };

    // Combines name + reason into one string here, in C#, rather than
    // in XAML via FormattedString/Span — a plain single Label.Text
    // binding to this is the same pattern already confirmed working
    // elsewhere on the page (SupportStateDisplay), and sidesteps
    // whatever was actually wrong with the Span-based approach.
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
