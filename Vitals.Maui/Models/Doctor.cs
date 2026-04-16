using System.Text.Json.Serialization;

namespace Vitals.Maui.Models;

public class Doctor
{
    [JsonPropertyName("doctor_id")]
    public string DoctorId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("specialty")]
    public string? Specialty { get; set; }

    [JsonPropertyName("phone")]
    public string? Phone { get; set; }

    [JsonPropertyName("fax")]
    public string? Fax { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("address")]
    public string? Address { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("is_active")]
    public bool IsActive { get; set; } = true;

    [JsonPropertyName("is_primary")]
    public bool IsPrimary { get; set; }

    public override string ToString() => Name;
}

public class DoctorsResponse
{
    [JsonPropertyName("doctors")]
    public List<Doctor> Doctors { get; set; } = new();
}