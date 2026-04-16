using System.Text.Json.Serialization;

namespace Vitals.Maui.Models;

public class LatestVitals
{
    [JsonPropertyName("recorded_at")]
    public DateTime? RecordedAt { get; set; }

    [JsonPropertyName("systolic")]
    public int? Systolic { get; set; }

    [JsonPropertyName("diastolic")]
    public int? Diastolic { get; set; }

    [JsonPropertyName("oxygen_saturation")]
    public int? OxygenSaturation { get; set; }

    [JsonPropertyName("heart_rate")]
    public int? HeartRate { get; set; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("weight")]
    public double? Weight { get; set; }

    [JsonPropertyName("blood_glucose")]
    public double? BloodGlucose { get; set; }
}

public class VitalsAverages
{
    [JsonPropertyName("days")]
    public int Days { get; set; }

    [JsonPropertyName("systolic")]
    public double? Systolic { get; set; }

    [JsonPropertyName("diastolic")]
    public double? Diastolic { get; set; }

    [JsonPropertyName("heart_rate")]
    public double? HeartRate { get; set; }

    [JsonPropertyName("oxygen_saturation")]
    public double? OxygenSaturation { get; set; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("weight")]
    public double? Weight { get; set; }

    [JsonPropertyName("blood_glucose")]
    public double? BloodGlucose { get; set; }
}