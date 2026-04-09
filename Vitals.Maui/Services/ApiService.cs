using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vitals.Maui.Models;

namespace Vitals.Maui.Services;

public class ApiService
{
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _jsonOptions;

    public ApiService(HttpClient http)
    {
        _http = http;
        _http.DefaultRequestHeaders.Add("X-API-KEY", AppConfig.ApiKey);

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
    }

    public async Task<List<Patient>> GetPatientsAsync()
    {
        try
        {
            var response = await _http.GetAsync("/api/patients");
            var raw = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine($"=== PATIENTS STATUS: {response.StatusCode}");
            System.Diagnostics.Debug.WriteLine($"=== PATIENTS BODY: {raw}");

            var result = JsonSerializer.Deserialize<List<Patient>>(raw, _jsonOptions);
            System.Diagnostics.Debug.WriteLine($"=== PATIENTS COUNT: {result?.Count}");
            foreach (var p in result ?? new List<Patient>())
                System.Diagnostics.Debug.WriteLine($"=== PATIENT: '{p.PatientId}' '{p.FirstName}' '{p.LastName}'");
            return result ?? new List<Patient>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== PATIENTS ERROR: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"=== PATIENTS STACK: {ex.StackTrace}");
            throw;
        }
    }

    public async Task<bool> RecordVitalsAsync(VitalEntry vital)
    {
        var json = JsonSerializer.Serialize(vital, _jsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync("/api/vitals", content);
        return response.IsSuccessStatusCode;
    }
}