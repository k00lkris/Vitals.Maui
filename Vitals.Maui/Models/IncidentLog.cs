using System.Text.Json.Serialization;

namespace Vitals.Maui.Models;

public class IncidentLog
{
    [JsonPropertyName("incident_id")]
    public string IncidentId { get; set; } = string.Empty;

    [JsonPropertyName("patient_id")]
    public string PatientId { get; set; } = string.Empty;

    [JsonPropertyName("incident_date")]
    public string? IncidentDate { get; set; }

    [JsonPropertyName("severity")]
    public string? Severity { get; set; }

    [JsonPropertyName("incident_type")]
    public string? IncidentType { get; set; }

    [JsonPropertyName("location")]
    public string? Location { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("outcome")]
    public string? Outcome { get; set; }

    [JsonPropertyName("follow_up_needed")]
    public bool FollowUpNeeded { get; set; }

    [JsonPropertyName("follow_up_notes")]
    public string? FollowUpNotes { get; set; }

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; set; }

    public string DisplayDate => string.IsNullOrEmpty(IncidentDate)
        ? "Unknown Date"
        : DateTime.Parse(IncidentDate).ToLocalTime().ToString("MMM d, yyyy h:mm tt");

    public string DisplaySeverity => Severity is null ? "—"
        : char.ToUpper(Severity[0]) + Severity[1..];

    public string SeverityColor => Severity switch
    {
        "low" => "#388e3c",
        "medium" => "#f57c00",
        "high" => "#d32f2f",
        "critical" => "#7b1fa2",
        _ => "#888888"
    };

    public string DisplayType => IncidentType ?? "General Incident";
}

public class IncidentLogResponse
{
    [JsonPropertyName("incidents")]
    public List<IncidentLog> Incidents { get; set; } = new();
}