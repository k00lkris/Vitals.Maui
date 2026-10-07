using System.Text.Json.Serialization;

namespace Vitals.Maui.Models;

public class Patient
{
    [JsonPropertyName("patient_id")]
    public string PatientId { get; set; } = string.Empty;

    [JsonPropertyName("first_name")]
    public string FirstName { get; set; } = string.Empty;

    [JsonPropertyName("last_name")]
    public string LastName { get; set; } = string.Empty;

    [JsonPropertyName("dob")]
    public string? Dob { get; set; }

    [JsonPropertyName("gender")]
    public string? Gender { get; set; }

    // Stored canonically in the database/API as total inches. The app keeps
    // feet/inches as presentation-only values so calculations never have to
    // reconcile two persisted height fields.
    [JsonPropertyName("height_inches")]
    public int? HeightInches { get; set; }

    [JsonPropertyName("is_entitlement_locked")]
    public bool IsEntitlementLocked { get; set; }

    public int? HeightFeetDisplay => HeightInches is null ? null : HeightInches.Value / 12;
    public int? HeightRemainderInchesDisplay => HeightInches is null ? null : HeightInches.Value % 12;
    public string HeightDisplay =>
        HeightInches is null
            ? "Not set"
            : $"{HeightFeetDisplay}' {HeightRemainderInchesDisplay}\"";

    public string FullName => $"{FirstName} {LastName}";

    public override string ToString() => FullName;
}