using System.Text.Json.Serialization;

namespace Vitals.Maui.Models;

public class PatientHeightRecord
{
    [JsonPropertyName("height_id")]
    public string HeightId { get; set; } = string.Empty;

    [JsonPropertyName("patient_id")]
    public string PatientId { get; set; } = string.Empty;

    [JsonPropertyName("height_inches")]
    public int HeightInches { get; set; }

    [JsonPropertyName("effective_date")]
    public DateTime EffectiveDate { get; set; }

    [JsonPropertyName("entry_type")]
    public string EntryType { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("supersedes_height_id")]
    public string? SupersedesHeightId { get; set; }

    [JsonPropertyName("is_active")]
    public bool IsActive { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    public string HeightDisplay =>
        $"{HeightInches / 12}' {HeightInches % 12}\"";

    public string EffectiveDateDisplay =>
        EffectiveDate.ToString("MMM d, yyyy");

    public string EntryTypeDisplay =>
        EntryType switch
        {
            "correction" => "Correction",
            "profile_backfill" => "Profile snapshot",
            _ => "Measurement"
        };

    public string StatusDisplay =>
        IsActive ? EntryTypeDisplay : $"{EntryTypeDisplay} · superseded";
}

public class PatientHeightSaveResponse
{
    [JsonPropertyName("record")]
    public PatientHeightRecord? Record { get; set; }

    [JsonPropertyName("current_height_inches")]
    public int? CurrentHeightInches { get; set; }
}
