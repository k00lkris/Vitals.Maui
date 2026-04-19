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

    public bool IsHypotension =>
        Classification == "hypotension" || Classification == "borderline_hypotension";
    public bool IsInsufficient => Status == "insufficient_data";
    public bool IsOk => Status == "ok";

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