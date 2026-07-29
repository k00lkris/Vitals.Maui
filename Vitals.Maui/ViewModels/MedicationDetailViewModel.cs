using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Xml.Linq;
using Vitals.Maui.Models;
using Vitals.Maui.Services;

namespace Vitals.Maui.ViewModels;

public partial class MedicationDetailViewModel : ObservableObject
{
    private readonly ApiService _api;
    private Medication? _original;

    // Mode
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private bool _isAddMode;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;

    // Fields
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _dosage = string.Empty;
    [ObservableProperty] private string _purpose = string.Empty;
    [ObservableProperty] private string _notes = string.Empty;
    [ObservableProperty] private bool _isActive = true;

    // Time of day toggles
    [ObservableProperty] private bool _morning;
    [ObservableProperty] private bool _midday;
    [ObservableProperty] private bool _evening;
    [ObservableProperty] private bool _night;

    // Toggle colors
    [ObservableProperty] private Color _morningColor = Color.FromArgb("#0f3460");
    [ObservableProperty] private Color _middayColor = Color.FromArgb("#0f3460");
    [ObservableProperty] private Color _eveningColor = Color.FromArgb("#0f3460");
    [ObservableProperty] private Color _nightColor = Color.FromArgb("#0f3460");

    // Rx/OTC
    [ObservableProperty] private bool _isRx = true;
    [ObservableProperty] private Color _rxColor = Color.FromArgb("#1976d2");
    [ObservableProperty] private Color _otcColor = Color.FromArgb("#0f3460");

    // Refill info
    [ObservableProperty] private string _qty = string.Empty;
    [ObservableProperty] private string _daysSupply = string.Empty;
    [ObservableProperty] private string _fillDate = string.Empty;
    [ObservableProperty] private string _estRefill = string.Empty;

    // Doctors
    [ObservableProperty] private ObservableCollection<Doctor> _doctors = new();
    [ObservableProperty] private Doctor? _selectedDoctor;

    // NDC Lookup
    [ObservableProperty] private string _ndcCode = string.Empty;
    [ObservableProperty] private string _ndcStatusMessage = string.Empty;

    // Patient context
    public string PatientId { get; set; } = string.Empty;

    // Callback to refresh list after save
    public Action? OnSaved { get; set; }
    public Action? OnCancelled { get; set; }

    public MedicationDetailViewModel(ApiService api)
    {
        _api = api;
    }

    public async Task InitializeAsync(Medication? medication, string patientId)
    {
        PatientId = patientId;
        IsAddMode = medication is null;
        IsEditing = IsAddMode;

        // Load doctors
        var doctorList = await _api.GetDoctorsAsync(patientId);
        Doctors = new ObservableCollection<Doctor>(doctorList);

        if (medication is not null)
        {
            _original = medication;
            LoadFromMedication(medication);
        }
    }

    private void LoadFromMedication(Medication med)
    {
        Name = med.Name;
        Dosage = med.Dosage ?? string.Empty;
        Purpose = med.Purpose ?? string.Empty;
        Notes = string.Empty;
        IsActive = med.IsActive;
        Qty = med.Qty?.ToString() ?? string.Empty;
        DaysSupply = med.DaysSupply?.ToString() ?? string.Empty;
        FillDate = med.FillDate ?? string.Empty;
        EstRefill = med.EstRefill ?? string.Empty;
        IsRx = (med.RxOtc ?? "rx") == "rx";

        Morning = med.TimeOfDay.Contains("morning");
        Midday = med.TimeOfDay.Contains("midday");
        Evening = med.TimeOfDay.Contains("evening");
        Night = med.TimeOfDay.Contains("night");

        UpdateTimeOfDayColors();
        UpdateRxOtcColors();

        SelectedDoctor = Doctors.FirstOrDefault(d =>
            d.Name == med.PrescribingDoctor);


    }

    private void UpdateRxOtcColors()
    {
        var active = Color.FromArgb("#1976d2");
        var inactive = Color.FromArgb("#0f3460");
        RxColor = IsRx ? active : inactive;
        OtcColor = IsRx ? inactive : active;
    }


    [RelayCommand]
    public void StartEdit()
    {
        IsEditing = true;
    }

    [RelayCommand]
    public void Cancel()
    {
        if (_original is not null)
            LoadFromMedication(_original);
        IsEditing = IsAddMode;
        OnCancelled?.Invoke();
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            StatusMessage = "Medication name is required.";
            return;
        }

        IsBusy = true;
        StatusMessage = string.Empty;

        try
        {
            var timeOfDay = new List<string>();
            if (Morning) timeOfDay.Add("morning");
            if (Midday) timeOfDay.Add("midday");
            if (Evening) timeOfDay.Add("evening");
            if (Night) timeOfDay.Add("night");

            bool success;

            if (IsAddMode)
            {
                var payload = new
                {
                    patient_id = PatientId,
                    name = Name,
                    dosage = Dosage,
                    purpose = Purpose,
                    time_of_day = timeOfDay,
                    prescribing_doctor_id = SelectedDoctor?.DoctorId,
                    qty = string.IsNullOrEmpty(Qty) ? (int?)null : int.Parse(Qty),
                    days_supply = string.IsNullOrEmpty(DaysSupply) ? (int?)null : int.Parse(DaysSupply),
                    is_active = IsActive,
                    rxotc = IsRx ? "rx" : "otc"
                };
                success = await _api.AddMedicationAsync(payload);
            }
            else
            {
                var payload = new
                {
                    name = Name,
                    dosage = Dosage,
                    purpose = Purpose,
                    time_of_day = timeOfDay,
                    prescribing_doctor_id = SelectedDoctor?.DoctorId,
                    qty = string.IsNullOrEmpty(Qty) ? (int?)null : int.Parse(Qty),
                    days_supply = string.IsNullOrEmpty(DaysSupply) ? (int?)null : int.Parse(DaysSupply),
                    is_active = IsActive,
                    rxotc = IsRx ? "rx" : "otc"
                };
                success = await _api.UpdateMedicationAsync(
                    _original!.MedicationId, payload);
            }

            if (success)
            {
                StatusMessage = IsAddMode ? "Medication added." : "Medication updated.";
                OnSaved?.Invoke();
            }
            else
            {
                StatusMessage = "Something went wrong. Please try again.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void ToggleMorning()
    {
        Morning = !Morning;
        UpdateTimeOfDayColors();
    }

    [RelayCommand]
    public void ToggleMidday()
    {
        Midday = !Midday;
        UpdateTimeOfDayColors();
    }

    [RelayCommand]
    public void ToggleEvening()
    {
        Evening = !Evening;
        UpdateTimeOfDayColors();
    }

    [RelayCommand]
    public void ToggleNight()
    {
        Night = !Night;
        UpdateTimeOfDayColors();
    }

    [RelayCommand]
    public void SelectRx()
    {
        IsRx = true;
        UpdateRxOtcColors();
    }

    [RelayCommand]
    public void SelectOtc()
    {
        IsRx = false;
        UpdateRxOtcColors();
    }

    private void UpdateTimeOfDayColors()
    {
        var active = Color.FromArgb("#1976d2");
        var inactive = Color.FromArgb("#0f3460");
        MorningColor = Morning ? active : inactive;
        MiddayColor = Midday ? active : inactive;
        EveningColor = Evening ? active : inactive;
        NightColor = Night ? active : inactive;
    }

    [RelayCommand]
    public async Task LookupNdcAsync()
    {
        if (string.IsNullOrWhiteSpace(NdcCode))
        {
            NdcStatusMessage = "Enter an NDC code first.";
            return;
        }

        IsBusy = true;
        NdcStatusMessage = string.Empty;

        try
        {
            var digits = new string(NdcCode.Where(char.IsDigit).ToArray());

            if (digits.Length is not (10 or 11 or 12 or 14))
            {
                // 10/11 = NDC as printed/CMS-normalized.
                // 12 = UPC-A, 14 = GTIN-14 — both possible from a barcode scan;
                // handled via the openfda.upc fallback below.
                NdcStatusMessage = "That doesn't look like a valid NDC or barcode.";
                return;
            }

            using var client = new System.Net.Http.HttpClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            // Try every plausible product_ndc split (the label's 11-digit
            // format doesn't tell you which segment was zero-padded — see
            // GenerateProductNdcCandidates for why) before falling back to
            // package_ndc, which some repackaged/relabeled NDCs need.
            // Using drug/ndc.json (NDC Directory) rather than drug/label.json —
            // fields are top-level (no openfda wrapper) and it returns a
            // packaging[] array we can match the exact bottle against.
            System.Text.Json.JsonElement? drug = null;
            string? matchedNdc = null;
            bool matchedOnPackage = false;
            bool matchedOnBarcode = false;

            // NDC-shaped input (10/11 digits): try the padding-ambiguity
            // candidates as before.
            if (digits.Length is 10 or 11)
            {
                foreach (var candidate in GenerateProductNdcCandidates(digits))
                {
                    drug = await TryFetchByFieldAsync(client, "product_ndc", candidate);
                    if (drug is not null)
                    {
                        matchedNdc = candidate;
                        break;
                    }
                }

                if (drug is null)
                {
                    foreach (var candidate in GeneratePackageNdcCandidates(digits))
                    {
                        drug = await TryFetchByFieldAsync(client, "packaging.package_ndc", candidate);
                        if (drug is not null)
                        {
                            matchedNdc = candidate;
                            matchedOnPackage = true;
                            break;
                        }
                    }
                }
            }

            // Barcode-shaped input (12-digit UPC-A or 14-digit GTIN from a
            // scanner) — or NDC input that didn't resolve above — try the
            // harmonized openfda.upc field. Most Rx bottle barcodes actually
            // encode the NDC directly, but OTC products commonly carry a
            // consumer UPC instead, which is a different number space.
            if (drug is null)
            {
                foreach (var candidate in GenerateUpcCandidates(digits))
                {
                    drug = await TryFetchByFieldAsync(client, "openfda.upc", candidate);
                    if (drug is not null)
                    {
                        matchedNdc = candidate;
                        matchedOnBarcode = true;
                        break;
                    }
                }
            }

            if (drug is null)
            {
                // Genuinely not in openFDA — discontinued/repackaged NDCs are
                // routinely excluded from the "finished product" directory.
                // This is an expected outcome, not just an API miss.
                NdcStatusMessage = "NDC not found. You can still enter the medication manually.";
                return;
            }

            System.Diagnostics.Debug.WriteLine($"=== NDC MATCHED on {matchedNdc} (via {(matchedOnBarcode ? "openfda.upc" : matchedOnPackage ? "packaging.package_ndc" : "product_ndc")})");

            var result = drug.Value;

            var brandName = GetString(result, "brand_name");
            var genericName = GetString(result, "generic_name");
            var baseName = brandName ?? genericName ?? string.Empty;

            string? strength = null;
            if (result.TryGetProperty("active_ingredients", out var ai) &&
                ai.ValueKind == System.Text.Json.JsonValueKind.Array &&
                ai.GetArrayLength() > 0)
            {
                strength = FormatStrength(GetString(ai[0], "strength"));
            }

            // Append strength to the name, e.g. "Pantoprazole Sodium 40 mg".
            Name = strength is not null ? $"{baseName} {strength}" : baseName;

            // Deliberately NOT touching Dosage/Instructions here. That field
            // holds the pharmacy-printed sig — "Take 1 tablet by mouth once
            // a day in the morning for 90 days" — which comes from the
            // prescriber and the dispensing pharmacy, not from the drug
            // product itself. openFDA has no way to know a specific
            // patient's dose, frequency, timing, or duration, so there is no
            // NDC lookup path that could fill this field correctly. It stays
            // whatever the user typed, always.

            var productType = GetString(result, "product_type")?.ToLower();
            IsRx = productType != "human otc drug";
            UpdateRxOtcColors();

            // Match the exact package the user typed (all 3 segments) against
            // the packaging array purely for display. This is the manufacturer's
            // DISTRIBUTION package size (e.g. "500 TABLET... in 1 BOTTLE" is what
            // ships to the pharmacy) — it is NOT what gets dispensed to the
            // patient, so it must never be written into Qty. Informational only.
            string? packageNote = null;
            if (result.TryGetProperty("packaging", out var packaging) &&
                packaging.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var typedPackageNdc = digits.Length == 11
                    ? $"{digits[..5]}-{digits[5..9]}-{digits[9..11]}"
                    : null;

                System.Text.Json.JsonElement? matchedPackage = null;
                foreach (var pkg in packaging.EnumerateArray())
                {
                    var pkgNdc = GetString(pkg, "package_ndc");
                    // Compare on digits only — the printed/padded form may
                    // differ from openFDA's stored form (e.g. 429 vs 0429).
                    var pkgDigits = pkgNdc is null ? "" : new string(pkgNdc.Where(char.IsDigit).ToArray());
                    if (pkgDigits == digits || pkgNdc == typedPackageNdc)
                    {
                        matchedPackage = pkg;
                        break;
                    }
                }

                var chosen = matchedPackage ?? (packaging.GetArrayLength() > 0 ? packaging[0] : (System.Text.Json.JsonElement?)null);
                if (chosen is not null)
                {
                    var description = GetString(chosen.Value, "description");
                    if (description is not null)
                    {
                        packageNote = matchedPackage is null
                            ? $"manufacturer package: {description} (closest match — exact size not confirmed)"
                            : $"manufacturer package: {description}";
                    }
                }
            }

            // Best-effort: fetch usage/indication from drug/label.json via
            // spl_id. OTC drugs carry a short "purpose" (e.g. "Antacid");
            // Rx drugs only have a long indications_and_usage paragraph, so
            // we just take the first sentence as a rough summary. If this
            // call fails or the fields aren't present, we silently skip it —
            // it's a bonus, not something the rest of the lookup depends on.
            var splId = GetString(result, "spl_id");
            string? usageText = null;
            if (!string.IsNullOrEmpty(splId))
            {
                usageText = await TryFetchUsageAsync(client, result, splId);
            }

            // Write it into the actual Purpose field, not just the status
            // message — only if the user hasn't already typed something.
            if (usageText is not null && string.IsNullOrWhiteSpace(Purpose))
            {
                Purpose = usageText;
            }

            var noteParts = new List<string>();
            if (usageText is not null) noteParts.Add($"usage: {usageText}");
            if (packageNote is not null) noteParts.Add(packageNote);

            NdcStatusMessage = noteParts.Count > 0
                ? $"✓ Found: {Name} — {string.Join("; ", noteParts)}"
                : $"✓ Found: {Name}";
        }
        catch (Exception ex)
        {
            NdcStatusMessage = "Lookup failed. Check your connection.";
            System.Diagnostics.Debug.WriteLine($"=== NDC LOOKUP ERROR: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Cleans up an openFDA strength string for display: "40 mg/1" -> "40 mg"
    /// (the "/1" is a per-single-unit denominator, not meaningful to show),
    /// but "125 mg/5 mL" is left alone since the denominator is meaningful
    /// for liquids.
    /// </summary>
    private static string? FormatStrength(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return raw.EndsWith("/1") ? raw[..^2].Trim() : raw.Trim();
    }

    /// <summary>
    /// Fetches a short usage/indication string. Tries pharm_class[EPC] first
    /// — the "Established Pharmacologic Class" is FDA-standardized and short
    /// — then translates it to plain language via a static lookup table (see
    /// TranslateEpcToPlainLanguage) since raw clinical class names ("Proton
    /// Pump Inhibitor") mean nothing to a caregiver. Falls back to the OTC
    /// "purpose" field from drug/label.json if no EPC entry exists at all.
    /// Deliberately does NOT fall back to truncating indications_and_usage
    /// prose: that path amounts to summarizing unstructured text without
    /// understanding it, which is the wrong tool for the job here.
    /// </summary>
    private static async Task<string?> TryFetchUsageAsync(
        System.Net.Http.HttpClient client, System.Text.Json.JsonElement ndcResult, string splId)
    {
        var epcClass = ExtractEpcClass(ndcResult);
        if (epcClass is not null) return TranslateEpcToPlainLanguage(epcClass);

        try
        {
            var url = $"https://api.fda.gov/drug/label.json?search=id:\"{splId}\"&limit=1";
            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;

            var raw = await response.Content.ReadAsStringAsync();
            var json = System.Text.Json.JsonDocument.Parse(raw);
            var results = json.RootElement.GetProperty("results");
            if (results.GetArrayLength() == 0) return null;

            var label = results[0];

            if (label.TryGetProperty("purpose", out var purpose) &&
                purpose.ValueKind == System.Text.Json.JsonValueKind.Array &&
                purpose.GetArrayLength() > 0)
            {
                var p = purpose[0].GetString();
                if (!string.IsNullOrWhiteSpace(p)) return p.Trim();
            }

            // Some labels carry pharm_class here too, under openfda,
            // even when the ndc.json record didn't have it.
            if (label.TryGetProperty("openfda", out var openfda))
            {
                var fallbackEpc = ExtractEpcClass(openfda);
                if (fallbackEpc is not null) return TranslateEpcToPlainLanguage(fallbackEpc);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Translates an FDA Established Pharmacologic Class name into
    /// caregiver-plain language. This is a small, hand-maintained lookup —
    /// not a summarizer — because EPC terms are a closed, standardized FDA
    /// vocabulary (a few hundred total), and a caregiving app only actually
    /// encounters a modest recurring subset of them (BP, cholesterol,
    /// diabetes, GI, pain, mood). Unmapped classes fall through to the raw
    /// EPC term rather than blank — imperfect but still more actionable
    /// than nothing — and get logged so the table can grow from real usage.
    /// Lookup is case-insensitive; extend this dictionary as new drugs
    /// surface unmapped classes in the debug log.
    /// </summary>
    private static readonly Dictionary<string, string> EpcPlainLanguage = new(StringComparer.OrdinalIgnoreCase)
    {
        // Cardiovascular / blood pressure
        ["Angiotensin Converting Enzyme Inhibitor"] = "Lowers blood pressure",
        ["Angiotensin 2 Receptor Antagonist"] = "Lowers blood pressure",
        ["Angiotensin 2 Receptor Blocker"] = "Lowers blood pressure",
        ["Beta-Adrenergic Blocker"] = "Lowers blood pressure and heart rate",
        ["Calcium Channel Blocker"] = "Lowers blood pressure",
        ["Dihydropyridine Calcium Channel Blocker"] = "Lowers blood pressure",
        ["Thiazide Diuretic"] = "Reduces fluid buildup (\"water pill\") and lowers blood pressure",
        ["Loop Diuretic"] = "Reduces fluid buildup (\"water pill\")",
        ["Antiarrhythmic"] = "Helps keep heart rhythm steady",

        // Cholesterol
        ["HMG-CoA Reductase Inhibitor"] = "Lowers cholesterol",

        // Blood thinning / clotting
        ["Platelet Aggregation Inhibitor"] = "Helps prevent blood clots",
        ["Factor Xa Inhibitor"] = "Helps prevent blood clots",
        ["Vitamin K Antagonist"] = "Helps prevent blood clots",

        // Diabetes / blood sugar
        ["Sulfonylurea"] = "Lowers blood sugar",
        ["Biguanide"] = "Lowers blood sugar",
        ["Insulin"] = "Lowers blood sugar",
        ["Dipeptidyl Peptidase 4 Inhibitor"] = "Lowers blood sugar",
        ["Sodium-Glucose Cotransporter 2 Inhibitor"] = "Lowers blood sugar",

        // GI
        ["Proton Pump Inhibitor"] = "Reduces stomach acid",
        ["Histamine-1 Receptor Antagonist"] = "Reduces stomach acid or relieves allergy symptoms",
        ["Osmotic Laxative"] = "Relieves constipation",
        ["Antiemetic"] = "Prevents nausea and vomiting",

        // Pain / inflammation
        ["Nonsteroidal Anti-inflammatory Drug"] = "Relieves pain and inflammation",
        ["Opioid Agonist"] = "Relieves pain",
        ["Full Opioid Agonists"] = "Relieves pain",
        ["Corticosteroid"] = "Reduces inflammation",

        // Mental health / neuro
        ["Selective Serotonin Reuptake Inhibitor"] = "Treats depression or anxiety",
        ["Serotonin Reuptake Inhibitor"] = "Treats depression or anxiety",
        ["Benzodiazepine"] = "Treats anxiety or helps with sleep",
        ["Atypical Antipsychotic"] = "Treats mood or psychiatric symptoms",
        ["Mood Stabilizer"] = "Helps stabilize mood",
        ["Anti-epileptic Agent"] = "Prevents seizures",

        // Respiratory / allergy
        ["Antihistamine"] = "Relieves allergy symptoms",
        ["Expectorant"] = "Loosens mucus to relieve cough",

        // Thyroid
        ["Thyroid Hormone"] = "Treats an underactive thyroid",

        // Infection
        ["Penicillin-class Antibacterial"] = "Treats bacterial infection",
        ["Azole Antifungal"] = "Treats fungal infection",
    };

    /// <summary>
    /// Looks up a plain-language translation for an EPC class name. Returns
    /// the raw class name unchanged if there's no entry yet — but logs it,
    /// since an unmapped class showing up in real usage is exactly the
    /// signal for what to add to EpcPlainLanguage next.
    /// </summary>
    private static string TranslateEpcToPlainLanguage(string epcClassName)
    {
        if (EpcPlainLanguage.TryGetValue(epcClassName, out var plain))
        {
            return plain;
        }

        System.Diagnostics.Debug.WriteLine($"=== UNMAPPED EPC CLASS (add to EpcPlainLanguage): {epcClassName}");
        return epcClassName;
    }

    /// <summary>
    /// Finds the FDA "Established Pharmacologic Class" entry in a
    /// pharm_class array (e.g. "Proton Pump Inhibitor [EPC]") and strips
    /// the trailing tag. Falls back to the first entry with any bracket
    /// tag stripped if no [EPC]-tagged entry exists. Returns null if
    /// pharm_class is absent or empty.
    /// </summary>
    private static string? ExtractEpcClass(System.Text.Json.JsonElement element)
    {
        if (!element.TryGetProperty("pharm_class", out var pharmClass) ||
            pharmClass.ValueKind != System.Text.Json.JsonValueKind.Array ||
            pharmClass.GetArrayLength() == 0)
        {
            return null;
        }

        string? firstAny = null;
        foreach (var entry in pharmClass.EnumerateArray())
        {
            var value = entry.GetString();
            if (string.IsNullOrWhiteSpace(value)) continue;

            var stripped = System.Text.RegularExpressions.Regex
                .Replace(value, @"\s*\[[A-Za-z]+\]\s*$", "").Trim();

            firstAny ??= stripped;

            if (value.Contains("[EPC]", StringComparison.OrdinalIgnoreCase))
            {
                return stripped;
            }
        }

        return firstAny;
    }

    /// <summary>
    /// Queries drug/ndc.json (the NDC Directory) for a single
    /// {field}:"{value}" match. Returns null on NOT_FOUND, empty results,
    /// or any HTTP/parse error — callers treat null as "try the next candidate."
    /// </summary>
    private static async Task<System.Text.Json.JsonElement?> TryFetchByFieldAsync(
        System.Net.Http.HttpClient client, string field, string value)
    {
        try
        {
            var url = $"https://api.fda.gov/drug/ndc.json?search={field}:\"{value}\"&limit=1";
            var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                return null; // includes 404 NOT_FOUND
            }

            var raw = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine($"=== NDC RAW ({field}:{value}): {raw[..Math.Min(300, raw.Length)]}");

            var json = System.Text.Json.JsonDocument.Parse(raw);
            var results = json.RootElement.GetProperty("results");

            return results.GetArrayLength() > 0 ? results[0] : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetString(System.Text.Json.JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var prop) &&
        prop.ValueKind == System.Text.Json.JsonValueKind.String
            ? prop.GetString()
            : null;

    /// <summary>
    /// Generates plausible UPC candidates for the openfda.upc fallback.
    /// A 12-digit UPC-A scans as-is. A 14-digit GTIN-14 wraps a UPC-A with
    /// an indicator digit + check digit, so we also try stripping the
    /// leading indicator digit and the trailing check digit. NDC-length
    /// input (10/11) is passed through too, since some OTC products are
    /// occasionally indexed under openfda.upc using their NDC-derived form.
    /// </summary>
    private static IEnumerable<string> GenerateUpcCandidates(string digits)
    {
        yield return digits; // as scanned

        if (digits.Length == 14)
        {
            yield return digits[1..13]; // strip GTIN-14 indicator + trailing check digit
            yield return digits[1..];   // strip only the indicator digit
        }
    }

    /// <summary>
    /// Generates plausible 10-digit product_ndc strings (labeler-product)
    /// from a scanned/typed NDC, covering all three FDA segment layouts
    /// (4-4-2, 5-3-2, 5-4-1). The 11-digit code printed on a bottle is a
    /// CMS billing normalization — one segment was zero-padded to make
    /// every NDC 11 digits — and you can't tell which segment from the
    /// digits alone. Order: 5-4-1 first (no product-segment padding, the
    /// common case), then 5-3-2 (padded product segment — e.g. a bottle
    /// reading 13668-0429-05 where the real product_ndc is 13668-429),
    /// then 4-4-2 (padded labeler segment, rare).
    /// </summary>
    private static IEnumerable<string> GenerateProductNdcCandidates(string digits)
    {
        if (digits.Length == 10)
        {
            yield return $"{digits[..4]}-{digits[4..8]}";   // 4-4
            yield return $"{digits[..5]}-{digits[5..8]}";   // 5-3
            yield return $"{digits[..5]}-{digits[5..9]}";   // 5-4
            yield break;
        }

        var labeler5 = digits[..5];
        var product4 = digits[5..9];

        yield return $"{labeler5}-{product4}"; // 5-4-1: package segment was padded

        if (product4.StartsWith('0'))
        {
            yield return $"{labeler5}-{product4[1..]}"; // 5-3-2: product segment was padded
        }

        if (labeler5.StartsWith('0'))
        {
            yield return $"{labeler5[1..]}-{product4}"; // 4-4-2: labeler segment was padded
        }
    }

    /// <summary>
    /// Generates plausible package_ndc candidates (all three segments)
    /// as a fallback when no product_ndc candidate matches.
    /// </summary>
    private static IEnumerable<string> GeneratePackageNdcCandidates(string digits)
    {
        if (digits.Length == 10)
        {
            yield return $"{digits[..4]}-{digits[4..8]}-{digits[8..10]}";
            yield return $"{digits[..5]}-{digits[5..8]}-{digits[8..10]}";
            yield break;
        }

        var labeler5 = digits[..5];
        var product4 = digits[5..9];
        var package2 = digits[9..11];

        yield return $"{labeler5}-{product4}-{package2}"; // as printed
        if (product4.StartsWith('0'))
        {
            yield return $"{labeler5}-{product4[1..]}-{package2}";
        }
    }
}