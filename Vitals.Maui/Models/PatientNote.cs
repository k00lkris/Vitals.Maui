using System.Text.Json.Serialization;

namespace Vitals.Maui.Models;

public class PatientNote
{
    [JsonPropertyName("note_id")]
    public string NoteId { get; set; } = string.Empty;

    [JsonPropertyName("patient_id")]
    public string PatientId { get; set; } = string.Empty;

    [JsonPropertyName("note_type")]
    public string NoteType { get; set; } = "general";

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public string? UpdatedAt { get; set; }

    public string DisplayDate => string.IsNullOrEmpty(CreatedAt)
        ? "Unknown Date"
        : DateTime.Parse(CreatedAt).ToLocalTime().ToString("MMM d, yyyy h:mm tt");

    public string DisplayType => NoteType switch
    {
        "general" => "General",
        "medication_change" => "Medication Change",
        "behavioral_observation" => "Behavioral Observation",
        "caregiver_handoff" => "Caregiver Handoff",
        "family_communication" => "Family Communication",
        _ => "General"
    };

    public string DisplayTitle => string.IsNullOrEmpty(Title)
        ? DisplayType
        : Title;

    public string TypeColor => NoteType switch
    {
        "general" => "#1976d2",
        "medication_change" => "#f57c00",
        "behavioral_observation" => "#7b1fa2",
        "caregiver_handoff" => "#388e3c",
        "family_communication" => "#d32f2f",
        _ => "#1976d2"
    };
}

public class PatientNoteResponse
{
    [JsonPropertyName("notes")]
    public List<PatientNote> Notes { get; set; } = new();
}