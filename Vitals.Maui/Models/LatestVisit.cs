using System.Text.Json.Serialization;

namespace Vitals.Maui.Models;

public class LatestVisit
{
    [JsonPropertyName("visit_date")]
    public string? VisitDate { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("follow_up_date")]
    public string? FollowUpDate { get; set; }

    public string DisplayVisitDate => string.IsNullOrEmpty(VisitDate)
        ? "No visits recorded"
        : DateTime.Parse(VisitDate).ToLocalTime().ToString("MMM d, yyyy");

    public string DisplayFollowUp => string.IsNullOrEmpty(FollowUpDate)
        ? "None scheduled"
        : DateTime.Parse(FollowUpDate).ToString("MMM d, yyyy");

    public bool HasFollowUp => !string.IsNullOrEmpty(FollowUpDate);

    public bool IsFollowUpSoon => HasFollowUp &&
        DateTime.Parse(FollowUpDate!).Date <= DateTime.Today.AddDays(7);

    public string FollowUpColor => IsFollowUpSoon ? "#f57c00" : "#90caf9";
}