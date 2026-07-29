# Vitals App — Complete Project Handover Document
## Date: June 5, 2026
## Purpose: Full context for continuing development in a new chat session

---

## 1. Project Overview

**Vitals** is a .NET MAUI Android caregiver health tracking app targeting a **September 2026 public launch**. It was built originally for Kris Woitena's mother **Delores Spohn** (DOB 01/16/1955), who has dementia, borderline hypotension, and is on Midodrine. The app tracks vitals, medications, care team, allergies, visit logs, incidents, and notes — and generates clinician-facing PDF reports.

**Exit threshold from Pharmacy Unlimited:** ~1,000 Vitals subscribers.

---

## 2. Infrastructure

### DigitalOcean Droplet
- **IP:** 206.189.207.242
- **Hostname:** vitals-bcba-nyc1
- **Shared with:** BehavioralAnalystPrep.com (BCBA exam prep platform)

### Services on Droplet
| Service | Path | Port | Domain |
|---------|------|------|--------|
| Vitals API | /var/www/vitals/ | 8000 | vitals-wellness.com |
| BCBA API | /var/www/bcbapractice/ | 8001 | behavioralanalystprep.com |
| PostgreSQL | localhost | 5432 | — |
| Nginx | — | 80/443 | Both domains |

### Systemd Services
- `vitals_api.service` — `/var/www/vitals/venv/bin/uvicorn main:app --host 127.0.0.1 --port 8000`
- `bcba-api.service` — `/var/www/bcbapractice/venv/bin/uvicorn bcba_api:app --host 127.0.0.1 --port 8001`

### Deploy Commands (Windows batch files)
- Vitals API: `D:\GitHub\Vitals.Maui\API\deploy_vitals.bat`
  ```bat
  scp D:\GitHub\Vitals.Maui\API\main.py root@206.189.207.242:/var/www/vitals/main.py
  ssh root@206.189.207.242 "systemctl restart vitals_api"
  ```
- Vitals Website: `C:\Users\krist\Documents\vitals_api\vitals_webpage\deploy_vitals_web.bat`
  ```bat
  scp -r C:\Users\krist\Documents\vitals_api\vitals_webpage\* root@206.189.207.242:/var/www/vitals/static/
  ```

### SSL Certificates (Let's Encrypt)
| Domain | Expiry |
|--------|--------|
| vitals-wellness.com | 2026-08-07 |
| behavioralanalystprep.com | 2026-07-19 |
| bcbapractice.com | 2026-07-19 |

### Monitoring
- UptimeRobot monitors both `https://vitals-wellness.com/api/health` and `https://behavioralanalystprep.com`

---

## 3. Database

### PostgreSQL on DigitalOcean
- **Database:** vitals_db
- **User:** vitals_user
- **Password:** vitals_prod_2026x
- **pgAdmin connection:** host 206.189.207.242, port 5432, user bcbauser (superuser), password bcba_prod_2026x

### Household
- **Household ID:** `8956fa9e-ab42-4588-bf54-c63f614095ba`
- **Patients:** Delores Spohn (`a854a0ff-8d8e-4f6a-a290-16e762b19d02`), Jessica Woitena, Kris Woitena

### Key Tables
```
patients, vitals, medications, medication_logs, doctors, patient_doctors,
allergies, doctor_visits, visit_logs, incident_logs, incidents,
patient_notes, patient_users, households, users
```

### Users Table (rebuilt June 2026)
```sql
CREATE TABLE users (
    user_id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    household_id        UUID REFERENCES households(household_id),
    email               TEXT UNIQUE,
    display_name        TEXT,
    avatar_url          TEXT,
    created_at          TIMESTAMPTZ DEFAULT now(),
    last_seen_at        TIMESTAMPTZ,
    auth_provider       TEXT DEFAULT 'email',
    provider_user_id    TEXT,
    stripe_customer_id  TEXT,
    subscription_status TEXT DEFAULT 'trial',
    subscription_ends_at TIMESTAMPTZ,
    theme               TEXT DEFAULT 'vitals_blue',
    show_heart_rate     BOOLEAN DEFAULT true,
    show_spo2           BOOLEAN DEFAULT true,
    show_temperature    BOOLEAN DEFAULT true,
    show_weight         BOOLEAN DEFAULT false,
    show_glucose        BOOLEAN DEFAULT false
);
```

**Current users:**
- Kris: `user_id = e03abe48-fb9c-4d8f-96be-9c0b0a80a238`, email kristopher.woitena@gmail.com
- Jessica: inserted with jessica.woitena@gmail.com

---

## 4. API (main.py)

**Location on droplet:** `/var/www/vitals/main.py`
**Local path:** `D:\GitHub\Vitals.Maui\API\main.py`
**Swagger:** `https://vitals-wellness.com/docs`

### Authentication
- Header: `X-API-KEY: kris_jessica_vitals_2026_secret`
- PDF endpoint uses token query param: `?token=ha`

### Environment Variables (.env)
```
DB_HOST=localhost
DB_PORT=5432
DB_NAME=vitals_db
DB_USER=vitals_user
DB_PASS=vitals_prod_2026x
API_KEY=kris_jessica_vitals_2026_secret
HOUSEHOLD_ID=8956fa9e-ab42-4588-bf54-c63f614095ba
```

### All Endpoints
| Method | Endpoint | Purpose |
|--------|----------|---------|
| POST | /api/vitals | Record vitals |
| GET | /api/vitals/latest | Latest reading |
| GET | /api/vitals/history | History with days param |
| GET | /api/vitals/averages | Averages with days param |
| GET | /api/vitals/analysis | Full clinical analysis |
| GET | /api/patients | List patients |
| POST | /api/patients | Create patient |
| GET | /api/patients_wrapped | HA-compatible patient list |
| GET | /api/medications | Get medications |
| POST | /api/medications | Add medication |
| PATCH | /api/medications/{id} | Update medication |
| GET | /api/medications/{patient_id}/pdf | Generate PDF |
| GET | /api/doctors | Get doctors |
| GET | /api/doctors/household | All household doctors |
| POST | /api/doctors | Create doctor |
| PATCH | /api/doctors/{id} | Update doctor |
| POST | /api/patient_doctors | Link doctor to patient |
| DELETE | /api/patient_doctors | Unlink doctor |
| GET | /api/allergies | Get allergies |
| POST | /api/allergies | Add allergy |
| PATCH | /api/allergies/{id} | Update allergy |
| GET | /api/visits | Get visit logs |
| POST | /api/visits | Create visit |
| PATCH | /api/visits/{id} | Update visit |
| GET | /api/visits/latest | Latest visit for doctor |
| GET | /api/incidents | Get incidents |
| POST | /api/incidents | Create incident |
| PATCH | /api/incidents/{id} | Update incident |
| GET | /api/notes | Get notes |
| POST | /api/notes | Create note |
| PATCH | /api/notes/{id} | Update note |
| GET/HEAD | /api/health | Health check (UptimeRobot) |
| GET | /api/user/preferences | Get user preferences |
| PATCH | /api/user/preferences | Update user preferences |

---

## 5. Clinical Analysis Engine (Phase 5 — Complete)

### Shared Functions in main.py
All math is in module-level shared functions called by both `get_vitals_analysis` and `export_medications_pdf`:

- `_trend_label(slope, significant)` — converts OLS slope to trend string
- `_momentum_label(values, times)` — second derivative momentum from NumPy gradient
- `_consistency_label(r2)` — maps R² to high/moderate/low
- `classify_bp(avg_sys, avg_dia)` — BP classification
- `classify_hr(avg)`, `classify_spo2(avg)`, `classify_temp(avg)`
- `analyze_vital_series(rows, vital_type)` — unified regression + burden for HR/SpO2/Temp
- `run_bp_analysis(rows)` — full BP analysis, returns complete dict

### BP Analysis Response Shape
```json
{
  "systolic": { avg, slope, r2, p_value, significant, trend, consistency, momentum },
  "diastolic": { avg, slope, r2, p_value, significant, trend, consistency, momentum },
  "map": { avg },
  "burden": { normal_pct, elevated_pct, stage1_pct, stage2_pct },
  "hypo_burden": { normal_pct, moderate_pct, severe_pct },
  "sbp_burden": { pct, auc_above_130, total_sys_auc, time_above_pct, prop_above },
  "ttr": { pct, time_in_days, total_days },
  "dbp_burden": { pct, auc_above_80, annualized_mmhg_year, time_above_pct, total_dia_auc },
  "low_dbp_burden": {
    normal_pct, low_pct, severe_pct, critical_pct,
    auc_below_60, annualized_mmhg_year, burden_pct,
    lowest_dia, has_critical, critical_readings, time_below_60_pct
  },
  "classification": "borderline_hypotension|normal|stage1|...",
  "reading_count": N
}
```

### Clinical Methodologies
- **SBP Burden:** Wang et al./SPRINT AUC-weighted. `Burden = (Sa/[Sa+Sb]) × (T1/[T1+T2+T3])`. Target range 100–130 mmHg.
- **TTR:** Rosendaal linear interpolation approximation. Time in 100–130 mmHg.
- **DBP Burden:** Cho et al. *Hypertension* 2024;81:273–281 (DOI: 10.1161/HYPERTENSIONAHA.123.22160). AUC above 80 mmHg re-zeroed at threshold, annualized to mmHg·year.
- **Low DBP Burden:** AUC below 60 mmHg using inverted `interpolated_time_and_area_above` on negated values. Thresholds: Normal ≥70, Low 60-69, Severe <60, Critical <50.
- **MAP:** (SBP + 2×DBP) / 3 per reading, averaged.

### BP Tab Card Order (VitalsAnalysisView.xaml)
1. Classification + MAP
2. Trend (Systolic + Diastolic with momentum)
3. BP Burden — Hypertension (hidden for hypotension)
4. BP Burden — Hypotension / Low BP Burden (shown for hypotension)
5. Low Diastolic Burden (shown for hypotension, 4 tiles + red critical alert)
6. Blood Pressure Burden & Time in Range (SBP Burden + TTR)
7. Resting Pressure Burden (DBP Burden)
8. Plain English + PCP line

### Alert Logic
- **`ShowDiastolicWarning`** (amber): fires when diastolic is significant AND rising, regardless of systolic
- **`ShowLowDiastolicAlert`** (red): fires when `has_critical == true` OR `severe_pct >= 15`

---

## 6. PDF Generation (Page 3 — Clinical Summary)

### Page Structure
- **Page 1:** Header, Most Recent Vitals (Latest | N-Day Average), Allergies, Medications, Care Team
- **Page 2:** Vital Trend Charts (BP, HR, SpO2, Temp) — LOESS smoothed
- **Page 3:** Vitals Analysis
  - Clinical Summary (clinician-facing narrative, 7 paragraphs)
  - Detailed Metrics (Classification, MAP, Systolic Trend, Diastolic Trend, Burden table, SBP Burden + TTR, DBP Burden, Low DBP Burden)
  - Heart Rate, SpO2, Temperature sections with burden tables
  - Physician note citing Wang et al./SPRINT and Cho et al. 2024
- **Page 4:** Historical Vitals table

### Clinical Summary Paragraphs
1. Classification + avg BP + MAP + context
2. Systolic trend (slope, p, R², consistency, momentum)
3. Diastolic trend (same)
4. Diastolic divergence warning (fires whenever diastolic significant + rising)
5. SBP TTR + Burden with TTR-aware consistency note
6b. Low diastolic burden (fires when severe_pct > 0 or has_critical)
6. DBP Burden with Cho et al. HR citation when elevated
7. Clinical impression (TTR-aware for hypertension, 3 tiers)

### TTR-Aware Clinical Impression (Stage 1/2)
- TTR ≥ 70% AND burden < 10% → "well-controlled with occasional excursions"
- TTR ≥ 50% → "moderate consistency, review regimen"
- TTR < 50% → "sustained above-threshold, adherence review"

---

## 7. MAUI App

### AppConfig.cs
```csharp
public static class AppConfig
{
    public const string BaseUrl = "https://vitals-wellness.com";
    public const string ApiKey  = "kris_jessica_vitals_2026_secret";
    public const string UserId  = "e03abe48-fb9c-4d8f-96be-9c0b0a80a238";
}
```

### MauiProgram.cs — Registered Services
**Services (Singleton):** HttpClient, ApiService, PatientStateService, AppShell
**ViewModels (Singleton):** DashboardViewModel, VitalsEntryViewModel, MedicationsViewModel, CareTeamViewModel, AllergiesViewModel, GeneratePdfViewModel, VisitLogViewModel, IncidentLogViewModel, NotesViewModel, SettingsViewModel
**ViewModels (Transient):** MedicationDetailViewModel, DoctorDetailViewModel, VitalsHistoryViewModel, AllergyDetailViewModel, VitalsAnalysisViewModel, VisitDetailViewModel, IncidentDetailViewModel, NoteDetailViewModel
**Pages (Singleton):** DashboardPage, VitalsEntryPage, MedicationsPage, CareTeamPage, AllergiesPage, GeneratePdfPage, VisitLogPage, IncidentLogPage, NotesPage, SettingsPage
**Pages (Transient):** MedicationDetailPopup, DoctorDetailPopup, VitalsHistoryPage, AllergyDetailPopup, VisitDetailPopup, IncidentDetailPopup, NoteDetailPopup, VitalsAnalysisView

### Key Models
- `VitalsAnalysis.cs` — full analysis model including all burden classes
- `LowDbpBurdenAnalysis` — new class (June 2026), has `PlainEnglish`, `HasCritical`, `CriticalReadings`
- `VitalsAnalysis.ShowLowDiastolicAlert` — `HasCritical == true || SeverePct >= 15`
- `VitalsAnalysis.LowDiastolicAlertText` — red alert text for critical diastolic readings

### Key Services
- `PatientStateService` — manages selected patient, patients list; BindingContext for AppShell
- `ApiService` — all HTTP calls; base URL from AppConfig; X-API-KEY header auto-added
- `ThemeService` — static class, `Apply(string theme)` swaps ResourceDictionary keys at runtime

### Converters Registered in App.xaml
- `StringToBoolConverter` — non-empty string → true
- `StringEqualsConverter` — string == parameter → true (used for theme selection indicator)
- `BoolToColorConverter`
- `InvertedBoolConverter`
- `BoolToStringConverter`

---

## 8. Theme System

### Theme Keys in App.xaml (ResourceDictionary)
```xml
PageBackground, CardBackground, CardStroke, PrimaryAccent,
TextPrimary, TextSecondary, TextMuted, ButtonBackground, ButtonSecondary,
TileNormalBg, TileLowBg, TileSevereBg, TileCriticalBg, DividerColor,
ShellForeground, ShellTitle
```

### Default (Vitals Blue) Values in App.xaml
```xml
<Color x:Key="PageBackground">#e8f4f8</Color>
<Color x:Key="CardBackground">#f0f9ff</Color>
<Color x:Key="CardStroke">#b2dff2</Color>
<Color x:Key="PrimaryAccent">#006e8c</Color>
<Color x:Key="TextPrimary">#0d2137</Color>
<Color x:Key="TextSecondary">#546e7a</Color>
<Color x:Key="TextMuted">#78909c</Color>
<Color x:Key="ButtonBackground">#00acc1</Color>
<Color x:Key="ButtonSecondary">#b2dff2</Color>
<Color x:Key="TileNormalBg">#e0f4f0</Color>
<Color x:Key="TileLowBg">#e1f5fe</Color>
<Color x:Key="TileSevereBg">#e8f5fd</Color>
<Color x:Key="TileCriticalBg">#b2ebf2</Color>
<Color x:Key="DividerColor">#b2dff2</Color>
<Color x:Key="ShellForeground">White</Color>
<Color x:Key="ShellTitle">White</Color>
```

### Four Themes
| Theme | Key Colors |
|-------|-----------|
| **dark** | Page #121212, Card #1e1e1e, Accent #90caf9, Text White |
| **light** | Page #f5f5f5, Card #ffffff, Accent #1565c0, Text #212121 |
| **vitals_blue** (default) | Page #e8f4f8, Card #f0f9ff, Accent #006e8c, Text #0d2137 |
| **system** | Calls Apply("light") or Apply("dark") based on AppTheme |

### ThemeService.cs Location
`Vitals.Maui/Services/ThemeService.cs`

### App.xaml.cs — Theme Applied on Startup
```csharp
var theme = Preferences.Get("theme", "vitals_blue");
ThemeService.Apply(theme);
```

### SettingsViewModel — Theme Persistence
- `Preferences.Set("theme", theme)` — local device persistence
- `_apiService.UpdateUserPreferencesAsync(AppConfig.UserId, payload)` — server sync

---

## 9. Pages Status — Dynamic Resource Migration

Pages that have been fully migrated to use `{DynamicResource ...}` instead of hardcoded colors:
- ✅ SettingsPage.xaml — built with dynamic resources from scratch
- ✅ DashboardPage.xaml — migrated June 2026

Pages still using hardcoded colors (need migration):
- ⬜ VitalsEntryPage.xaml
- ⬜ VitalsAnalysisView.xaml
- ⬜ MedicationsPage.xaml
- ⬜ CareTeamPage.xaml
- ⬜ AllergiesPage.xaml
- ⬜ VitalsHistoryPage.xaml
- ⬜ VisitLogPage.xaml
- ⬜ IncidentLogPage.xaml
- ⬜ NotesPage.xaml
- ⬜ GeneratePdfPage.xaml
- ⬜ All detail popups

**Migration pattern:** Replace hardcoded values as follows:
```
BackgroundColor="#16213e" or "#1a1a2e"  →  {DynamicResource CardBackground}
BackgroundColor="#0d0d0d" or "#1a1a2e"  →  {DynamicResource PageBackground}
Stroke="#2a2a4a"                         →  {DynamicResource CardStroke}
BackgroundColor="#2a2a4a" (BoxView)      →  {DynamicResource DividerColor}
TextColor="#e0e0e0" or "#888888"         →  {DynamicResource TextSecondary}
TextColor="#666666"                      →  {DynamicResource TextMuted}
TextColor="#90caf9"                      →  {DynamicResource PrimaryAccent}
BackgroundColor="#1976d2" or "#0f3460"   →  {DynamicResource ButtonBackground}
```
**WARNING:** Do NOT find/replace across entire project — corrupted files in previous attempt.
Do one file at a time, paste into Claude, get corrected version back.

---

## 10. AppShell.xaml — Current State

- `FlyoutBackgroundColor="{DynamicResource CardBackground}"`
- `Shell.BackgroundColor="{DynamicResource CardBackground}"`
- `Shell.ForegroundColor="{DynamicResource ShellForeground}"`
- `Shell.TitleColor="{DynamicResource ShellTitle}"`
- `Shell.Resources` contains `FlyoutItem` DataTemplate with `TextColor="{DynamicResource ShellForeground}"`
- Flyout header is hardcoded `#0f3460` (intentionally branded, not themed)
- Picker `TitleColor="White"` and `TextColor="White"` (hardcoded since header is always dark)

### Current Menu Items
🏠 Home | ➕ Enter Vitals | 💊 Medications | 👨‍⚕️ Care Team | 📄 Generate PDF | ⚠️ Allergies | 📋 Vitals History | 🏥 Visit Log | ⚠️ Incident Log | 📝 Notes | ⚙️ Settings

---

## 11. Home Assistant Integration

- **HA runs on:** Raspberry Pi at 192.168.68.62:8123 (Docker container)
- **Config file:** `/var/lib/homeassistant/homeassistant/configuration.yaml` (accessible via Blueprint Studio addon)
- All API URLs updated from `http://192.168.68.116:8000` to `https://vitals-wellness.com`
- HA confirmed hitting DO — patient and doctor lists populate correctly on startup
- **Vitals wall** (Lovelace dashboard) working — all vitals pulling correctly

### HA Key Config
- `rest:` sensor polls `patients_wrapped` every scan interval
- `rest_command:` entries for submit_vitals, add/update medication, add/update doctor, export_medications_pdf, add/update allergy
- `sensor:` REST sensors for latest_vitals_raw, medications, vitals_history, doctors_raw, vitals_averages, allergies

---

## 12. Current Outstanding Issues / Next Steps

### Immediately Pending
1. **Theme migration** — remaining pages still use hardcoded colors. Work through one page at a time. DashboardPage is done; start with VitalsEntryPage next.
2. **AppShell flyout item text** — was still showing black on dark theme in last screenshot. The `Shell.Resources` DataTemplate fix was just applied — needs verification on next build.
3. **ButtonSecondary for inactive day buttons** — `UpdateButtonColors()` in DashboardViewModel updated to read `ButtonSecondary` from resources. Needs verification.

### Short Term (Before Launch)
4. **Multi-user auth** — Google/Apple Sign-In via Firebase Auth, JWT, household creation. Users table is ready. No auth logic exists yet — purely API key currently.
5. **Onboarding flow** — Welcome → Create Account → Verify → Household → First Patient → Trial → Ready
6. **Stripe subscription** — products to create, webhook handler for checkout.session.completed, invoice.payment_succeeded, customer.subscription.deleted. Trial is currently unlimited.
7. **Vital field preferences wiring** — `SettingsViewModel` saves preferences to API and local `Preferences`. The VitalsEntry form still shows all fields regardless — need to read preferences and hide/show fields accordingly.
8. **Jessica's preferences** — UserId in AppConfig is Kris's. Before multi-user auth, Jessica's phone needs its own UserId or a shared approach.

### Medium Term
9. **Glucose module** — separate opt-in, own recording form, TIR analysis, own PDF section
10. **Google Play submission**
11. **White paper** — clinical analysis engine grounded in two peer-reviewed papers, WellMed endorsements, September launch as prospective observational study phase
12. **Prior authorization platform** — separate product, Amy as co-founder

---

## 13. Key Clinical Context — Delores Spohn

- **DOB:** 01/16/1955
- **Conditions:** Alzheimer's/dementia, borderline hypotension (recently improved to Normal on Midodrine)
- **PCP:** Gregorio Jimenez, Schertz Pkwy, Schertz TX
- **Cardiologist:** Juan Martinez
- **Key medications:** Midodrine 2.5mg TID (low BP), Memantine (Alzheimer's), Divalproex (mood/seizure), Mirtazapine (depression/sleep)
- **Historical low:** Diastolic 49 mmHg on 05/14/2026 — triggered `has_critical = true`
- **Current status (June 2026):** Classified Normal, BP improving on Midodrine. Diastolic running higher (some readings 80+). Worth monitoring for overcorrection.
- **WellMed nurse** scanned Vitals PDF into official medical chart — 9+ clinical endorsements total

---

## 14. Raspberry Pi (Local Backup)

- **IP:** 192.168.68.116
- **Vitals API still running** at `/opt/vitals_api/` on port 8000 as local backup
- **Service:** `vitals_api.service` (underscore, not hyphen)
- **Do not stop** — kept as fallback
- All app traffic now routes to DO; Pi is passive

---

## 15. Key File Locations

| File | Location |
|------|----------|
| Vitals API | D:\GitHub\Vitals.Maui\API\main.py |
| AppConfig.cs | Vitals.Maui/AppConfig.cs |
| App.xaml | Vitals.Maui/App.xaml |
| App.xaml.cs | Vitals.Maui/App.xaml.cs |
| AppShell.xaml | Vitals.Maui/AppShell.xaml |
| ThemeService.cs | Vitals.Maui/Services/ThemeService.cs |
| ApiService.cs | Vitals.Maui/Services/ApiService.cs |
| PatientStateService.cs | Vitals.Maui/Services/PatientStateService.cs |
| VitalsAnalysis.cs | Vitals.Maui/Models/VitalsAnalysis.cs |
| SettingsPage.xaml | Vitals.Maui/Views/SettingsPage.xaml |
| SettingsViewModel.cs | Vitals.Maui/ViewModels/SettingsViewModel.cs |
| DashboardPage.xaml | Vitals.Maui/Views/DashboardPage.xaml |
| VitalsAnalysisView.xaml | Vitals.Maui/Views/VitalsAnalysisView.xaml |
| Vitals website | C:\Users\krist\Documents\vitals_api\vitals_webpage\ |

---

## 16. Recent Outage Record

**BehavioralAnalystPrep.com outage — May 9, 2026**
- Duration: ~14 minutes (17:16–17:30 UTC)
- Cause: Empty Nginx config file for behavioranalystprep domain exposed during Vitals migration
- Secondary: shared.js API_BASE had '/api' prefix but routes are at /auth/, /bcba/ directly
- Fix: Reconstructed Nginx config, changed API_BASE from '/api' to ''
- Impact: 2 trial users (Kerin, Harlis) — no paying subscribers
- Apology emails sent via Resend — both delivered
- Post-mortem written and stored

---

## 17. Vitals Website

- **URL:** https://vitals-wellness.com
- **Static files:** `/var/www/vitals/static/`
- **Nginx serves** `/` from static directory
- **Current state:** Coming soon page with "Clinical clarity for the people who care most" headline, September 2026 launch date, Vitals Blue branding
- **Planned:** Email waitlist capture (deferred until build is closer to launch)
