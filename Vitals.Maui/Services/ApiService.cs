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
        // Remove: _http.DefaultRequestHeaders.Add("X-API-KEY", AppConfig.ApiKey);
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
    }

    public async Task<List<Patient>> GetPatientsAsync(bool excludeSelfClaimed = false)
    {
        try
        {
            var url = excludeSelfClaimed ? "/api/patients?exclude_self_claimed=true" : "/api/patients";
            var response = await _http.GetAsync(url);
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

    /// <summary>
    /// Creates a new patient under the caller's household (server derives
    /// household from the JWT — see get_household_id in main.py) and
    /// returns the created record, including its new patient_id, so the
    /// caller can select it immediately without a second round-trip.
    /// Returns null on failure.
    /// </summary>
    public async Task<Patient?> AddPatientAsync(object payload)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync("/api/patients", content);
            var raw = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine($"=== ADD PATIENT STATUS: {response.StatusCode}");
            System.Diagnostics.Debug.WriteLine($"=== ADD PATIENT RESPONSE: {raw}");

            if (!response.IsSuccessStatusCode) return null;
            return JsonSerializer.Deserialize<Patient>(raw, _jsonOptions);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== ADD PATIENT ERROR: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Updates mutable patient demographics. Height is sent as total inches;
    /// feet/inches are presentation-only in the mobile UI.
    /// </summary>
    public async Task<Patient?> UpdatePatientDemographicsAsync(
        string patientId,
        object payload)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _http.PatchAsync(
                $"/api/patients/{Uri.EscapeDataString(patientId)}",
                content);
            var raw = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine(
                $"=== UPDATE PATIENT DEMOGRAPHICS STATUS: {response.StatusCode} {raw}");

            if (!response.IsSuccessStatusCode)
                return null;

            return JsonSerializer.Deserialize<Patient>(raw, _jsonOptions);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"=== UPDATE PATIENT DEMOGRAPHICS ERROR: {ex.Message}");
            return null;
        }
    }

    public async Task<List<PatientHeightRecord>> GetPatientHeightHistoryAsync(
        string patientId)
    {
        try
        {
            var response = await _http.GetAsync(
                $"/api/patients/{Uri.EscapeDataString(patientId)}/height-history");
            var raw = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine(
                $"=== HEIGHT HISTORY STATUS: {response.StatusCode} {raw}");

            if (!response.IsSuccessStatusCode)
                return new List<PatientHeightRecord>();

            return JsonSerializer.Deserialize<List<PatientHeightRecord>>(
                       raw,
                       _jsonOptions)
                   ?? new List<PatientHeightRecord>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"=== HEIGHT HISTORY ERROR: {ex.Message}");
            return new List<PatientHeightRecord>();
        }
    }

    public async Task<PatientHeightSaveResponse?> RecordPatientHeightAsync(
        string patientId,
        int heightInches,
        DateTime? effectiveDate,
        string updateType)
    {
        try
        {
            var payload = new
            {
                height_inches = heightInches,
                effective_date = effectiveDate?.ToString("yyyy-MM-dd"),
                update_type = updateType,
            };

            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(
                $"/api/patients/{Uri.EscapeDataString(patientId)}/height",
                content);
            var raw = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine(
                $"=== RECORD HEIGHT STATUS: {response.StatusCode} {raw}");

            if (!response.IsSuccessStatusCode)
                return null;

            return JsonSerializer.Deserialize<PatientHeightSaveResponse>(
                raw,
                _jsonOptions);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"=== RECORD HEIGHT ERROR: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Links the caller to an existing patient as 'self' — called after
    /// the user confirms (via a DOB/gender verification prompt) that an
    /// existing patient in the Join flow's "attach to existing" list
    /// really is them. Returns a friendly error (e.g. already claimed by
    /// someone else) via the shared ExtractErrorDetail parser on failure.
    /// </summary>
    public async Task<InviteActionResult> ClaimPatientAsync(string patientId)
    {
        try
        {
            var response = await _http.PostAsync($"/api/patients/{patientId}/claim", null);
            var raw = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine($"=== CLAIM PATIENT STATUS: {response.StatusCode} {raw}");

            if (!response.IsSuccessStatusCode)
            {
                return InviteActionResult.Failed(ExtractErrorDetail(raw));
            }
            return InviteActionResult.Ok();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== CLAIM PATIENT ERROR: {ex.Message}");
            return InviteActionResult.Failed("Couldn't reach the server. Check your connection and try again.");
        }
    }

    public async Task<bool> RecordVitalsAsync(VitalEntry vital)
    {
        var json = JsonSerializer.Serialize(vital, _jsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync("/api/vitals", content);
        return response.IsSuccessStatusCode;
    }

    public async Task<VitalRecord?> GetVitalRecordAsync(string vitalId)
    {
        try
        {
            var response = await _http.GetAsync(
                $"/api/vitals/{Uri.EscapeDataString(vitalId)}");
            var raw = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine(
                $"=== VITAL RECORD STATUS: {response.StatusCode} {raw}");

            if (!response.IsSuccessStatusCode)
                return null;

            return JsonSerializer.Deserialize<VitalRecord>(raw, _jsonOptions);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"=== VITAL RECORD ERROR: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> UpdateVitalRecordAsync(
        string vitalId,
        VitalEntry vital)
    {
        try
        {
            var json = JsonSerializer.Serialize(vital, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _http.PatchAsync(
                $"/api/vitals/{Uri.EscapeDataString(vitalId)}",
                content);
            var raw = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine(
                $"=== UPDATE VITAL STATUS: {response.StatusCode} {raw}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"=== UPDATE VITAL ERROR: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> DeleteVitalRecordAsync(string vitalId)
    {
        try
        {
            var response = await _http.DeleteAsync(
                $"/api/vitals/{Uri.EscapeDataString(vitalId)}");
            var raw = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine(
                $"=== DELETE VITAL STATUS: {response.StatusCode} {raw}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"=== DELETE VITAL ERROR: {ex.Message}");
            return false;
        }
    }

    public async Task<LatestVitals?> GetLatestVitalsAsync(string patientId)
    {
        try
        {
            var response = await _http.GetAsync($"/api/vitals/latest?patient_id={patientId}");
            var raw = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine($"=== LATEST VITALS: {raw}");
            if (raw == "{}") return null;
            return JsonSerializer.Deserialize<LatestVitals>(raw, _jsonOptions);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== LATEST VITALS ERROR: {ex.Message}");
            return null;
        }
    }

    public async Task<VitalsAverages?> GetVitalsAveragesAsync(string patientId, int days)
    {
        try
        {
            var response = await _http.GetAsync(
                $"/api/vitals/averages?patient_id={patientId}&days={days}");
            var raw = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine($"=== AVERAGES: {raw}");
            if (raw == "{}") return null;
            return JsonSerializer.Deserialize<VitalsAverages>(raw, _jsonOptions);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== AVERAGES ERROR: {ex.Message}");
            return null;
        }
    }

    public async Task<List<Medication>> GetMedicationsAsync(string patientId)
    {
        try
        {
            var response = await _http.GetAsync(
                $"/api/medications?patient_id={patientId}");
            var raw = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine($"=== MEDICATIONS: {raw}");
            var result = JsonSerializer.Deserialize<MedicationsResponse>(raw, _jsonOptions);
            return result?.Medications ?? new List<Medication>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== MEDICATIONS ERROR: {ex.Message}");
            return new List<Medication>();
        }
    }

    public async Task<List<Doctor>> GetDoctorsAsync(string patientId)
    {
        try
        {
            var response = await _http.GetAsync(
                $"/api/doctors?patient_id={patientId}&active_only=true");
            var raw = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine($"=== DOCTORS: {raw}");
            var result = JsonSerializer.Deserialize<DoctorsResponse>(raw, _jsonOptions);
            return result?.Doctors ?? new List<Doctor>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== DOCTORS ERROR: {ex.Message}");
            return new List<Doctor>();
        }
    }

    public async Task<bool> UpdateMedicationAsync(string medicationId, object payload)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            System.Diagnostics.Debug.WriteLine($"=== UPDATE MED PAYLOAD: {json}");
            System.Diagnostics.Debug.WriteLine($"=== UPDATE MED URL: /api/medications/{medicationId}");
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var response = await _http.PatchAsync(
                $"/api/medications/{medicationId}", content);
            var responseBody = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine($"=== UPDATE MED STATUS: {response.StatusCode}");
            System.Diagnostics.Debug.WriteLine($"=== UPDATE MED RESPONSE: {responseBody}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== UPDATE MED ERROR: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> AddMedicationAsync(object payload)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var response = await _http.PostAsync("/api/medications", content);
            System.Diagnostics.Debug.WriteLine($"=== ADD MED STATUS: {response.StatusCode}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== ADD MED ERROR: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> UpdateDoctorAsync(string doctorId, object payload)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var response = await _http.PatchAsync($"/api/doctors/{doctorId}", content);
            System.Diagnostics.Debug.WriteLine($"=== UPDATE DOCTOR STATUS: {response.StatusCode}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== UPDATE DOCTOR ERROR: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> AddDoctorAsync(object payload)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var response = await _http.PostAsync("/api/doctors", content);
            System.Diagnostics.Debug.WriteLine($"=== ADD DOCTOR STATUS: {response.StatusCode}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== ADD DOCTOR ERROR: {ex.Message}");
            return false;
        }
    }

    public async Task<List<VitalHistoryRow>> GetVitalsHistoryAsync(string patientId, int days)
    {
        try
        {
            var response = await _http.GetAsync(
                $"/api/vitals/history?patient_id={patientId}&days={days}");
            var raw = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine($"=== HISTORY: {raw.Substring(0, Math.Min(200, raw.Length))}");
            var result = JsonSerializer.Deserialize<VitalHistoryResponse>(raw, _jsonOptions);
            return result?.Rows ?? new List<VitalHistoryRow>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== HISTORY ERROR: {ex.Message}");
            return new List<VitalHistoryRow>();
        }
    }

    public async Task<List<Allergy>> GetAllergiesAsync(string patientId)
    {
        try
        {
            var response = await _http.GetAsync(
                $"/api/allergies?patient_id={patientId}&active_only=true");
            var raw = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine($"=== ALLERGIES: {raw.Substring(0, Math.Min(200, raw.Length))}");
            var result = JsonSerializer.Deserialize<AllergiesResponse>(raw, _jsonOptions);
            return result?.Allergies ?? new List<Allergy>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== ALLERGIES ERROR: {ex.Message}");
            return new List<Allergy>();
        }
    }

    public async Task<bool> AddAllergyAsync(object payload)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var response = await _http.PostAsync("/api/allergies", content);
            System.Diagnostics.Debug.WriteLine($"=== ADD ALLERGY STATUS: {response.StatusCode}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== ADD ALLERGY ERROR: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> UpdateAllergyAsync(string allergyId, object payload)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var response = await _http.PatchAsync($"/api/allergies/{allergyId}", content);
            System.Diagnostics.Debug.WriteLine($"=== UPDATE ALLERGY STATUS: {response.StatusCode}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== UPDATE ALLERGY ERROR: {ex.Message}");
            return false;
        }
    }

    public async Task<byte[]?> GetPdfAsync(
        string patientId,
        int days,
        UserPreferences? preferences = null)
    {
        try
        {
            // No token=ha param anymore — that literal-string bypass was
            // removed server-side now that Home Assistant no longer touches
            // this endpoint. AuthHeaderHandler already attaches the real
            // X-API-KEY and JWT to every request through this HttpClient.
            //
            // Current builds also pass the exact optional-vital visibility
            // state being shown for this patient. That keeps clinician PDF
            // content in lockstep with Dashboard/Analysis even if a prior
            // preference write was delayed or failed.
            var url = $"/api/medications/{Uri.EscapeDataString(patientId)}/pdf?days={days}";
            if (preferences is not null)
            {
                url +=
                    $"&include_heart_rate={preferences.ShowHeartRate.ToString().ToLowerInvariant()}" +
                    $"&include_spo2={preferences.ShowSpo2.ToString().ToLowerInvariant()}" +
                    $"&include_temperature={preferences.ShowTemperature.ToString().ToLowerInvariant()}" +
                    $"&include_weight={preferences.ShowWeight.ToString().ToLowerInvariant()}" +
                    $"&include_glucose={preferences.ShowGlucose.ToString().ToLowerInvariant()}";
            }

            var response = await _http.GetAsync(url);
            System.Diagnostics.Debug.WriteLine($"=== PDF STATUS: {response.StatusCode}");
            if (!response.IsSuccessStatusCode)
            {
                var raw = await response.Content.ReadAsStringAsync();
                System.Diagnostics.Debug.WriteLine($"=== PDF ERROR RESPONSE: {raw}");
                return null;
            }
            return await response.Content.ReadAsByteArrayAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== PDF ERROR: {ex.Message}");
            return null;
        }
    }

    // =====================================================
    // HOUSEHOLD DOCTORS
    // =====================================================
    public async Task<List<Doctor>> GetHouseholdDoctorsAsync()
    {
        try
        {
            var response = await _http.GetAsync("/api/doctors/household");
            var raw = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<DoctorsResponse>(raw, _jsonOptions);
            return result?.Doctors ?? new List<Doctor>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== HOUSEHOLD DOCTORS ERROR: {ex.Message}");
            return new List<Doctor>();
        }
    }

    public async Task<bool> LinkDoctorToPatientAsync(string patientId, string doctorId, bool isPrimary = false)
    {
        try
        {
            var payload = new { patient_id = patientId, doctor_id = doctorId, is_primary = isPrimary };
            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var response = await _http.PostAsync("/api/patient_doctors", content);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== LINK DOCTOR ERROR: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> UnlinkDoctorFromPatientAsync(string patientId, string doctorId)
    {
        try
        {
            var response = await _http.DeleteAsync(
                $"/api/patient_doctors?patient_id={patientId}&doctor_id={doctorId}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== UNLINK DOCTOR ERROR: {ex.Message}");
            return false;
        }
    }

    // =====================================================
    // VISIT LOG
    // =====================================================
    public async Task<List<VisitLog>> GetVisitsAsync(string patientId)
    {
        try
        {
            var response = await _http.GetAsync($"/api/visits?patient_id={patientId}");
            var raw = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<VisitLogResponse>(raw, _jsonOptions);
            return result?.Visits ?? new List<VisitLog>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== VISITS ERROR: {ex.Message}");
            return new List<VisitLog>();
        }
    }

    public async Task<bool> CreateVisitAsync(object payload)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var response = await _http.PostAsync("/api/visits", content);
            System.Diagnostics.Debug.WriteLine($"=== CREATE VISIT STATUS: {response.StatusCode}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== CREATE VISIT ERROR: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> UpdateVisitAsync(string visitId, object payload)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var response = await _http.PatchAsync($"/api/visits/{visitId}", content);
            System.Diagnostics.Debug.WriteLine($"=== UPDATE VISIT STATUS: {response.StatusCode}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== UPDATE VISIT ERROR: {ex.Message}");
            return false;
        }
    }

    public async Task<LatestVisit?> GetLatestVisitAsync(string patientId, string doctorId)
    {
        try
        {
            var response = await _http.GetAsync(
                $"/api/visits/latest?patient_id={patientId}&doctor_id={doctorId}");
            var raw = await response.Content.ReadAsStringAsync();
            if (raw == "{}") return null;
            return JsonSerializer.Deserialize<LatestVisit>(raw, _jsonOptions);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== LATEST VISIT ERROR: {ex.Message}");
            return null;
        }
    }

    // =====================================================
    // INCIDENT LOG
    // =====================================================
    public async Task<List<IncidentLog>> GetIncidentsAsync(string patientId)
    {
        try
        {
            var response = await _http.GetAsync($"/api/incidents?patient_id={patientId}");
            var raw = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<IncidentLogResponse>(raw, _jsonOptions);
            return result?.Incidents ?? new List<IncidentLog>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== INCIDENTS ERROR: {ex.Message}");
            return new List<IncidentLog>();
        }
    }

    public async Task<bool> CreateIncidentAsync(object payload)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var response = await _http.PostAsync("/api/incidents", content);
            System.Diagnostics.Debug.WriteLine($"=== CREATE INCIDENT STATUS: {response.StatusCode}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== CREATE INCIDENT ERROR: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> UpdateIncidentAsync(string incidentId, object payload)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var response = await _http.PatchAsync($"/api/incidents/{incidentId}", content);
            System.Diagnostics.Debug.WriteLine($"=== UPDATE INCIDENT STATUS: {response.StatusCode}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== UPDATE INCIDENT ERROR: {ex.Message}");
            return false;
        }
    }

    // =====================================================
    // NOTES
    // =====================================================
    public async Task<List<PatientNote>> GetNotesAsync(string patientId)
    {
        try
        {
            var response = await _http.GetAsync($"/api/notes?patient_id={patientId}");
            var raw = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<PatientNoteResponse>(raw, _jsonOptions);
            return result?.Notes ?? new List<PatientNote>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== NOTES ERROR: {ex.Message}");
            return new List<PatientNote>();
        }
    }

    public async Task<bool> CreateNoteAsync(object payload)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var response = await _http.PostAsync("/api/notes", content);
            System.Diagnostics.Debug.WriteLine($"=== CREATE NOTE STATUS: {response.StatusCode}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== CREATE NOTE ERROR: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> UpdateNoteAsync(string noteId, object payload)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var response = await _http.PatchAsync($"/api/notes/{noteId}", content);
            System.Diagnostics.Debug.WriteLine($"=== UPDATE NOTE STATUS: {response.StatusCode}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== UPDATE NOTE ERROR: {ex.Message}");
            return false;
        }
    }

    public async Task<VitalsAnalysis?> GetVitalsAnalysisAsync(string patientId, int days)
    {
        try
        {
            var response = await _http.GetAsync(
                $"/api/vitals/analysis?patient_id={patientId}&days={days}");
            var raw = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine($"=== ANALYSIS: {raw.Substring(0, Math.Min(300, raw.Length))}");
            return JsonSerializer.Deserialize<VitalsAnalysis>(raw, _jsonOptions);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== ANALYSIS ERROR: {ex.Message}");
            return null;
        }
    }

    // =====================================================
    // USER PREFERENCES
    // =====================================================
    // =====================================================
    // HOUSEHOLD — TIER SELECTION / JOIN / INVITES
    // =====================================================

    /// <summary>
    /// Creates a household with Standard/Family plan intent, or "trial" when
    /// the user chooses to decide later. Every new household receives the
    /// same 30-day full-access trial with capacity for five patients.
    /// Returns the new token + household_id
    /// on success — caller is responsible for handing that to
    /// AuthService.UpdateSessionAsync(). Returns null on failure.
    /// </summary>
    public async Task<HouseholdSessionResult?> SelectTierAsync(string tier)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new { tier }, _jsonOptions);
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync("/api/household/select-tier", content);
            var raw = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine($"=== SELECT TIER STATUS: {response.StatusCode} {raw}");

            if (!response.IsSuccessStatusCode) return null;
            return JsonSerializer.Deserialize<HouseholdSessionResult>(raw, _jsonOptions);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== SELECT TIER ERROR: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Redeems an invite code, attaching the caller to the inviting
    /// household. Always succeeds for a valid code regardless of patient
    /// count — CanCreateNewPatient tells the caller whether to offer
    /// "create a new patient" on the next screen, or only "attach to an
    /// existing one." Returns null on an invalid/expired/used code.
    /// </summary>
    public async Task<JoinHouseholdResult?> JoinHouseholdAsync(string inviteCode)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new { invite_code = inviteCode }, _jsonOptions);
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync("/api/household/join", content);
            var raw = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine($"=== JOIN HOUSEHOLD STATUS: {response.StatusCode} {raw}");

            if (!response.IsSuccessStatusCode)
            {
                return new JoinHouseholdResult { Success = false, ErrorMessage = ExtractErrorDetail(raw) };
            }

            var result = JsonSerializer.Deserialize<JoinHouseholdResult>(raw, _jsonOptions);
            if (result is not null) result.Success = true;
            return result;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== JOIN HOUSEHOLD ERROR: {ex.Message}");
            return new JoinHouseholdResult { Success = false, ErrorMessage = "Couldn't reach the server. Check your connection and try again." };
        }
    }

    /// <summary>
    /// Sends a household invite to the given email. Returns a friendly
    /// error (e.g. "no available slots") on failure via the shared
    /// ExtractErrorDetail parser.
    /// </summary>
    public async Task<InviteActionResult> CreateHouseholdInviteAsync(string invitieeEmail)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new { invitee_email = invitieeEmail }, _jsonOptions);
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync("/api/household/invite", content);
            var raw = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine($"=== CREATE INVITE STATUS: {response.StatusCode} {raw}");

            if (!response.IsSuccessStatusCode)
            {
                return InviteActionResult.Failed(ExtractErrorDetail(raw));
            }
            return InviteActionResult.Ok();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== CREATE INVITE ERROR: {ex.Message}");
            return InviteActionResult.Failed("Couldn't reach the server. Check your connection and try again.");
        }
    }

    public async Task<List<PendingInvite>> GetPendingInvitesAsync()
    {
        try
        {
            var response = await _http.GetAsync("/api/household/invites");
            var raw = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine($"=== PENDING INVITES STATUS: {response.StatusCode} {raw}");

            if (!response.IsSuccessStatusCode) return new List<PendingInvite>();

            var wrapper = JsonSerializer.Deserialize<PendingInvitesResponse>(raw, _jsonOptions);
            return wrapper?.Invites ?? new List<PendingInvite>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== PENDING INVITES ERROR: {ex.Message}");
            return new List<PendingInvite>();
        }
    }

    public async Task<bool> CancelInviteAsync(string inviteId)
    {
        try
        {
            var response = await _http.DeleteAsync($"/api/household/invite/{inviteId}");
            System.Diagnostics.Debug.WriteLine($"=== CANCEL INVITE STATUS: {response.StatusCode}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== CANCEL INVITE ERROR: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Lets the Settings invite screen proactively disable "Invite" using
    /// the same slot math the server enforces, rather than only finding
    /// out after tapping it and getting a 403.
    /// </summary>
    public async Task<HouseholdStatus?> GetHouseholdStatusAsync()
    {
        try
        {
            var response = await _http.GetAsync("/api/household/status");
            var raw = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine($"=== HOUSEHOLD STATUS: {response.StatusCode} {raw}");

            if (!response.IsSuccessStatusCode) return null;
            return JsonSerializer.Deserialize<HouseholdStatus>(raw, _jsonOptions);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== HOUSEHOLD STATUS ERROR: {ex.Message}");
            return null;
        }
    }


    /// <summary>
    /// Returns the server-authoritative household commercial entitlement.
    /// 0.7.1b only exposes/caches this state; later Phase 7 components use
    /// it for feature gates and downgrade workflows.
    /// </summary>
    public async Task<HouseholdEntitlement?> GetHouseholdEntitlementAsync()
    {
        try
        {
            var response = await _http.GetAsync("/api/household/entitlement");
            var raw = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine(
                $"=== HOUSEHOLD ENTITLEMENT: {response.StatusCode} {raw}");

            if (!response.IsSuccessStatusCode) return null;
            return JsonSerializer.Deserialize<HouseholdEntitlement>(raw, _jsonOptions);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"=== HOUSEHOLD ENTITLEMENT ERROR: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Returns the provider-specific product identifiers configured by the
    /// backend for StoreKit / Google Play. Localized price and currency remain
    /// store-authoritative and are fetched by the platform billing client.
    /// </summary>
    public async Task<BillingCatalog?> GetBillingCatalogAsync(string provider)
    {
        try
        {
            var safeProvider = Uri.EscapeDataString(provider);
            var response = await _http.GetAsync($"/api/billing/catalog?provider={safeProvider}");
            var raw = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine(
                $"=== BILLING CATALOG: {response.StatusCode} {raw}");

            if (!response.IsSuccessStatusCode) return null;
            return JsonSerializer.Deserialize<BillingCatalog>(raw, _jsonOptions);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"=== BILLING CATALOG ERROR: {ex.Message}");
            return null;
        }
    }


    /// <summary>
    /// Sends an opaque native-store purchase reference to the Vitals API.
    /// The server, not the phone, resolves the product and entitlement.
    /// Apple uses transactionId; Google uses purchaseToken.
    /// </summary>
    public async Task<BillingVerificationResult?> VerifyBillingPurchaseAsync(
        string provider,
        string? transactionId = null,
        string? purchaseToken = null)
    {
        var payload = new
        {
            provider,
            transaction_id = transactionId,
            purchase_token = purchaseToken
        };

        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync("/api/billing/verify", content);
        var raw = await response.Content.ReadAsStringAsync();
        System.Diagnostics.Debug.WriteLine(
            $"=== BILLING VERIFY: {response.StatusCode} {raw}");

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ExtractErrorDetail(raw));

        return JsonSerializer.Deserialize<BillingVerificationResult>(
            raw,
            _jsonOptions);
    }


    /// <summary>
    /// Persists which patient profiles remain active when the household is
    /// above its effective plan capacity. Unselected profiles are preserved
    /// server-side and returned as entitlement-locked until capacity is
    /// restored.
    /// </summary>
    public async Task<HouseholdEntitlement?> SelectActivePatientsAsync(
        IEnumerable<string> patientIds)
    {
        try
        {
            var payload = new { patient_ids = patientIds.Distinct().ToArray() };
            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync("/api/household/patient-access", content);
            var raw = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine(
                $"=== PATIENT ACCESS SELECTION: {response.StatusCode} {raw}");

            if (!response.IsSuccessStatusCode) return null;

            using var document = JsonDocument.Parse(raw);
            if (!document.RootElement.TryGetProperty("entitlement", out var entitlement))
                return null;

            return JsonSerializer.Deserialize<HouseholdEntitlement>(
                entitlement.GetRawText(), _jsonOptions);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"=== PATIENT ACCESS SELECTION ERROR: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Same "{detail: ...}" parsing AuthService uses for its own errors —
    /// duplicated here (not shared) since ApiService and AuthService use
    /// separate HttpClients and neither currently depends on the other.
    /// </summary>
    private static string ExtractErrorDetail(string rawResponseBody)
    {
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(rawResponseBody);
            if (json.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
            {
                return detail.GetString() ?? "Something went wrong. Please try again.";
            }
        }
        catch { /* fall through */ }
        return "Something went wrong. Please try again.";
    }

    public async Task<UserPreferences?> GetPatientVitalPreferencesAsync(
        string patientId)
    {
        try
        {
            var response = await _http.GetAsync(
                $"/api/patients/{Uri.EscapeDataString(patientId)}/vital-preferences");
            var raw = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine(
                $"=== PATIENT VITAL PREFS STATUS: {response.StatusCode} {raw}");

            if (!response.IsSuccessStatusCode)
                return null;

            return JsonSerializer.Deserialize<UserPreferences>(raw, _jsonOptions);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"=== PATIENT VITAL PREFS ERROR: {ex.Message}");
            return null;
        }
    }

    public async Task<UserPreferences?> UpdatePatientVitalPreferencesAsync(
        string patientId,
        object payload)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _http.PatchAsync(
                $"/api/patients/{Uri.EscapeDataString(patientId)}/vital-preferences",
                content);
            var raw = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine(
                $"=== UPDATE PATIENT VITAL PREFS STATUS: {response.StatusCode} {raw}");

            if (!response.IsSuccessStatusCode)
                return null;

            return JsonSerializer.Deserialize<UserPreferences>(raw, _jsonOptions);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"=== UPDATE PATIENT VITAL PREFS ERROR: {ex.Message}");
            return null;
        }
    }

    public async Task<UserPreferences?> GetUserPreferencesAsync(
        string userId,
        string? patientId = null)
    {
        try
        {
            var url =
                $"/api/user/preferences?user_id={Uri.EscapeDataString(userId)}";

            if (!string.IsNullOrWhiteSpace(patientId))
                url += $"&patient_id={Uri.EscapeDataString(patientId)}";

            var response = await _http.GetAsync(url);
            var raw = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine(
                $"=== USER/PATIENT PREFS STATUS: {response.StatusCode}");

            if (!response.IsSuccessStatusCode)
                return null;

            return JsonSerializer.Deserialize<UserPreferences>(raw, _jsonOptions);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== USER/PATIENT PREFS ERROR: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> UpdateUserPreferencesAsync(
        string userId,
        object payload,
        string? patientId = null)
    {
        try
        {
            var url =
                $"/api/user/preferences?user_id={Uri.EscapeDataString(userId)}";

            if (!string.IsNullOrWhiteSpace(patientId))
                url += $"&patient_id={Uri.EscapeDataString(patientId)}";

            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _http.PatchAsync(url, content);
            System.Diagnostics.Debug.WriteLine(
                $"=== UPDATE USER/PATIENT PREFS STATUS: {response.StatusCode}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"=== UPDATE USER/PATIENT PREFS ERROR: {ex.Message}");
            return false;
        }
    }
}

public class HouseholdSessionResult
{
    public string Token { get; set; } = string.Empty;
    [JsonPropertyName("household_id")]
    public string HouseholdId { get; set; } = string.Empty;
    public string Tier { get; set; } = string.Empty;
    [JsonPropertyName("subscription_status")]
    public string SubscriptionStatus { get; set; } = string.Empty;
    [JsonPropertyName("trial_ends_at")]
    public DateTimeOffset? TrialEndsAt { get; set; }
    [JsonPropertyName("patient_limit")]
    public int PatientLimit { get; set; }
}

public class JoinHouseholdResult
{
    [JsonIgnore]
    public bool Success { get; set; }
    [JsonIgnore]
    public string? ErrorMessage { get; set; }

    public string Token { get; set; } = string.Empty;
    [JsonPropertyName("household_id")]
    public string HouseholdId { get; set; } = string.Empty;
    [JsonPropertyName("can_create_new_patient")]
    public bool CanCreateNewPatient { get; set; }
}

public class InviteActionResult
{
    public bool Success { get; private set; }
    public string? ErrorMessage { get; private set; }
    public static InviteActionResult Ok() => new() { Success = true };
    public static InviteActionResult Failed(string message) => new() { Success = false, ErrorMessage = message };
}

public class PendingInvite
{
    [JsonPropertyName("invite_id")]
    public string InviteId { get; set; } = string.Empty;
    [JsonPropertyName("invited_email")]
    public string InvitedEmail { get; set; } = string.Empty;
    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = string.Empty;
    [JsonPropertyName("expires_at")]
    public string ExpiresAt { get; set; } = string.Empty;
}

public class PendingInvitesResponse
{
    public List<PendingInvite> Invites { get; set; } = new();
}

public class HouseholdStatus
{
    [JsonPropertyName("patient_limit")]
    public int? PatientLimit { get; set; }
    [JsonPropertyName("patient_count")]
    public int PatientCount { get; set; }
    [JsonPropertyName("pending_invite_count")]
    public int PendingInviteCount { get; set; }
    [JsonPropertyName("available_slots")]
    public int? AvailableSlots { get; set; }
    [JsonPropertyName("can_invite")]
    public bool CanInvite { get; set; }
    [JsonPropertyName("can_manage_household")]
    public bool CanManageHousehold { get; set; }
    [JsonPropertyName("household_role")]
    public string HouseholdRole { get; set; } = "member";
    [JsonPropertyName("delegated_management_allowed")]
    public bool DelegatedManagementAllowed { get; set; }
    [JsonPropertyName("is_unlimited")]
    public bool IsUnlimited { get; set; }
    public string Plan { get; set; } = string.Empty;
    [JsonPropertyName("effective_plan")]
    public string EffectivePlan { get; set; } = string.Empty;
    [JsonPropertyName("access_state")]
    public string AccessState { get; set; } = string.Empty;
}

public class BillingCatalog
{
    public string Provider { get; set; } = string.Empty;
    public bool Configured { get; set; }

    [JsonPropertyName("account_binding_token")]
    public string AccountBindingToken { get; set; } = string.Empty;

    public List<BillingCatalogProduct> Products { get; set; } = new();
}

public class BillingCatalogProduct
{
    public string Plan { get; set; } = string.Empty;

    [JsonPropertyName("billing_period")]
    public string BillingPeriod { get; set; } = string.Empty;

    [JsonPropertyName("product_id")]
    public string? ProductId { get; set; }

    [JsonPropertyName("base_plan_id")]
    public string? BasePlanId { get; set; }

    public bool Configured { get; set; }
}

public class BillingVerificationResult
{
    public bool Verified { get; set; }

    [JsonPropertyName("entitlement_changed")]
    public bool EntitlementChanged { get; set; }

    public string Provider { get; set; } = string.Empty;
    public string Plan { get; set; } = string.Empty;

    [JsonPropertyName("billing_period")]
    public string BillingPeriod { get; set; } = string.Empty;

    [JsonPropertyName("product_id")]
    public string ProductId { get; set; } = string.Empty;

    [JsonPropertyName("base_plan_id")]
    public string? BasePlanId { get; set; }

    [JsonPropertyName("store_status")]
    public string StoreStatus { get; set; } = string.Empty;

    [JsonPropertyName("auto_renew_enabled")]
    public bool? AutoRenewEnabled { get; set; }

    [JsonPropertyName("expires_at")]
    public DateTimeOffset? ExpiresAt { get; set; }

    [JsonPropertyName("billing_subscription_id")]
    public string BillingSubscriptionId { get; set; } = string.Empty;

    public HouseholdEntitlement Entitlement { get; set; } = new();
}

public class HouseholdEntitlement
{
    public string Plan { get; set; } = string.Empty;

    [JsonPropertyName("effective_plan")]
    public string EffectivePlan { get; set; } = string.Empty;

    [JsonPropertyName("subscription_status")]
    public string SubscriptionStatus { get; set; } = string.Empty;

    [JsonPropertyName("access_state")]
    public string AccessState { get; set; } = string.Empty;

    [JsonPropertyName("has_premium_access")]
    public bool HasPremiumAccess { get; set; }

    [JsonPropertyName("is_founder")]
    public bool IsFounder { get; set; }

    [JsonPropertyName("is_beta")]
    public bool IsBeta { get; set; }

    [JsonPropertyName("is_unlimited")]
    public bool IsUnlimited { get; set; }

    [JsonPropertyName("patient_limit")]
    public int? PatientLimit { get; set; }

    [JsonPropertyName("patient_count")]
    public int PatientCount { get; set; }

    [JsonPropertyName("active_patient_count")]
    public int ActivePatientCount { get; set; }

    [JsonPropertyName("locked_patient_count")]
    public int LockedPatientCount { get; set; }

    [JsonPropertyName("trial_started_at")]
    public DateTimeOffset? TrialStartedAt { get; set; }

    [JsonPropertyName("trial_ends_at")]
    public DateTimeOffset? TrialEndsAt { get; set; }

    [JsonPropertyName("legacy_trial")]
    public bool LegacyTrial { get; set; }

    [JsonPropertyName("subscription_started_at")]
    public DateTimeOffset? SubscriptionStartedAt { get; set; }

    [JsonPropertyName("subscription_ends_at")]
    public DateTimeOffset? SubscriptionEndsAt { get; set; }

    [JsonPropertyName("grace_ends_at")]
    public DateTimeOffset? GraceEndsAt { get; set; }

    [JsonPropertyName("cancel_at_period_end")]
    public bool CancelAtPeriodEnd { get; set; }

    [JsonPropertyName("billing_provider")]
    public string? BillingProvider { get; set; }

    [JsonPropertyName("billing_product_id")]
    public string? BillingProductId { get; set; }

    [JsonPropertyName("household_role")]
    public string HouseholdRole { get; set; } = "member";

    [JsonPropertyName("delegated_management_allowed")]
    public bool DelegatedManagementAllowed { get; set; }

    [JsonPropertyName("is_household_owner")]
    public bool IsHouseholdOwner { get; set; }

    [JsonPropertyName("is_household_manager")]
    public bool IsHouseholdManager { get; set; }

    [JsonPropertyName("is_billing_owner")]
    public bool IsBillingOwner { get; set; }

    [JsonPropertyName("can_manage_household")]
    public bool CanManageHousehold { get; set; }

    [JsonPropertyName("can_start_purchase")]
    public bool CanStartPurchase { get; set; }

    [JsonPropertyName("can_manage_billing")]
    public bool CanManageBilling { get; set; }

    [JsonPropertyName("requires_basic_patient_selection")]
    public bool RequiresBasicPatientSelection { get; set; }
}
