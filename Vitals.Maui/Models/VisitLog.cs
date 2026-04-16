using System.Text.Json.Serialization;

namespace Vitals.Maui.Models;

public class VisitLog
{
    [JsonPropertyName("visit_id")]
    public string VisitId { get; set; } = string.Empty;

    [JsonPropertyName("patient_id")]
    public string PatientId { get; set; } = string.Empty;

    [JsonPropertyName("doctor_id")]
    public string? DoctorId { get; set; }

    [JsonPropertyName("doctor_name")]
    public string? DoctorName { get; set; }

    [JsonPropertyName("visit_date")]
    public string? VisitDate { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("follow_up_date")]
    public string? FollowUpDate { get; set; }

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; set; }

    public string DisplayDate => string.IsNullOrEmpty(VisitDate)
        ? "Unknown Date"
        : DateTime.Parse(VisitDate).ToLocalTime().ToString("MMM d, yyyy h:mm tt");

    public string DisplayDoctor => DoctorName ?? "No Doctor Selected";

    public string DisplayFollowUp => string.IsNullOrEmpty(FollowUpDate)
        ? "None"
        : DateTime.Parse(FollowUpDate).ToString("MMM d, yyyy");
}

public class VisitLogResponse
{
    [JsonPropertyName("visits")]
    public List<VisitLog> Visits { get; set; } = new();
}