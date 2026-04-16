using System.Text.Json.Serialization;

namespace Vitals.Maui.Models;

public class Allergy
{
    [JsonPropertyName("allergy_id")]
    public string AllergyId { get; set; } = string.Empty;

    [JsonPropertyName("patient_id")]
    public string PatientId { get; set; } = string.Empty;

    [JsonPropertyName("allergen")]
    public string Allergen { get; set; } = string.Empty;

    [JsonPropertyName("allergy_type")]
    public string AllergyType { get; set; } = string.Empty;

    [JsonPropertyName("reaction")]
    public string? Reaction { get; set; }

    [JsonPropertyName("severity")]
    public string? Severity { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("is_active")]
    public bool IsActive { get; set; } = true;

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; set; }

    public string SeverityDisplay => Severity is null ? "—" :
        char.ToUpper(Severity[0]) + Severity[1..];

    public string TypeDisplay => AllergyType is null ? "—" :
        char.ToUpper(AllergyType[0]) + AllergyType[1..];

    public string SeverityColor => Severity switch
    {
        "mild" => "#388e3c",
        "moderate" => "#f57c00",
        "severe" => "#d32f2f",
        _ => "#888888"
    };
}

public class AllergiesResponse
{
    [JsonPropertyName("allergies")]
    public List<Allergy> Allergies { get; set; } = new();
}