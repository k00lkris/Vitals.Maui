using System.Text.Json.Serialization;

namespace Vitals.Maui.Models;

/// <summary>
/// Account-level display preferences returned by /api/user/preferences.
/// Blood pressure is intentionally not represented here because Vitals
/// currently treats it as always enabled.
/// </summary>
public class UserPreferences
{
    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "vitals_blue";

    [JsonPropertyName("show_heart_rate")]
    public bool ShowHeartRate { get; set; } = true;

    [JsonPropertyName("show_spo2")]
    public bool ShowSpo2 { get; set; } = true;

    [JsonPropertyName("show_temperature")]
    public bool ShowTemperature { get; set; } = true;

    [JsonPropertyName("show_weight")]
    public bool ShowWeight { get; set; }

    [JsonPropertyName("show_glucose")]
    public bool ShowGlucose { get; set; }
}
