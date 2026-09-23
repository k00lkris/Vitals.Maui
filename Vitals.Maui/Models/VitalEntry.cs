using System.Text.Json.Serialization;

namespace Vitals.Maui.Models;

public class VitalEntry
{
    [JsonPropertyName("patient_id")]
    public string PatientId { get; set; } = string.Empty;

    [JsonPropertyName("recorded_at")]
    public DateTime? RecordedAt { get; set; }

    // The device's UTC offset (minutes) at the moment this reading is
    // submitted — not heart-rate-specific, benefits every vital's
    // analysis that needs real local-time accuracy (distinct-day
    // counting, time-of-day buckets), not just heart rate's. Set by
    // VitalsEntryViewModel at submission time from the device's own
    // clock, not user-entered.
    [JsonPropertyName("local_offset_minutes")]
    public int? LocalOffsetMinutes { get; set; }

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
    [JsonPropertyName("weight")]
    public double? Weight { get; set; }

    // Heart Rate Analysis Spec §4 — only the two fields that actually
    // gate the "eligible resting set" almost every P0 heart-rate analysis
    // depends on. symptom_tags and device_irregular_pulse_flag stay out
    // of the UI until the analysis sets that actually consume them
    // (§6.9 Symptom Association) are built — no point collecting data
    // nothing reads yet. source_type isn't user-facing here either: every
    // reading through this screen is inherently manual entry, so the
    // ViewModel sets it directly rather than asking the user to confirm
    // something that's always the same answer on this form.
    [JsonPropertyName("hr_activity_context")]
    public string? HrActivityContext { get; set; }

    [JsonPropertyName("hr_posture")]
    public string? HrPosture { get; set; }

    [JsonPropertyName("hr_source_type")]
    public string? HrSourceType { get; set; }
}