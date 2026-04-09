using System.Text.Json.Serialization;

namespace Vitals.Maui.Models;

public class VitalEntry
{
    [JsonPropertyName("patient_id")]
    public string PatientId { get; set; } = string.Empty;

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

    [JsonPropertyName("blood_glucose")]
    public int? BloodGlucose { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = "maui_app";

    [JsonPropertyName("notes")]
    public string Notes { get; set; } = string.Empty;
}