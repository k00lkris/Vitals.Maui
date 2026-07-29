using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vitals.Maui.Models;

public class VitalsAnalysis
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("reading_count")]
    public int ReadingCount { get; set; }

    [JsonPropertyName("days")]
    public int Days { get; set; }

    [JsonPropertyName("readings_needed")]
    public int ReadingsNeeded { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("systolic")]
    public BpAnalysis? Systolic { get; set; }

    [JsonPropertyName("diastolic")]
    public BpAnalysis? Diastolic { get; set; }

    [JsonPropertyName("burden")]
    public BurdenAnalysis? Burden { get; set; }

    [JsonPropertyName("hypo_burden")]
    public HypoBurdenAnalysis? HypoBurden { get; set; }

    [JsonPropertyName("heart_rate")]
    public SecondaryAnalysis? HeartRate { get; set; }

    [JsonPropertyName("spo2")]
    public SecondaryAnalysis? Spo2 { get; set; }

    [JsonPropertyName("temperature")]
    public SecondaryAnalysis? Temperature { get; set; }

    [JsonPropertyName("classification")]
    public string? Classification { get; set; }

    [JsonPropertyName("pcp_name")]
    public string? PcpName { get; set; }

    [JsonPropertyName("next_followup")]
    public string? NextFollowup { get; set; }

    [JsonPropertyName("map")]
    public MapAnalysis? Map { get; set; }

    [JsonPropertyName("sbp_burden")]
    public SbpBurdenAnalysis? SbpBurden { get; set; }

    [JsonPropertyName("ttr")]
    public TtrBurdenAnalysis? Ttr { get; set; }

    [JsonPropertyName("dbp_burden")]
    public DbpBurdenAnalysis? DbpBurden { get; set; }

    [JsonPropertyName("low_dbp_burden")]
    public LowDbpBurdenAnalysis? LowDbpBurden { get; set; }
    public bool IsHypotension =>
        Classification == "hypotension" || Classification == "borderline_hypotension";
    public bool IsInsufficient => Status == "insufficient_data";
    public bool IsOk => Status == "ok";

    public bool ShowLowDiastolicAlert =>
    LowDbpBurden?.HasCritical == true ||
    LowDbpBurden?.SeverePct >= 15;

    public string LowDiastolicAlertText
    {
        get
        {
            if (LowDbpBurden?.HasCritical == true)
            {
                var readings = string.Join(", ",
                    LowDbpBurden.CriticalReadings.Select(r => $"{r:F0}"));
                return $"⚠ Critical: diastolic reading(s) of {readings} mmHg recorded. " +
                       $"Severely low diastolic pressure may impair coronary perfusion. " +
                       $"Consider contacting the care team.";
            }
            if (LowDbpBurden?.SeverePct >= 15)
                return $"⚠ Diastolic pressure was below 60 mmHg {LowDbpBurden.SeverePct:F1}% " +
                       $"of the time — persistent diastolic hypotension. Clinical review recommended.";
            return string.Empty;
        }
    }

    public string ClassificationDisplay => Classification switch
    {
        "hypotension" => "⚠️ Hypotension",
        "borderline_hypotension" => "🔶 Borderline Hypotension",
        "normal" => "✅ Normal",
        "elevated" => "🟡 Elevated",
        "stage1" => "🟠 Stage 1 Hypertension",
        "stage2" => "🔴 Stage 2 Hypertension",
        _ => "— Unknown"
    };

    public string ClassificationColor => Classification switch
    {
        "hypotension" => "#e65100",
        "borderline_hypotension" => "#ffa726",
        "normal" => "#388e3c",
        "elevated" => "#f57c00",
        "stage1" => "#d32f2f",
        "stage2" => "#7b1fa2",
        _ => "#888888"
    };
}

public class BpAnalysis
{
    [JsonPropertyName("avg")]
    public double Avg { get; set; }

    [JsonPropertyName("slope")]
    public double Slope { get; set; }

    [JsonPropertyName("r2")]
    public double R2 { get; set; }

    [JsonPropertyName("p_value")]
    public double PValue { get; set; }

    [JsonPropertyName("significant")]
    public bool Significant { get; set; }

    [JsonPropertyName("trend")]
    public string Trend { get; set; } = string.Empty;

    [JsonPropertyName("consistency")]
    public string Consistency { get; set; } = string.Empty;

    [JsonPropertyName("momentum")]
    public string Momentum { get; set; } = string.Empty;

    public string TrendArrow => Trend switch
    {
        "rising_significant" => "↑",
        "rising" => "↗",
        "falling_significant" => "↓",
        "falling" => "↘",
        "stable" => "→",
        _ => "→"
    };

    public string TrendColor => Trend switch
    {
        "rising_significant" => "#d32f2f",
        "rising" => "#f57c00",
        "falling_significant" => "#388e3c",
        "falling" => "#66bb6a",
        "stable" => "#90caf9",
        _ => "#90caf9"
    };

    public string TrendLabel => Trend switch
    {
        "rising_significant" => "Rising (significant)",
        "rising" => "Rising",
        "falling_significant" => "Falling (significant)",
        "falling" => "Falling",
        "stable" => "Stable",
        _ => "Stable"
    };

    public string ConsistencyLabel => Consistency switch
    {
        "high" => $"High (R²={R2:F2})",
        "moderate" => $"Moderate (R²={R2:F2})",
        "low" => $"Low (R²={R2:F2})",
        _ => $"R²={R2:F2}"
    };

    public string MomentumLabel => Momentum switch
    {
        "accelerating" => "Accelerating",
        "decelerating" => "Slowing down",
        "stable" => "Stable",
        _ => "Stable"
    };

    public string SlopeDisplay =>
        Slope >= 0 ? $"+{Slope:F1} mmHg/day" : $"{Slope:F1} mmHg/day";

    public string PValueDisplay =>
        Significant
            ? $"Significant (p={PValue:F3})"
            : $"Not significant (p={PValue:F3})";
}

public class BurdenAnalysis
{
    [JsonPropertyName("normal_pct")]
    public double NormalPct { get; set; }

    [JsonPropertyName("elevated_pct")]
    public double ElevatedPct { get; set; }

    [JsonPropertyName("stage1_pct")]
    public double Stage1Pct { get; set; }

    [JsonPropertyName("stage2_pct")]
    public double Stage2Pct { get; set; }

    public string NormalDisplay => $"{NormalPct:F0}%";
    public string ElevatedDisplay => $"{ElevatedPct:F0}%";
    public string Stage1Display => $"{Stage1Pct:F0}%";
    public string Stage2Display => $"{Stage2Pct:F0}%";
}

public class HypoBurdenAnalysis
{
    [JsonPropertyName("severe_pct")]
    public double SeverePct { get; set; }

    [JsonPropertyName("moderate_pct")]
    public double ModeratePct { get; set; }

    [JsonPropertyName("normal_pct")]
    public double NormalPct { get; set; }

    public string SevereDisplay => $"{SeverePct:F0}%";
    public string ModerateDisplay => $"{ModeratePct:F0}%";
    public string NormalDisplay => $"{NormalPct:F0}%";
}

// =====================================================
// HR BURDEN
// =====================================================
public class HrBurden
{
    [JsonPropertyName("bradycardia_pct")]
    public double BradycardiaPct { get; set; }

    [JsonPropertyName("normal_pct")]
    public double NormalPct { get; set; }

    [JsonPropertyName("mild_tachy_pct")]
    public double MildTachyPct { get; set; }

    [JsonPropertyName("tachycardia_pct")]
    public double TachycardiaPct { get; set; }

    public string BradycardiaDisplay => $"{BradycardiaPct:F0}%";
    public string NormalDisplay => $"{NormalPct:F0}%";
    public string MildTachyDisplay => $"{MildTachyPct:F0}%";
    public string TachycardiaDisplay => $"{TachycardiaPct:F0}%";
}

// =====================================================
// SPO2 BURDEN
// =====================================================
public class Spo2Burden
{
    [JsonPropertyName("normal_pct")]
    public double NormalPct { get; set; }

    [JsonPropertyName("mild_hypoxemia_pct")]
    public double MildHypoxemiaPct { get; set; }

    [JsonPropertyName("moderate_hypoxemia_pct")]
    public double ModerateHypoxemiaPct { get; set; }

    [JsonPropertyName("severe_hypoxemia_pct")]
    public double SevereHypoxemiaPct { get; set; }

    public string NormalDisplay => $"{NormalPct:F0}%";
    public string MildHypoxemiaDisplay => $"{MildHypoxemiaPct:F0}%";
    public string ModerateHypoxemiaDisplay => $"{ModerateHypoxemiaPct:F0}%";
    public string SevereHypoxemiaDisplay => $"{SevereHypoxemiaPct:F0}%";
}

// =====================================================
// TEMPERATURE BURDEN
// =====================================================
public class TempBurden
{
    [JsonPropertyName("hypothermia_pct")]
    public double HypothermiaPct { get; set; }

    [JsonPropertyName("normal_pct")]
    public double NormalPct { get; set; }

    [JsonPropertyName("elevated_pct")]
    public double ElevatedPct { get; set; }

    [JsonPropertyName("fever_pct")]
    public double FeverPct { get; set; }

    [JsonPropertyName("high_fever_pct")]
    public double HighFeverPct { get; set; }

    public string HypothermiaDisplay => $"{HypothermiaPct:F0}%";
    public string NormalDisplay => $"{NormalPct:F0}%";
    public string ElevatedDisplay => $"{ElevatedPct:F0}%";
    public string FeverDisplay => $"{FeverPct:F0}%";
    public string HighFeverDisplay => $"{HighFeverPct:F0}%";
}

// =====================================================
// SECONDARY ANALYSIS (HR / SPO2 / TEMP)
// =====================================================
public class SecondaryAnalysis
{
    [JsonPropertyName("avg")]
    public double Avg { get; set; }

    [JsonPropertyName("slope")]
    public double Slope { get; set; }

    [JsonPropertyName("r2")]
    public double R2 { get; set; }

    [JsonPropertyName("p_value")]
    public double PValue { get; set; }

    [JsonPropertyName("significant")]
    public bool Significant { get; set; }

    [JsonPropertyName("trend")]
    public string Trend { get; set; } = string.Empty;

    [JsonPropertyName("consistency")]
    public string Consistency { get; set; } = string.Empty;

    [JsonPropertyName("reading_count")]
    public int ReadingCount { get; set; }

    [JsonPropertyName("classification")]
    public string? Classification { get; set; }

    // Raw burden JsonElement — deserialized into typed classes below
    [JsonPropertyName("burden")]
    public JsonElement? Burden { get; set; }

    // Typed burden accessors
    public HrBurden? HrBurden => TryDeserializeBurden<HrBurden>();
    public Spo2Burden? Spo2Burden => TryDeserializeBurden<Spo2Burden>();
    public TempBurden? TempBurden => TryDeserializeBurden<TempBurden>();

    private T? TryDeserializeBurden<T>() where T : class
    {
        try
        {
            if (Burden is null) return null;
            return JsonSerializer.Deserialize<T>(Burden.Value.GetRawText());
        }
        catch { return null; }
    }

    public string TrendArrow => Trend switch
    {
        "rising_significant" => "↑",
        "rising" => "↗",
        "falling_significant" => "↓",
        "falling" => "↘",
        "stable" => "→",
        _ => "→"
    };

    public string TrendColor => Trend switch
    {
        "rising_significant" => "#d32f2f",
        "rising" => "#f57c00",
        "falling_significant" => "#388e3c",
        "falling" => "#66bb6a",
        "stable" => "#90caf9",
        _ => "#90caf9"
    };

    public string TrendLabel => Trend switch
    {
        "rising_significant" => "Rising (significant)",
        "rising" => "Rising",
        "falling_significant" => "Falling (significant)",
        "falling" => "Falling",
        "stable" => "Stable",
        _ => "Stable"
    };

    public string SlopeDisplay =>
        Slope >= 0 ? $"+{Slope:F1}" : $"{Slope:F1}";

    public string PValueDisplay =>
        Significant
            ? $"Significant (p={PValue:F3})"
            : $"Not significant (p={PValue:F3})";

    public string ConsistencyLabel =>
        $"{(Consistency == "high" ? "High" : Consistency == "moderate" ? "Moderate" : "Low")} (R²={R2:F2})";

    // HR classification display
    public string HrClassificationDisplay => Classification switch
    {
        "bradycardia" => "Bradycardia",
        "normal" => "Normal",
        "mild_tachycardia" => "Mild Tachycardia",
        "tachycardia" => "Tachycardia",
        _ => "Unknown"
    };

    public string HrClassificationColor => Classification switch
    {
        "bradycardia" => "#1976d2",
        "normal" => "#388e3c",
        "mild_tachycardia" => "#f57c00",
        "tachycardia" => "#d32f2f",
        _ => "#888888"
    };

    // SpO2 classification display
    public string Spo2ClassificationDisplay => Classification switch
    {
        "normal" => "Normal",
        "mild_hypoxemia" => "Mild Hypoxemia",
        "moderate_hypoxemia" => "Moderate Hypoxemia",
        "severe_hypoxemia" => "Severe Hypoxemia",
        _ => "Unknown"
    };

    public string Spo2ClassificationColor => Classification switch
    {
        "normal" => "#388e3c",
        "mild_hypoxemia" => "#f57c00",
        "moderate_hypoxemia" => "#d32f2f",
        "severe_hypoxemia" => "#7b1fa2",
        _ => "#888888"
    };

    // Temperature classification display
    public string TempClassificationDisplay => Classification switch
    {
        "hypothermia" => "Hypothermia",
        "normal" => "Normal",
        "slightly_elevated" => "Slightly Elevated",
        "fever" => "Fever",
        "high_fever" => "High Fever",
        _ => "Unknown"
    };

    public string TempClassificationColor => Classification switch
    {
        "hypothermia" => "#1976d2",
        "normal" => "#388e3c",
        "slightly_elevated" => "#f57c00",
        "fever" => "#d32f2f",
        "high_fever" => "#7b1fa2",
        _ => "#888888"
    };
}

// =====================================================
// Add these classes to VitalsAnalysis.cs
// Add these properties to the VitalsAnalysis class:
//
//   [JsonPropertyName("map")]
//   public MapAnalysis? Map { get; set; }
//
//   [JsonPropertyName("sbp_burden")]
//   public SbpBurden? SbpBurden { get; set; }
//
//   [JsonPropertyName("ttr")]
//   public TtrAnalysis? Ttr { get; set; }
//
//   [JsonPropertyName("dbp_burden")]
//   public DbpBurden? DbpBurden { get; set; }
// =====================================================

public class MapAnalysis
{
    [JsonPropertyName("avg")]
    public double Avg { get; set; }

    // Normal MAP range is 70–100 mmHg
    public string Display => $"{Avg:F1} mmHg";

    public string RangeLabel => Avg switch
    {
        < 70 => "⚠️ Low (risk of hypoperfusion)",
        <= 100 => "✅ Normal",
        _ => "🟡 Elevated"
    };

    public string RangeColor => Avg switch
    {
        < 70 => "#e65100",
        <= 100 => "#388e3c",
        _ => "#f57c00"
    };

    public string PlainEnglish =>
        $"The average pressure her arteries experience throughout the full " +
        $"heartbeat cycle is {Avg:F1} mmHg — normal range is 70–100 mmHg.";
}

public class SbpBurdenAnalysis
{
    [JsonPropertyName("pct")]
    public double Pct { get; set; }

    [JsonPropertyName("auc_above_130")]
    public double AucAbove130 { get; set; }

    [JsonPropertyName("total_sys_auc")]
    public double TotalSysAuc { get; set; }

    [JsonPropertyName("time_above_pct")]
    public double TimeAbovePct { get; set; }

    [JsonPropertyName("prop_above")]
    public double PropAbove { get; set; }

    public string PctDisplay => $"{Pct:F1}%";
    public string TimeDisplay => $"{TimeAbovePct:F1}%";

    // Caregiver plain English
    public string PlainEnglish =>
        Pct < 5
            ? $"Blood pressure exceeded the healthy upper limit of 130 mmHg only {Pct:F1}% of the time — well controlled."
            : Pct < 20
                ? $"About {Pct:F1}% of readings exceeded the healthy upper limit of 130 mmHg."
                : $"Blood pressure exceeded the healthy upper limit of 130 mmHg {Pct:F1}% of the time — this warrants attention.";

    // Clinician PDF label
    public string PdfLabel =>
        $"SBP Burden: {Pct:F1}%  " +
        $"(AUC above 130: {AucAbove130:F1} mmHg·day  |  " +
        $"Time above 130: {TimeAbovePct:F1}%  |  " +
        $"Weighted excess proportion: {PropAbove:F1}%)";
}

public class TtrBurdenAnalysis
{
    [JsonPropertyName("pct")]
    public double Pct { get; set; }

    [JsonPropertyName("time_in_days")]
    public double TimeInDays { get; set; }

    [JsonPropertyName("total_days")]
    public double TotalDays { get; set; }

    public string PctDisplay => $"{Pct:F1}%";

    // Caregiver plain English
    public string PlainEnglish =>
        Pct >= 70
            ? $"Blood pressure was within the healthy target range (100–130 mmHg) {Pct:F1}% of the time — great consistency."
            : Pct >= 50
                ? $"Blood pressure was within the healthy target range (100–130 mmHg) {Pct:F1}% of the time."
                : $"Blood pressure was within the healthy target range (100–130 mmHg) only {Pct:F1}% of the time — consistent daily monitoring will help clarify this pattern.";

    // Clinician PDF label
    public string PdfLabel =>
        $"SBP TTR: {Pct:F1}%  " +
        $"(Target 100–130 mmHg  |  " +
        $"{TimeInDays:F1} of {TotalDays:F1} days in range  |  " +
        $"Rosendaal linear interpolation approximation)";
}

public class DbpBurdenAnalysis
{
    [JsonPropertyName("pct")]
    public double Pct { get; set; }

    [JsonPropertyName("auc_above_80")]
    public double AucAbove80 { get; set; }

    [JsonPropertyName("annualized_mmhg_year")]
    public double AnnualizedMmhgYear { get; set; }

    [JsonPropertyName("time_above_pct")]
    public double TimeAbovePct { get; set; }

    [JsonPropertyName("total_dia_auc")]
    public double TotalDiaAuc { get; set; }

    public string PctDisplay => $"{Pct:F1}%";
    public string AnnualizedDisplay => $"{AnnualizedMmhgYear:F2} mmHg·year";
    public string TimeDisplay => $"{TimeAbovePct:F1}%";

    // Caregiver plain English — uses "heart at rest" framing
    public string PlainEnglish =>
        Pct < 10
            ? $"Even while the heart was at rest between beats, resting pressure exceeded 80 mmHg only {Pct:F1}% of the time — reassuring."
            : Pct < 25
                ? $"About {Pct:F1}% of the time, even while the heart was at rest between beats, the pressure in the arteries remained above the healthy threshold of 80 mmHg."
                : $"Resting pressure between heartbeats exceeded 80 mmHg {Pct:F1}% of the time. " +
                  $"This means the cardiovascular system rarely had a chance to fully rest at a safe pressure level.";

    // Clinician PDF label (Cho et al. methodology)
    public string PdfLabel =>
        $"Cumulative DBP Burden: {AnnualizedMmhgYear:F3} mmHg·year  " +
        $"(AUC above 80 mmHg re-zeroed at threshold, annualized  |  " +
        $"Proportional: {Pct:F1}% of total DBP AUC  |  " +
        $"Time above 80 mmHg: {TimeAbovePct:F1}%  |  " +
        $"Cho et al. Hypertension 2024;81:273–281)";
}

public class LowDbpBurdenAnalysis
{
    [JsonPropertyName("normal_pct")]
    public double NormalPct { get; set; }

    [JsonPropertyName("low_pct")]
    public double LowPct { get; set; }

    [JsonPropertyName("severe_pct")]
    public double SeverePct { get; set; }

    [JsonPropertyName("critical_pct")]
    public double CriticalPct { get; set; }

    [JsonPropertyName("auc_below_60")]
    public double AucBelow60 { get; set; }

    [JsonPropertyName("annualized_mmhg_year")]
    public double AnnualizedMmhgYear { get; set; }

    [JsonPropertyName("burden_pct")]
    public double BurdenPct { get; set; }

    [JsonPropertyName("lowest_dia")]
    public double LowestDia { get; set; }

    [JsonPropertyName("has_critical")]
    public bool HasCritical { get; set; }

    [JsonPropertyName("critical_readings")]
    public List<double> CriticalReadings { get; set; } = new();

    [JsonPropertyName("time_below_60_pct")]
    public double TimeBelow60Pct { get; set; }

    // ---- Display properties ----

    public string NormalDisplay => $"{NormalPct:F1}%";
    public string LowDisplay => $"{LowPct:F1}%";
    public string SevereDisplay => $"{SeverePct:F1}%";
    public string CriticalDisplay => $"{CriticalPct:F1}%";
    public string BurdenDisplay => $"{BurdenPct:F1}%";
    public string LowestDiaDisplay => $"{LowestDia:F0} mmHg";

    public string PlainEnglish
    {
        get
        {
            if (HasCritical)
            {
                var readings = string.Join(", ", CriticalReadings.Select(r => $"{r:F0}"));
                return $"One or more readings fell critically low ({readings} mmHg) — " +
                       $"severely low diastolic pressure may impair coronary perfusion. " +
                       $"Consider contacting the care team.";
            }
            if (SeverePct >= 15)
                return $"Diastolic pressure was below 60 mmHg {SeverePct:F1}% of the time — " +
                       $"a persistent pattern of diastolic hypotension worth monitoring.";
            if (SeverePct > 0)
                return $"Diastolic pressure occasionally fell below 60 mmHg ({SeverePct:F1}% of readings) — " +
                       $"monitor for symptoms of low diastolic pressure.";
            return $"Diastolic pressure remained above 60 mmHg throughout — reassuring.";
        }
    }
}