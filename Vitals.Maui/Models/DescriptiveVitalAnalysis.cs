using System.Text.Json.Serialization;

namespace Vitals.Maui.Models;

public class DescriptiveVitalAnalysis
{
    [JsonPropertyName("vital_type")]
    public string VitalType { get; set; } = string.Empty;

    [JsonPropertyName("unit")]
    public string Unit { get; set; } = string.Empty;

    [JsonPropertyName("latest")]
    public DescriptiveVitalLatest? Latest { get; set; }

    [JsonPropertyName("reading_count")]
    public int ReadingCount { get; set; }

    [JsonPropertyName("summary")]
    public DescriptiveVitalSummary? Summary { get; set; }

    [JsonPropertyName("change_from_first")]
    public DescriptiveVitalChange? ChangeFromFirst { get; set; }

    [JsonPropertyName("trend")]
    public DescriptiveVitalTrend? Trend { get; set; }

    [JsonPropertyName("data_support")]
    public DescriptiveVitalDataSupport? DataSupport { get; set; }

    public bool HasLatest => Latest is not null;
    public bool HasSummary => Summary is not null;
    public bool HasChange => ChangeFromFirst is not null;
    public bool HasTrend => Trend is not null;
    public bool HasUnavailableAnalyses => DataSupport?.UnavailableAnalyses.Count > 0;

    public string LatestDisplay =>
        Latest is null ? string.Empty : $"{Latest.Value:F1} {Unit}";

    public string SummaryDisplay =>
        Summary is null
            ? string.Empty
            : $"Mean {Summary.Mean:F1} {Unit} · Median {Summary.Median:F1} {Unit} · Range {Summary.Min:F1}–{Summary.Max:F1} {Unit}";

    public string ChangeDisplay =>
        ChangeFromFirst is null
            ? string.Empty
            : ChangeFromFirst.PctChange is null
                ? $"{ChangeFromFirst.AbsoluteChange:+0.0;-0.0;0.0} {Unit} from first logged reading"
                : $"{ChangeFromFirst.AbsoluteChange:+0.0;-0.0;0.0} {Unit} ({ChangeFromFirst.PctChange:+0.0;-0.0;0.0}%) from first logged reading";

    public string TrendDisplay =>
        Trend is null
            ? string.Empty
            : $"{Trend.SlopePerWeek:+0.00;-0.00;0.00} {Unit}/week across {Trend.SpanDays:F1} days";

    public string UnavailableAnalysesDisplay =>
        DataSupport is null || DataSupport.UnavailableAnalyses.Count == 0
            ? string.Empty
            : string.Join(
                Environment.NewLine,
                DataSupport.UnavailableAnalyses.Select(x => $"• {x.DisplayText}"));
}

public class DescriptiveVitalLatest
{
    [JsonPropertyName("value")]
    public double Value { get; set; }

    [JsonPropertyName("recorded_at")]
    public DateTime? RecordedAt { get; set; }
}

public class DescriptiveVitalSummary
{
    [JsonPropertyName("mean")]
    public double Mean { get; set; }

    [JsonPropertyName("median")]
    public double Median { get; set; }

    [JsonPropertyName("min")]
    public double Min { get; set; }

    [JsonPropertyName("max")]
    public double Max { get; set; }
}

public class DescriptiveVitalChange
{
    [JsonPropertyName("first_value")]
    public double FirstValue { get; set; }

    [JsonPropertyName("first_at")]
    public DateTime? FirstAt { get; set; }

    [JsonPropertyName("latest_value")]
    public double LatestValue { get; set; }

    [JsonPropertyName("latest_at")]
    public DateTime? LatestAt { get; set; }

    [JsonPropertyName("absolute_change")]
    public double AbsoluteChange { get; set; }

    [JsonPropertyName("pct_change")]
    public double? PctChange { get; set; }
}

public class DescriptiveVitalTrend
{
    [JsonPropertyName("n")]
    public int N { get; set; }

    [JsonPropertyName("span_days")]
    public double SpanDays { get; set; }

    [JsonPropertyName("slope_per_day")]
    public double SlopePerDay { get; set; }

    [JsonPropertyName("slope_per_week")]
    public double SlopePerWeek { get; set; }

    [JsonPropertyName("r2")]
    public double R2 { get; set; }

    [JsonPropertyName("p_value")]
    public double PValue { get; set; }

    public string FitDisplay => $"R² {R2:F2} · p={PValue:F3}";
}

public class DescriptiveVitalDataSupport
{
    [JsonPropertyName("n")]
    public int N { get; set; }

    [JsonPropertyName("distinct_days")]
    public int DistinctDays { get; set; }

    [JsonPropertyName("span_days")]
    public double SpanDays { get; set; }

    [JsonPropertyName("support_state")]
    public string SupportState { get; set; } = "snapshot";

    [JsonPropertyName("unavailable_analyses")]
    public List<DescriptiveVitalUnavailableAnalysis> UnavailableAnalyses { get; set; } = new();

    public string SupportStateDisplay => SupportState switch
    {
        "trend" => "Longitudinal trend available",
        "descriptive" => "Descriptive summary available",
        _ => "Snapshot only"
    };

    public string CoverageDisplay =>
        $"{N} readings · {DistinctDays} distinct days · {SpanDays:F1} day span";
}

public class DescriptiveVitalUnavailableAnalysis
{
    [JsonPropertyName("analysis")]
    public string Analysis { get; set; } = string.Empty;

    [JsonPropertyName("reason_code")]
    public string ReasonCode { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonIgnore]
    public string DisplayText =>
        string.IsNullOrWhiteSpace(Reason)
            ? Humanize(Analysis)
            : $"{Humanize(Analysis)}: {Reason}";

    private static string Humanize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var text = value.Replace("_", " ");
        return char.ToUpperInvariant(text[0]) + text[1..];
    }
}
