using System.Text.Json.Serialization;
using System.Linq;

namespace Vitals.Maui.Models;

// =====================================================
// TEMPERATURE ANALYSIS
//
// Matches the dedicated run_temperature_analysis JSON
// contract in the production API. Temperature is modeled
// as a sparse, episode-centric, measurement-site-aware
// vital. These classes intentionally do not recreate the
// retired generic "average/classification/time burden"
// model.
//
// As with HeartRateAnalysis/Spo2Analysis, XAML-facing
// combined strings live here as computed properties so
// collection templates can bind to one plain Label.Text.
// =====================================================
public class TemperatureAnalysis
{
    [JsonPropertyName("latest")]
    public TemperatureLatest? Latest { get; set; }

    [JsonPropertyName("reading_count")]
    public int ReadingCount { get; set; }

    [JsonPropertyName("target_profile")]
    public TemperatureTargetProfile? TargetProfile { get; set; }

    [JsonPropertyName("baseline")]
    public TemperatureBaseline? Baseline { get; set; }

    [JsonPropertyName("range_events")]
    public TemperatureRangeEvents? RangeEvents { get; set; }

    [JsonPropertyName("episodes")]
    public List<TemperatureEpisode> Episodes { get; set; } = new();

    [JsonPropertyName("latest_episode")]
    public TemperatureEpisode? LatestEpisode { get; set; }

    [JsonPropertyName("acute_trend")]
    public TemperatureAcuteTrend? AcuteTrend { get; set; }

    [JsonPropertyName("cross_vital_context")]
    public TemperatureCrossVitalContext? CrossVitalContext { get; set; }

    // P1 placeholders. The API deliberately returns null until the
    // corresponding structured data/capability exists. Keep the fields
    // in the contract now so adding those capabilities later does not
    // require a top-level model redesign.
    [JsonPropertyName("time_of_day_baseline")]
    public object? TimeOfDayBaseline { get; set; }

    [JsonPropertyName("symptom_associations")]
    public object? SymptomAssociations { get; set; }

    [JsonPropertyName("antipyretic_associations")]
    public object? AntipyreticAssociations { get; set; }

    [JsonPropertyName("pediatric_flags")]
    public object? PediatricFlags { get; set; }

    [JsonPropertyName("data_support")]
    public TemperatureDataSupport? DataSupport { get; set; }

    [JsonPropertyName("generated_at")]
    public DateTime? GeneratedAt { get; set; }

    // ---- Visibility gates ----
    public bool HasLatest => Latest is not null;
    public bool HasBaseline => Baseline is not null;
    public bool HasEpisodes => Episodes.Count > 0;
    public bool HasLatestEpisode => LatestEpisode is not null;
    public bool HasAcuteTrend => AcuteTrend is not null;
    public bool HasRangeEvents => RangeEvents is not null;
    public bool HasFeverObservations => RangeEvents?.FeverCount > 0;
    public bool HasHypothermiaRangeObservations => RangeEvents?.HypothermiaRangeCount > 0;
    public bool HasCrossVitalFeverObservations =>
        CrossVitalContext is not null && CrossVitalContext.FeverObservations.Count > 0;
    public bool HasUnavailableAnalyses =>
        DataSupport is not null && DataSupport.UnavailableAnalyses.Count > 0;
    public bool HasConfiguredLowTemperatureThreshold =>
        TargetProfile?.LowTemperatureThresholdF is not null;

    public int EpisodeCount => Episodes.Count;

    // Safe, spec-aligned wording for spot data. This deliberately says
    // "logged readings" and never implies continuous time burden.
    public string FeverLoggedSummary =>
        RangeEvents is null
            ? string.Empty
            : $"{RangeEvents.FeverCount} of {ReadingCount} logged reading(s) " +
              $"({RangeEvents.FeverLoggedPct:F1}%) were in the fever range.";

    public string FebrileDaysSummary =>
        RangeEvents is null
            ? string.Empty
            : RangeEvents.FebrileDays == 1
                ? "Fever-range readings were recorded on 1 distinct day."
                : $"Fever-range readings were recorded on {RangeEvents.FebrileDays} distinct days.";

    // Same pattern that fixed the SpO2 Data Confidence blank-space issue:
    // bind one plain Label.Text instead of a templated stack of fragments.
    public string UnavailableAnalysesDisplay =>
        DataSupport?.UnavailableAnalysesDisplay ?? string.Empty;
}

public class TemperatureLatest
{
    [JsonPropertyName("value_f")]
    public double ValueF { get; set; }

    [JsonPropertyName("value_c")]
    public double ValueC { get; set; }

    [JsonPropertyName("recorded_at")]
    public DateTime? RecordedAt { get; set; }

    [JsonPropertyName("site")]
    public string Site { get; set; } = "unknown";

    // Ingestion/application source, e.g. "maui_app".
    [JsonPropertyName("source")]
    public string Source { get; set; } = "unknown";

    // Reserved for true measurement provenance (manual, thermometer,
    // HealthKit/HealthConnect, etc.). Null in the current API.
    [JsonPropertyName("source_type")]
    public string? SourceType { get; set; }

    [JsonPropertyName("linked_context")]
    public TemperatureLinkedContext? LinkedContext { get; set; }

    public string ValueDisplay => $"{ValueF:F1} °F";
    public string DualUnitDisplay => $"{ValueF:F1} °F  ({ValueC:F1} °C)";
    public string SiteDisplay => TemperatureModelFormatting.SiteDisplay(Site);
    public string SourceDisplay => TemperatureModelFormatting.SourceDisplay(Source);
    public bool HasSourceType => !string.IsNullOrWhiteSpace(SourceType);
    public string SourceTypeDisplay => TemperatureModelFormatting.SourceTypeDisplay(SourceType);
    public bool HasKnownSite => !string.Equals(Site, "unknown", StringComparison.OrdinalIgnoreCase);
    public bool HasLinkedContext => LinkedContext?.HasAny == true;

    public string MeasurementContextDisplay =>
        HasSourceType
            ? $"{SiteDisplay} · {SourceTypeDisplay}"
            : $"{SiteDisplay} · {SourceDisplay}";
}

public class TemperatureLinkedContext
{
    [JsonPropertyName("heart_rate")]
    public int? HeartRate { get; set; }

    [JsonPropertyName("oxygen_saturation")]
    public int? OxygenSaturation { get; set; }

    [JsonPropertyName("blood_pressure")]
    public LinkedBp? BloodPressure { get; set; }

    [JsonPropertyName("blood_glucose")]
    public int? BloodGlucose { get; set; }

    [JsonPropertyName("weight")]
    public double? Weight { get; set; }

    public bool HasHeartRate => HeartRate is not null;
    public bool HasOxygenSaturation => OxygenSaturation is not null;
    public bool HasBloodPressure => BloodPressure is not null;
    public bool HasBloodGlucose => BloodGlucose is not null;
    public bool HasWeight => Weight is not null;
    public bool HasAny =>
        HasHeartRate || HasOxygenSaturation || HasBloodPressure || HasBloodGlucose || HasWeight;
}

public class TemperatureTargetProfile
{
    [JsonPropertyName("fever_threshold_f")]
    public double FeverThresholdF { get; set; }

    [JsonPropertyName("fever_threshold_c")]
    public double FeverThresholdC { get; set; }

    // Intentionally nullable: the current product has not defined a
    // broader low-temperature product threshold and must not invent one.
    [JsonPropertyName("low_temperature_threshold_f")]
    public double? LowTemperatureThresholdF { get; set; }

    [JsonPropertyName("low_temperature_threshold_c")]
    public double? LowTemperatureThresholdC { get; set; }

    [JsonPropertyName("hypothermia_threshold_f")]
    public double HypothermiaThresholdF { get; set; }

    [JsonPropertyName("hypothermia_threshold_c")]
    public double HypothermiaThresholdC { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("patient_specific")]
    public bool PatientSpecific { get; set; }

    public bool HasLowTemperatureThreshold => LowTemperatureThresholdF is not null;

    public string SourceDisplay =>
        PatientSpecific ? "Patient-specific reference" : "Reference default (not individualized)";

    public string FeverReferenceDisplay =>
        $"{FeverThresholdF:F1} °F / {FeverThresholdC:F1} °C";

    public string HypothermiaReferenceDisplay =>
        $"Below {HypothermiaThresholdF:F1} °F / {HypothermiaThresholdC:F1} °C";
}

public class TemperatureBaseline
{
    [JsonPropertyName("site")]
    public string Site { get; set; } = "unknown";

    [JsonPropertyName("median_f")]
    public double MedianF { get; set; }

    [JsonPropertyName("median_c")]
    public double MedianC { get; set; }

    [JsonPropertyName("n")]
    public int N { get; set; }

    [JsonPropertyName("distinct_days")]
    public int DistinctDays { get; set; }

    [JsonPropertyName("span_days")]
    public double SpanDays { get; set; }

    [JsonPropertyName("delta_current_f")]
    public double DeltaCurrentF { get; set; }

    [JsonPropertyName("delta_current_c")]
    public double DeltaCurrentC { get; set; }

    public string SiteDisplay => TemperatureModelFormatting.SiteDisplay(Site);
    public string MedianDisplay => $"{MedianF:F1} °F";
    public string MedianDualUnitDisplay => $"{MedianF:F1} °F  ({MedianC:F1} °C)";
    public string DeltaCurrentDisplay => TemperatureModelFormatting.SignedDegreesF(DeltaCurrentF);
    public string DeltaCurrentCDisplay => TemperatureModelFormatting.SignedDegreesC(DeltaCurrentC);
    public string SupportDisplay => $"{N} readings across {DistinctDays} days";
    public string SpanDisplay => $"{SpanDays:F1} day baseline span";
}

public class TemperatureRangeEvents
{
    [JsonPropertyName("fever_threshold_f")]
    public double FeverThresholdF { get; set; }

    [JsonPropertyName("fever_count")]
    public int FeverCount { get; set; }

    [JsonPropertyName("fever_logged_pct")]
    public double FeverLoggedPct { get; set; }

    [JsonPropertyName("febrile_days")]
    public int FebrileDays { get; set; }

    [JsonPropertyName("low_temperature_threshold_f")]
    public double? LowTemperatureThresholdF { get; set; }

    [JsonPropertyName("low_temperature_count")]
    public int? LowTemperatureCount { get; set; }

    [JsonPropertyName("low_temperature_readings")]
    public List<TemperatureRangeReading>? LowTemperatureReadings { get; set; }

    [JsonPropertyName("hypothermia_threshold_f")]
    public double HypothermiaThresholdF { get; set; }

    [JsonPropertyName("hypothermia_range_count")]
    public int HypothermiaRangeCount { get; set; }

    [JsonPropertyName("hypothermia_readings")]
    public List<TemperatureRangeReading> HypothermiaReadings { get; set; } = new();

    [JsonPropertyName("lowest_f")]
    public double LowestF { get; set; }

    [JsonPropertyName("lowest_c")]
    public double LowestC { get; set; }

    [JsonPropertyName("lowest_at")]
    public DateTime? LowestAt { get; set; }

    [JsonPropertyName("lowest_site")]
    public string LowestSite { get; set; } = "unknown";

    public bool HasFeverReadings => FeverCount > 0;
    public bool HasHypothermiaReadings => HypothermiaRangeCount > 0;
    public bool HasLowTemperatureCapability => LowTemperatureThresholdF is not null;
    public bool HasLowTemperatureReadings => LowTemperatureCount > 0;
    public string LowestDisplay => $"{LowestF:F1} °F";
    public string LowestSiteDisplay => TemperatureModelFormatting.SiteDisplay(LowestSite);

    // Explicitly says "logged readings", never "time".
    public string FeverLoggedDisplay =>
        $"{FeverCount} logged reading(s) ({FeverLoggedPct:F1}%)";

    public string FebrileDaysDisplay =>
        FebrileDays == 1 ? "1 distinct day" : $"{FebrileDays} distinct days";

    public string HypothermiaRangeDisplay =>
        HypothermiaRangeCount == 1
            ? "1 hypothermia-range reading"
            : $"{HypothermiaRangeCount} hypothermia-range readings";
}

public class TemperatureRangeReading
{
    [JsonPropertyName("recorded_at")]
    public DateTime? RecordedAt { get; set; }

    [JsonPropertyName("value_f")]
    public double ValueF { get; set; }

    [JsonPropertyName("value_c")]
    public double? ValueC { get; set; }

    [JsonPropertyName("site")]
    public string Site { get; set; } = "unknown";

    public string SiteDisplay => TemperatureModelFormatting.SiteDisplay(Site);
    public double EffectiveValueC => ValueC ?? TemperatureModelFormatting.ToCelsius(ValueF);
    public string DualUnitDisplay => $"{ValueF:F1} °F  ({EffectiveValueC:F1} °C)";
    public string DisplayText =>
        RecordedAt is null
            ? $"{ValueF:F1} °F · {SiteDisplay}"
            : $"{ValueF:F1} °F · {SiteDisplay} · {RecordedAt:MMM d, h:mm tt}";
}

public class TemperatureEpisode
{
    [JsonPropertyName("episode_id")]
    public int EpisodeId { get; set; }

    [JsonPropertyName("first_fever_at")]
    public DateTime FirstFeverAt { get; set; }

    [JsonPropertyName("last_fever_at")]
    public DateTime LastFeverAt { get; set; }

    [JsonPropertyName("observed_span_hours")]
    public double ObservedSpanHours { get; set; }

    [JsonPropertyName("peak_f")]
    public double PeakF { get; set; }

    [JsonPropertyName("peak_c")]
    public double PeakC { get; set; }

    [JsonPropertyName("peak_at")]
    public DateTime PeakAt { get; set; }

    [JsonPropertyName("peak_site")]
    public string PeakSite { get; set; } = "unknown";

    [JsonPropertyName("minimum_f")]
    public double MinimumF { get; set; }

    [JsonPropertyName("minimum_c")]
    public double MinimumC { get; set; }

    [JsonPropertyName("minimum_at")]
    public DateTime MinimumAt { get; set; }

    [JsonPropertyName("minimum_site")]
    public string MinimumSite { get; set; } = "unknown";

    [JsonPropertyName("n_fever_readings")]
    public int FeverReadingCount { get; set; }

    [JsonPropertyName("febrile_days")]
    public int FebrileDays { get; set; }

    [JsonPropertyName("sites")]
    public List<string> Sites { get; set; } = new();

    [JsonPropertyName("mixed_sites")]
    public bool MixedSites { get; set; }

    [JsonPropertyName("delta_latest_from_peak_f")]
    public double? DeltaLatestFromPeakF { get; set; }

    [JsonPropertyName("hours_since_peak")]
    public double? HoursSincePeak { get; set; }

    public string PeakSiteDisplay => TemperatureModelFormatting.SiteDisplay(PeakSite);
    public string MinimumSiteDisplay => TemperatureModelFormatting.SiteDisplay(MinimumSite);
    public string PeakDisplay => $"{PeakF:F1} °F";
    public string MinimumDisplay => $"{MinimumF:F1} °F";
    public bool HasLatestDelta => DeltaLatestFromPeakF is not null;
    public string DeltaLatestFromPeakDisplay =>
        DeltaLatestFromPeakF is null
            ? "Not available for cross-site/unknown-site comparison"
            : TemperatureModelFormatting.SignedDegreesF(DeltaLatestFromPeakF.Value);
    public string SitesDisplay =>
        Sites.Count == 0
            ? "Unknown"
            : string.Join(", ", Sites.Select(TemperatureModelFormatting.SiteDisplay));

    // "Observed span" is deliberate: sparse readings do not establish
    // continuous fever duration.
    public string SpanDisplay => $"{ObservedSpanHours:F1} h observed span";

    public string ObservedSpanSentence =>
        $"Fever-range readings were observed across {ObservedSpanHours:F1} hours.";

    public string PeakContextDisplay =>
        $"Peak {PeakF:F1} °F · {PeakSiteDisplay} · {PeakAt:MMM d, h:mm tt}";

    public string SummaryDisplay =>
        $"Peak {PeakF:F1} °F · {FeverReadingCount} fever-range reading(s) · {FebrileDays} febrile day(s)";
}

public class TemperatureAcuteTrend
{
    [JsonPropertyName("site")]
    public string Site { get; set; } = "unknown";

    [JsonPropertyName("n")]
    public int N { get; set; }

    [JsonPropertyName("span_hours")]
    public double SpanHours { get; set; }

    [JsonPropertyName("slope_f_per_hour")]
    public double SlopeFPerHour { get; set; }

    [JsonPropertyName("slope_f_per_12_hours")]
    public double SlopeFPer12Hours { get; set; }

    [JsonPropertyName("modeled_change_f")]
    public double ModeledChangeF { get; set; }

    [JsonPropertyName("r2")]
    public double? R2 { get; set; }

    [JsonPropertyName("p_value")]
    public double? PValue { get; set; }

    [JsonPropertyName("trend_label")]
    public string TrendLabel { get; set; } = "stable";

    [JsonPropertyName("first_reading_at")]
    public DateTime FirstReadingAt { get; set; }

    [JsonPropertyName("last_reading_at")]
    public DateTime LastReadingAt { get; set; }

    [JsonPropertyName("includes_post_fever_readings")]
    public bool IncludesPostFeverReadings { get; set; }

    public string SiteDisplay => TemperatureModelFormatting.SiteDisplay(Site);

    // Direction only; no "good/bad" interpretation is attached to rising
    // or falling temperature.
    public string TrendArrow => TrendLabel switch
    {
        "rising" => "↗",
        "falling" => "↘",
        _ => "→"
    };

    public string TrendLabelDisplay => TrendLabel switch
    {
        "rising" => "Rising",
        "falling" => "Falling",
        _ => "Stable"
    };

    public bool IsRising => TrendLabel == "rising";
    public bool IsStable => TrendLabel == "stable";
    public bool IsFalling => TrendLabel == "falling";

    public string SlopeDisplay => $"{SlopeFPer12Hours:+0.00;-0.00;0.00} °F / 12 h";
    public string ModeledChangeDisplay => TemperatureModelFormatting.SignedDegreesF(ModeledChangeF);
    public string PValueDisplay => PValue is null ? "Not available" : $"p = {PValue:F3}";
    public string R2Display => R2 is null ? "Not available" : $"R² = {R2:F2}";
    public string SupportDisplay => $"{N} same-site readings across {SpanHours:F1} h";
    public string TrendSummaryDisplay => $"{TrendLabelDisplay} · {SlopeDisplay} · {SupportDisplay}";
}

public class TemperatureCrossVitalContext
{
    [JsonPropertyName("fever_observations")]
    public List<TemperatureFeverObservation> FeverObservations { get; set; } = new();

    [JsonPropertyName("paired_counts")]
    public TemperaturePairedCounts? PairedCounts { get; set; }

    public bool HasFeverObservations => FeverObservations.Count > 0;
    public bool HasAnyPairedVitals => PairedCounts?.HasAny == true;
}

public class TemperatureFeverObservation
{
    [JsonPropertyName("recorded_at")]
    public DateTime RecordedAt { get; set; }

    [JsonPropertyName("temperature_f")]
    public double TemperatureF { get; set; }

    [JsonPropertyName("site")]
    public string Site { get; set; } = "unknown";

    [JsonPropertyName("context")]
    public TemperatureCrossVitalObservationContext? Context { get; set; }

    public string SiteDisplay => TemperatureModelFormatting.SiteDisplay(Site);
    public bool HasContext => Context?.HasAny == true;
    public string DisplayText => $"{TemperatureF:F1} °F · {SiteDisplay} · {RecordedAt:MMM d, h:mm tt}";
}

public class TemperatureCrossVitalObservationContext
{
    [JsonPropertyName("heart_rate")]
    public int? HeartRate { get; set; }

    [JsonPropertyName("oxygen_saturation")]
    public int? OxygenSaturation { get; set; }

    [JsonPropertyName("blood_pressure")]
    public LinkedBp? BloodPressure { get; set; }

    public bool HasHeartRate => HeartRate is not null;
    public bool HasOxygenSaturation => OxygenSaturation is not null;
    public bool HasBloodPressure => BloodPressure is not null;
    public bool HasAny => HasHeartRate || HasOxygenSaturation || HasBloodPressure;
}

public class TemperaturePairedCounts
{
    [JsonPropertyName("heart_rate")]
    public int HeartRate { get; set; }

    [JsonPropertyName("oxygen_saturation")]
    public int OxygenSaturation { get; set; }

    [JsonPropertyName("blood_pressure")]
    public int BloodPressure { get; set; }

    public bool HasAny => HeartRate > 0 || OxygenSaturation > 0 || BloodPressure > 0;
}

public class TemperatureDataSupport
{
    [JsonPropertyName("n")]
    public int N { get; set; }

    [JsonPropertyName("distinct_days")]
    public int DistinctDays { get; set; }

    [JsonPropertyName("span_days")]
    public double SpanDays { get; set; }

    [JsonPropertyName("known_site_pct")]
    public double KnownSitePct { get; set; }

    [JsonPropertyName("modal_site")]
    public string? ModalSite { get; set; }

    [JsonPropertyName("site_consistency_pct")]
    public double? SiteConsistencyPct { get; set; }

    [JsonPropertyName("mixed_measurement_sites")]
    public bool MixedMeasurementSites { get; set; }

    [JsonPropertyName("support_state")]
    public string SupportState { get; set; } = "snapshot";

    [JsonPropertyName("unavailable_analyses")]
    public List<TemperatureUnavailableAnalysis> UnavailableAnalyses { get; set; } = new();

    public bool HasKnownSiteCoverage => KnownSitePct > 0;
    public bool HasSiteConsistency => SiteConsistencyPct is not null;
    public bool HasUnavailableAnalyses => UnavailableAnalyses.Count > 0;
    public string? ModalSiteDisplay =>
        string.IsNullOrWhiteSpace(ModalSite)
            ? null
            : TemperatureModelFormatting.SiteDisplay(ModalSite);

    public string SupportStateDisplay => SupportState switch
    {
        "snapshot" => "Snapshot only",
        "descriptive" => "Descriptive summary available",
        "baseline" => "Same-site baseline available",
        "episode" => "Fever-episode analysis available",
        "acute_trend" => "Acute trajectory available",
        _ => SupportState
    };

    public string CoverageDisplay =>
        $"{N} readings · {DistinctDays} days · {SpanDays:F1} day span";

    public string KnownSiteCoverageDisplay =>
        $"{KnownSitePct:F1}% of readings have a known measurement site";

    public string SiteConsistencyDisplay =>
        SiteConsistencyPct is null
            ? "Site consistency unavailable"
            : $"{SiteConsistencyPct:F1}% same-site consistency";

    public bool HasMeasurementSiteLimitation =>
        KnownSitePct < 100.0 || MixedMeasurementSites;

    public string MeasurementSiteLimitationDisplay =>
        MixedMeasurementSites
            ? "Mixed measurement sites limit direct comparison; same-method readings are more comparable."
            : KnownSitePct <= 0
                ? "Measurement site is unknown, which limits site-sensitive comparisons."
                : KnownSitePct < 100.0
                    ? "Some readings have an unknown measurement site, which limits site-sensitive comparisons."
                    : string.Empty;

    [JsonIgnore]
    public string UnavailableAnalysesDisplay =>
        HasUnavailableAnalyses
            ? string.Join(
                Environment.NewLine,
                UnavailableAnalyses
                    .Where(x => !string.IsNullOrWhiteSpace(x.DisplayText))
                    .Select(x => $"• {x.DisplayText}"))
            : string.Empty;
}

public class TemperatureUnavailableAnalysis
{
    [JsonPropertyName("analysis")]
    public string AnalysisName { get; set; } = string.Empty;

    [JsonPropertyName("reason_code")]
    public string ReasonCode { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonIgnore]
    public string AnalysisNameDisplay => AnalysisName switch
    {
        "personal_baseline" => "Personal baseline",
        "fever_episode" => "Fever episode",
        "acute_trend" => "Acute trend",
        "low_temperature" => "Low-temperature analysis",
        "site_sensitive_comparison" => "Site-sensitive comparison",
        "cross_vital_context" => "Cross-vital context",
        "symptom_association" => "Symptom association",
        "antipyretic_association" => "Antipyretic association",
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

internal static class TemperatureModelFormatting
{
    public static string SiteDisplay(string? site) => (site ?? "unknown").ToLowerInvariant() switch
    {
        "oral" => "Oral",
        "rectal" => "Rectal",
        "axillary" => "Axillary",
        "tympanic" => "Tympanic",
        "temporal" => "Temporal",
        "other" => "Other",
        _ => "Unknown"
    };

    public static string SourceDisplay(string? source) => (source ?? "unknown").ToLowerInvariant() switch
    {
        "maui_app" => "Vitals app",
        "home_assistant" => "Home Assistant",
        _ => string.IsNullOrWhiteSpace(source) ? "Unknown" : source.Replace("_", " ")
    };

    public static string SourceTypeDisplay(string? sourceType) => (sourceType ?? "unknown").ToLowerInvariant() switch
    {
        "manual" => "Manual",
        "thermometer" => "Thermometer",
        "healthkit" => "HealthKit",
        "health_connect" => "Health Connect",
        "healthconnect" => "Health Connect",
        "other" => "Other",
        _ => "Unknown"
    };

    public static double ToCelsius(double valueF) =>
        (valueF - 32.0) * 5.0 / 9.0;

    public static string SignedDegreesF(double value) =>
        $"{value:+0.0;-0.0;0.0} °F";

    public static string SignedDegreesC(double value) =>
        $"{value:+0.0;-0.0;0.0} °C";
}
