using System.Text.Json.Serialization;

namespace Vitals.Maui.Models;

public class Medication
{
    [JsonPropertyName("medication_id")]
    public string MedicationId { get; set; } = string.Empty;

    [JsonPropertyName("patient_id")]
    public string PatientId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("dosage")]
    public string? Dosage { get; set; }

    [JsonPropertyName("time_of_day")]
    public List<string> TimeOfDay { get; set; } = new();

    [JsonPropertyName("purpose")]
    public string? Purpose { get; set; }

    [JsonPropertyName("prescribing_doctor")]
    public string? PrescribingDoctor { get; set; }

    [JsonPropertyName("rxotc")]
    public string? RxOtc { get; set; }

    [JsonPropertyName("qty")]
    public int? Qty { get; set; }

    [JsonPropertyName("days_supply")]
    public int? DaysSupply { get; set; }

    [JsonPropertyName("fill_date")]
    public string? FillDate { get; set; }

    [JsonPropertyName("est_refill")]
    public string? EstRefill { get; set; }

    [JsonPropertyName("is_active")]
    public bool IsActive { get; set; }

    // discontinued removed — retired backend-side (is_active is the
    // single source of truth now; discontinued was never read or
    // written by this app in practice, only carried through unused).

    [JsonPropertyName("start_date")]
    public string? StartDate { get; set; }

    [JsonPropertyName("discontinued_date")]
    public string? DiscontinuedDate { get; set; }
}

public class MedicationsResponse
{
    [JsonPropertyName("medications")]
    public List<Medication> Medications { get; set; } = new();
}