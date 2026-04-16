using System.Text.Json.Serialization;

namespace Vitals.Maui.Models;

public class VitalHistoryRow
{
    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("systolic")]
    public int? Systolic { get; set; }

    [JsonPropertyName("diastolic")]
    public int? Diastolic { get; set; }

    [JsonPropertyName("spo2")]
    public int? Spo2 { get; set; }

    [JsonPropertyName("heart_rate")]
    public int? HeartRate { get; set; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("weight")]
    public double? Weight { get; set; }

    [JsonPropertyName("blood_glucose")]
    public int? BloodGlucose { get; set; }
}

public class VitalHistoryResponse
{
    [JsonPropertyName("rows")]
    public List<VitalHistoryRow> Rows { get; set; } = new();
}

public class VitalHistoryDisplay
{
    public string Date { get; set; } = string.Empty;
    public string Bp { get; set; } = "—";
    public string HeartRate { get; set; } = "—";
    public string Spo2 { get; set; } = "—";
    public string Temperature { get; set; } = "—";
    public string Weight { get; set; } = "—";
    public string Glucose { get; set; } = "—";

    public static VitalHistoryDisplay FromRow(VitalHistoryRow row)
    {
        var dt = DateTime.Parse(row.Date).ToLocalTime();
        return new VitalHistoryDisplay
        {
            Date = dt.ToString("MM/dd/yy\nh:mm tt"),
            Bp = row.Systolic.HasValue && row.Diastolic.HasValue
                ? $"{row.Systolic}/{row.Diastolic}"
                : "—",
            HeartRate = row.HeartRate.HasValue
                ? $"{row.HeartRate}"
                : "—",
            Spo2 = row.Spo2.HasValue
                ? $"{row.Spo2}%"
                : "—",
            Temperature = row.Temperature.HasValue
                ? $"{row.Temperature:F1}°"
                : "—",
            Weight = row.Weight.HasValue ? $"{row.Weight:F1}" : "—",
            Glucose = row.BloodGlucose.HasValue ? $"{row.BloodGlucose}" : "—"
        };
    }
}