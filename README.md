# Vitals

**A physician-validated, caregiver-first health tracking platform for Android and iOS.**

[![Platform](https://img.shields.io/badge/platform-Android%20%7C%20iOS-blue)](https://github.com/k00lkris/Vitals.Maui)
[![Framework](https://img.shields.io/badge/framework-.NET%209%20MAUI-purple)](https://dotnet.microsoft.com/en-us/apps/maui)
[![Backend](https://img.shields.io/badge/backend-FastAPI%20%2B%20PostgreSQL-green)](https://fastapi.tiangolo.com/)
[![Status](https://img.shields.io/badge/status-Active%20Development-orange)](https://github.com/k00lkris/Vitals.Maui)

---

## What Is Vitals?

Vitals is a cross-platform mobile health tracking application built for caregivers and patients who need more than a simple vitals logger. It combines daily health monitoring with a full clinical record — medications, allergies, care team, doctor visits, incident logs, clinical trend analysis, and physician-grade PDF exports — in a single, clean interface, behind real multi-tenant authentication.

The app was conceived alongside its Home Assistant integration and validated by practicing physicians, including a primary care provider and a cardiologist who described the PDF export as the **"gold standard"** for patient-generated health summaries.

---

## Key Features

### 📊 Vitals Tracking
- Blood pressure, heart rate, SpO₂, temperature, weight, and blood glucose
- Dashboard with latest readings and configurable averages (15 / 30 / 45 / 60 day)
- Collapsible trend charts powered by LiveCharts2
- Full vitals history with date range selector and custom range support
- Per-user display preferences (which vitals are shown/tracked) synced between onboarding, Settings, and vitals entry

### 📈 Clinical Visualization Engine
The white paper feature set:
- **LOESS smoothing** applied to raw vital sign data
- **OLS linear regression** with slope coefficient, R², and p-value
- **First and second order derivatives** — rate of change and acceleration
- **Trapezoidal integration** — cumulative BP burden over time
- **Goal range bands** — ACC/AHA guideline overlays (Normal / Elevated / Stage 1 / Stage 2)
- **Medication timeline overlay** — correlate medication changes with BP response
- **Patient-facing plain language indicators** — directional arrows, variability scores, momentum indicators

### 💊 Clinical Value Layer
- **Medications** — grouped by time of day, Rx/OTC, prescribing doctor, refill tracking, NDC lookup with plain-language purpose (via openFDA), active/inactive filtering
- **Care Team** — physician directory with tap-to-call and tap-to-email, PCP designation
- **Allergies** — grouped by type (medication, food, environmental, other) with severity badges
- **Doctor Visit Log** — visit notes, vitals snapshot at time of visit, follow-up scheduling
- **Incident Log** — severity-flagged emergency and incident tracking with outcome notes
- **General Notes** — five clinical note types with contextual templates (Behavioral Observation, Caregiver Handoff, Medication Change, Family Communication, General)

### 📄 PDF Export
- Physician-grade care summary PDF
- Includes vitals history, averages, trend charts, medications, care team, and allergies
- Configurable date range (15 / 30 / 45 / 60 / custom days)
- Generated on-device, stored locally, shareable via native share sheet
- Validated by Dr. Lisa Warren (Primary Care) and Dr. Juan Martinez (Cardiologist); a Vitals-generated PDF was used as a primary clinical reference by a WellMed nurse and scanned into a patient's official chart

### 🔐 Authentication & Multi-Patient
- **Google Sign-In** and **email/password** (with email verification via Resend), both fully functional
- Apple Sign-In planned, pending Apple Developer Program enrollment
- JWT-based sessions with server-side session verification on app launch (a locally-cached token alone is never trusted — the account's continued existence is confirmed server-side)
- Household-scoped multi-patient support — Family tier supports multiple patients per household
- Guided first-run onboarding: welcome → purpose → "who are you tracking for" → patient setup → interactive dashboard walkthrough → vital preferences → first reading (with skip/resume support if interrupted)
- Household sharing/invite (letting a second household member join an existing household) and Apple Sign-In are still outstanding — see Roadmap below

---

## Architecture

```
┌─────────────────────────────────────┐
│         .NET MAUI (Android/iOS)     │
│  MVVM • CommunityToolkit • LiveCharts2 │
└──────────────┬──────────────────────┘
               │ HTTP (JWT Bearer + X-API-KEY)
┌──────────────▼──────────────────────┐
│         FastAPI (Python)            │
│         DigitalOcean Droplet        │
│    Nginx + FastAPI + PostgreSQL     │
└──────────────┬──────────────────────┘
               │
┌──────────────▼──────────────────────┐
│         PostgreSQL                  │
│  Households → Users / Patients →    │
│         Records                     │
└─────────────────────────────────────┘
```

### Mobile Stack
- **.NET 9 MAUI** — cross-platform Android and iOS
- **CommunityToolkit.Mvvm** — ObservableObject, RelayCommand, ObservableProperty
- **CommunityToolkit.Maui** — Popup system for detail screens
- **LiveCharts2 / SkiaSharp** — trend chart visualization
- **Shell navigation** (post-auth) with a separate `NavigationPage`-based flow for Login/Sign Up/Onboarding, which run outside Shell
- **WebAuthenticator** — Google OAuth

### Backend Stack
- **FastAPI** — REST API with Pydantic validation
- **PostgreSQL** — relational data with household-scoped multi-patient support
- **ReportLab + Matplotlib** — server-side PDF generation with embedded charts
- **psycopg2** — direct PostgreSQL connection
- **python-jose** — JWT issuance and verification
- **bcrypt** — password hashing for email/password accounts
- **Resend** — transactional email (account verification)
- **Deployment** — DigitalOcean Droplet, Nginx reverse proxy, uvicorn

### Authentication & Authorization
- Dual auth model: JWT Bearer tokens (mobile app, per-user/household) and a legacy static API key (Home Assistant integration, vitals recording only)
- Every patient-scoped endpoint (vitals, medications, doctors, allergies, visits, incidents, notes, user preferences) verifies the requested record actually belongs to the caller's own household before reading or writing it — not just that the caller is authenticated
- `/api/auth/verify` confirms a locally-cached session's account still exists server-side before the app trusts it

### Database Schema (Key Tables)
```
households
├── users (auth: Google OAuth or email/password + verification)
├── patients
│   ├── vitals
│   ├── medications
│   ├── allergies
│   ├── visit_logs
│   ├── incident_logs
│   └── patient_notes
├── doctors
│   └── patient_doctors (junction)
```

---

## Project Structure

```
Vitals.Maui/
├── Models/              # Data models with JSON deserialization
│   ├── Patient.cs
│   ├── VitalEntry.cs
│   ├── Medication.cs
│   ├── Doctor.cs
│   ├── Allergy.cs
│   ├── VisitLog.cs
│   ├── IncidentLog.cs
│   └── PatientNote.cs
├── Services/
│   ├── ApiService.cs           # Authenticated HTTP calls to FastAPI backend
│   ├── AuthService.cs          # Google OAuth, email/password, session persistence & verification
│   ├── AuthHeaderHandler.cs    # Attaches JWT/API key to every ApiService request
│   ├── AppNavigation.cs        # Root-page swaps (login/logout/onboarding transitions)
│   └── PatientStateService.cs  # Singleton patient context (reset at every session boundary)
├── ViewModels/          # MVVM ViewModels (one per screen/popup)
│   └── Onboarding*.cs   # Welcome, Purpose, Personalization, Patient Setup,
│                         # Sample Dashboard, Vital Preferences, First Reading, Resume Prompt
├── Views/               # XAML pages and popups
│   ├── *Page.xaml       # Full screens
│   └── *Popup.xaml      # CommunityToolkit popups
└── Converters/           # Value converters for XAML bindings
```

---

## Development Roadmap

| Phase | Name | Status |
|-------|------|--------|
| 1 | Core Loop Complete | ✅ Complete |
| 2 | Clinical Value Layer | ✅ Complete |
| 3 | PDF Export on Mobile | ✅ Complete |
| 4 | Clinical Visualization Engine | ✅ Complete |
| 5 | Authentication & Multi-Patient | ✅ Complete |
| 6 | Onboarding Flow | 🔄 In Progress — Apple Sign-In and household sharing/invite outstanding |
| 7 | Stripe Integration & Launch | Sep 2026 |

---

## Clinical Validation

Vitals has been reviewed and validated by practicing clinicians:

- **Dr. Lisa Warren, NP** — Primary Care — *"This is exactly what I need my patients to bring to appointments."*
- **Dr. Juan Martinez, MD** — Cardiologist — *"Gold standard for patient-generated health summaries."*
- **Amy [RN]** — Registered Nurse and clinical co-author (prior authorization platform)

The PDF export format was designed in collaboration with Dr. Warren and has been used in actual clinical appointments; a generated PDF was used as a primary clinical reference by a WellMed nurse and scanned into a patient's official chart.

---

## Home Assistant Integration

Vitals originated alongside a Home Assistant integration sharing the same FastAPI backend. **Current usage is scoped strictly to vitals recording** — submitting readings from HA input helpers via the legacy static-API-key auth path. Medication management, PDF generation, and other write operations are handled through the mobile app only going forward.

The mobile app and HA integration write to the same PostgreSQL database with source tagging (`source = "home_assistant"` vs `source = "doctor_visit"` etc.), so historical HA-submitted readings remain fully visible in the app.

---

## Getting Started

### Prerequisites
- .NET 9 SDK
- Visual Studio 2022 with MAUI workload
- Android SDK (API 35)
- Python 3.11+ (for backend)
- PostgreSQL 14+

### Backend Setup
```bash
cd /opt/vitals_api
python -m venv venv
source venv/bin/activate
pip install -r requirements.txt

# Configure environment
cp .env.example .env
# Edit .env with your DB credentials, API key, household ID, JWT secret, and Resend API key

# Start the API
uvicorn main:app --host 0.0.0.0 --port 8000 --reload
```

### Mobile Setup
1. Clone the repository
2. Open `Vitals.Maui.sln` in Visual Studio 2022
3. Update `AppConfig.cs` with your API base URL, API key, and Google OAuth client ID
4. Select Android device or emulator
5. Build and run
6. **When testing Google Sign-In specifically, use "Start Without Debugging" (Ctrl+F5)** rather than attaching the debugger — an attached debugger can be killed during the OAuth redirect handoff on some emulator/VS combinations, which looks like an infinite consent-screen loop but is a tooling artifact, not an app bug

### Environment Variables (Backend)
```
DB_HOST=localhost
DB_PORT=5432
DB_NAME=vitals
DB_USER=postgres
DB_PASS=your_password
API_KEY=your_api_key
HOUSEHOLD_ID=your_household_uuid
JWT_SECRET=your_jwt_secret
RESEND_API_KEY=your_resend_api_key
```

---

## Background

Vitals was built by a senior developer as a side project alongside full-time employment, caregiving responsibilities, and a September 2026 commercial launch target.

The project originated from a need to track vitals for a family member with advanced dementia — a use case where caregiver continuity, physician communication, and incident documentation are as important as the numbers themselves.

The platform is designed for the **caregiver market**: adult children managing aging parents, spouses tracking chronic conditions, and families coordinating care across multiple providers.

---

## License

Private — All Rights Reserved
© 2026 Vitals Wellness

---

## Contact

For clinical collaboration, white paper co-authorship inquiries, or early access:
**vitals-wellness.com**
