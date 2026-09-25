from fastapi import FastAPI, Header, HTTPException, Request, Response, Query, Body, Depends, BackgroundTasks
from pydantic import BaseModel, Field, validator
from typing import Optional, List, Literal
from datetime import datetime, date, timedelta
from uuid import UUID
from dotenv import load_dotenv
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse, StreamingResponse, HTMLResponse
from fastapi.middleware.cors import CORSMiddleware
from fastapi.staticfiles import StaticFiles
from reportlab.lib.pagesizes import LETTER
from reportlab.pdfgen import canvas
from reportlab.lib.utils import ImageReader
from reportlab.lib import colors
from io import BytesIO
import google.auth.transport.requests
from google.oauth2 import id_token as google_id_token
from jose import jwt as jose_jwt, JWTError
from datetime import timezone
import matplotlib
import numpy as np
from scipy import stats
matplotlib.use('Agg')
import matplotlib.pyplot as plt
import matplotlib.dates as mdates
import json
import os
import psycopg2
import re
import bcrypt
import secrets
import requests
from zoneinfo import ZoneInfo
import statistics


load_dotenv()

DB_CONFIG = {
    "host": os.getenv("DB_HOST"),
    "port": os.getenv("DB_PORT"),
    "dbname": os.getenv("DB_NAME"),
    "user": os.getenv("DB_USER"),
    "password": os.getenv("DB_PASS")
}

API_KEY = os.getenv("API_KEY")
HOUSEHOLD_ID = os.getenv("HOUSEHOLD_ID")
EXPECTED_TOKEN = "ha"
print("API_KEY FROM ENV:", API_KEY)
print("HOUSEHOLD_ID FROM ENV:", HOUSEHOLD_ID)

JWT_SECRET = os.getenv("JWT_SECRET", "vitals_jwt_secret_2026x")
JWT_ALGORITHM = "HS256"
JWT_EXPIRE_HOURS = 24 * 7  # 7 days
PUBLIC_PATHS = {
    "/api/auth/google", "/api/health",
    "/api/auth/register", "/api/auth/login",
    "/api/auth/verify-email", "/api/auth/resend-verification",
}

RESEND_API_KEY = os.getenv("RESEND_API_KEY")
EMAIL_FROM = "Vitals <noreply@vitals-wellness.com>"

# --------------------
# Auth dependency
# Must be defined BEFORE app = FastAPI()
# --------------------
def get_auth(
    request: Request,
    x_api_key: str = Header(None, alias="X-API-KEY"),
    authorization: str = Header(None)
):
    if request.url.path in PUBLIC_PATHS:
        return {}

    # Prefer JWT (mobile app) when present — it identifies a specific
    # user/household. The mobile client always sends X-API-KEY alongside
    # the JWT too (several other endpoints separately require it via their
    # own check_key() call), so checking the API key first meant EVERY
    # mobile request was being silently treated as the legacy single-tenant
    # path, regardless of which account was actually signed in. This is
    # what caused new patients to be written into the original hardcoded
    # household instead of whichever household the JWT actually belonged to.
    if authorization and authorization.startswith("Bearer "):
        token = authorization.split(" ")[1]
        try:
            payload = jose_jwt.decode(token, JWT_SECRET, algorithms=[JWT_ALGORITHM])
            return payload
        except JWTError:
            raise HTTPException(status_code=401, detail="Invalid or expired token")

    # Legacy API key (Home Assistant) — only reached when no Bearer token
    # was sent at all.
    if x_api_key and x_api_key == API_KEY:
        return {"type": "api_key"}

    raise HTTPException(status_code=401, detail="Unauthorized")


def get_household_id(auth: dict = Depends(get_auth)) -> str:
    """
    Derives the caller's household_id from their auth context, instead of
    the hardcoded HOUSEHOLD_ID env var (a leftover from the pre-multi-tenant,
    single-household Raspberry Pi/Home Assistant era). Without this, every
    mobile user — regardless of which household they actually belong to —
    would read and write against the one household HOUSEHOLD_ID points to.

    - Legacy API-key callers (Home Assistant) have no per-household concept,
      so they keep using the env var for backward compatibility.
    - JWT callers (the mobile app) get the household_id claim actually
      encoded in their token by create_jwt().
    """
    if auth.get("type") == "api_key":
        return HOUSEHOLD_ID

    household_id = auth.get("household_id")
    if not household_id:
        raise HTTPException(status_code=401, detail="Token missing household_id")
    return household_id


def get_own_user_id(auth: dict = Depends(get_auth)) -> Optional[str]:
    """
    For endpoints that take a user_id directly as a query/path parameter
    (e.g. /api/user/preferences), confirms the caller is the SAME user_id
    they're trying to read or modify — otherwise anyone with a valid
    session could pass a different user's user_id and read or change that
    user's settings. Returns None for the legacy API-key path, which has
    no per-user concept and is handled separately by callers.
    """
    if auth.get("type") == "api_key":
        return None

    user_id = auth.get("sub")
    if not user_id:
        raise HTTPException(status_code=401, detail="Token missing user id")
    return user_id


app = FastAPI(
    title="Vitals Tracking API",
    dependencies=[Depends(get_auth)]
)
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

app.mount("/pdfs", StaticFiles(directory="pdfs"), name="pdfs")


# --------------------
# Database helper
# --------------------
def get_conn():
    return psycopg2.connect(**DB_CONFIG)

# --------------------
# Auth check (legacy — keeps HA endpoints working)
# --------------------
def check_key(key):
    if key != API_KEY:
        raise HTTPException(status_code=401, detail="Unauthorized")

# --------------------
# Household ownership check
# --------------------
def verify_patient_household(cur, patient_id: str, household_id: str):
    """
    Confirms patient_id actually belongs to household_id before any read or
    write proceeds. Without this, any authenticated caller — mobile JWT or
    the shared legacy API key both — could read or modify any OTHER
    household's patient data just by passing a different patient_id, since
    check_key() only validates the static API key and never checked which
    household a patient_id actually belongs to. Call this right after
    opening the cursor, before the endpoint's main query.
    """
    cur.execute(
        "SELECT 1 FROM patients WHERE patient_id = %s AND household_id = %s",
        (patient_id, household_id)
    )
    if cur.fetchone() is None:
        raise HTTPException(status_code=403, detail="Patient not found in your household")


def verify_child_record_household(cur, table: str, id_column: str, record_id: str, household_id: str):
    """
    Same idea as verify_patient_household, but for endpoints that take a
    child record's own id (medication_id, allergy_id, visit_id,
    incident_id, note_id) rather than a patient_id directly — joins through
    to patients to find which household actually owns it.
    """
    cur.execute(f"""
        SELECT 1 FROM {table} t
        JOIN patients p ON p.patient_id = t.patient_id
        WHERE t.{id_column} = %s AND p.household_id = %s
    """, (record_id, household_id))
    if cur.fetchone() is None:
        raise HTTPException(status_code=403, detail="Record not found in your household")

# --------------------
# Validation
# --------------------
@app.exception_handler(RequestValidationError)
async def validation_exception_handler(request: Request, exc: RequestValidationError):
    body = await request.body()
    try:
        body_json = json.loads(body)
    except Exception:
        body_json = body.decode()

    print("🚨 VALIDATION ERROR PAYLOAD:", body_json)
    print("🚨 VALIDATION ERRORS:", exc.errors())

    return JSONResponse(
        status_code=422,
        content={"detail": exc.errors()},
    )

# --------------------
# Models
# --------------------
class VitalCreate(BaseModel):
    patient_id: str
    recorded_at: Optional[datetime] = None
    # The device's UTC offset (in minutes) at the moment this reading was
    # taken — e.g. -300 for Central Daylight Time. Lives on vitals, not
    # any per-vital context table, since it's a property of the reading
    # itself (when/where it was taken), not specific to heart rate —
    # every future vital's analysis benefits from real per-reading local
    # time without needing its own copy of this field. Optional: readings
    # from before this field existed, or from a source that doesn't send
    # it, fall back to a hardcoded America/Chicago approximation at
    # analysis time (see _hr_local_datetime) rather than being excluded.
    local_offset_minutes: Optional[int] = None
    systolic: Optional[int] = Field(None, ge=50, le=250)
    diastolic: Optional[int] = Field(None, ge=30, le=150)
    oxygen_saturation: Optional[int] = Field(None, ge=50, le=100)
    heart_rate: Optional[int] = Field(None, ge=30, le=220)
    temperature: Optional[float] = Field(None, ge=90, le=110)
    blood_glucose: Optional[int] = Field(None, ge=30, le=600)
    weight: Optional[float] = Field(None, ge=50, le=700)
    source: Optional[str] = "home_assistant"
    notes: Optional[str] = ""

    # Heart-rate context (Heart Rate Analysis Spec §4) — all optional since
    # no client sends these yet (the Entry-form UI to collect them hasn't
    # been built). Accepted now so the backend is ready the moment it is,
    # with no further model changes needed then. Ignored entirely unless
    # heart_rate is also present on this submission.
    hr_activity_context: Optional[str] = None   # 'resting' | 'post_activity' | 'during_activity' | 'unknown'
    hr_posture: Optional[str] = None             # 'seated' | 'supine' | 'standing' | 'unknown'
    hr_symptom_tags: Optional[list[str]] = None  # e.g. dizziness, palpitations, syncope, dyspnea, fatigue, chest_discomfort, other
    hr_source_type: Optional[str] = None         # 'manual' | 'BP_monitor' | 'pulse_oximeter' | 'wearable' | 'imported'
    hr_device_irregular_pulse_flag: Optional[bool] = None

class PatientCreate(BaseModel):
    first_name: str
    last_name: str
    dob: Optional[date] = None
    gender: Optional[str]
    # How the creating user relates to this patient (e.g. "self",
    # "caregiver") — recorded in patient_users, not used for access control.
    # Household-wide access is still the current model; this is metadata
    # for future features (primary caregiver, notification routing, etc.)
    # so that ground doesn't need backfilling later.
    relationship: Optional[str] = "caregiver"
    # household_id intentionally NOT a client-supplied field — it's derived
    # server-side from the authenticated caller via get_household_id(), never
    # trusted from the request body. (It used to be listed here but was never
    # actually read from `p.household_id` in create_patient — dead weight
    # that also made Pydantic wrongly require clients to supply it.)

class PatientOut(BaseModel):
    patient_id: str
    first_name: str
    last_name: str
    dob: Optional[date]
    gender: Optional[str]

class MedicationCreate(BaseModel):
    patient_id: UUID
    name: str
    dosage: Optional[str] = None
    time_of_day: Optional[List[str]] = None
    prescribing_doctor_id: Optional[UUID] = None
    @validator('prescribing_doctor_id', pre=True)
    def empty_str_to_none(cls, v):
        if v == '' or v == 'None' or v == 'none':
            return None
        return v
    qty: Optional[int] = None
    days_supply: Optional[int] = None
    fill_date: Optional[date] = None
    is_active: bool = True
    rxotc: Optional[Literal["rx", "otc"]] = "rx"
    purpose: Optional[str] = None
    # Editable, defaults to today in the UI — not auto-derived from
    # created_at, since a caregiver logging a medication weeks after
    # actually starting it is the normal case, not an edge case.
    start_date: Optional[date] = None

class MedicationUpdate(BaseModel):
    name: Optional[str] = None
    dosage: Optional[str] = None
    prescribing_doctor_id: Optional[UUID] = None
    time_of_day: Optional[List[str]] = None
    qty: Optional[int] = None
    days_supply: Optional[int] = None
    fill_date: Optional[date] = None
    is_active: Optional[bool] = None
    rxotc: Optional[Literal["rx", "otc"]] = None
    purpose: Optional[str] = None
    # Editable after creation too — same reasoning as discontinued_date:
    # if the original start_date turns out to be wrong, there's no
    # reason it should be permanently locked in.
    start_date: Optional[date] = None
    # discontinued (the boolean) is retired — is_active is the single
    # source of truth now. Confirmed unused by both the current mobile
    # app and Home Assistant (medications moved fully to the app; HA is
    # vitals-only now), so nothing depends on it.
    discontinued_date: Optional[date] = None
    # Shared effective date for a dosage and/or time_of_day change in
    # THIS save — editable, defaults to today in the UI. Same rationale
    # as start_date: relying on prompt logging is unreliable, so the
    # date has to be something the user can correct, not something the
    # app silently assumes from when the edit happened to be saved.
    change_effective_date: Optional[date] = None

class DoctorCreate(BaseModel):
    patient_id: UUID
    name: str
    specialty: Optional[str] = None
    phone: Optional[str] = None
    fax: Optional[str] = None
    email: Optional[str] = None
    address: Optional[str] = None
    notes: Optional[str] = None
    is_primary: Optional[bool] = False
    relationship_notes: Optional[str] = None

class DoctorUpdate(BaseModel):
    patient_id: UUID
    name: Optional[str] = None
    specialty: Optional[str] = None
    phone: Optional[str] = None
    fax: Optional[str] = None
    email: Optional[str] = None
    address: Optional[str] = None
    notes: Optional[str] = None
    is_active: Optional[bool] = None
    is_primary: Optional[bool] = None
    relationship_notes: Optional[str] = None

class AllergyCreate(BaseModel):
    patient_id: UUID
    allergen: str
    allergy_type: Literal["medication", "food", "environmental", "other"]
    reaction: Optional[str] = None
    severity: Optional[Literal["mild", "moderate", "severe"]] = None
    notes: Optional[str] = None
    is_active: bool = True

class AllergyUpdate(BaseModel):
    allergen: Optional[str] = None
    allergy_type: Optional[Literal["medication", "food", "environmental", "other"]] = None
    reaction: Optional[str] = None
    severity: Optional[Literal["mild", "moderate", "severe"]] = None
    notes: Optional[str] = None
    is_active: Optional[bool] = None

class PatientDoctorCreate(BaseModel):
    patient_id: str
    doctor_id: str
    is_primary: bool = False
    relationship_notes: Optional[str] = None

class VisitCreate(BaseModel):
    patient_id: str
    doctor_id: Optional[str] = None
    visit_date: Optional[datetime] = None
    reason: Optional[str] = None
    notes: Optional[str] = None
    follow_up_date: Optional[date] = None
    systolic: Optional[int] = Field(None, ge=50, le=250)
    diastolic: Optional[int] = Field(None, ge=30, le=150)
    oxygen_saturation: Optional[int] = Field(None, ge=50, le=100)
    heart_rate: Optional[int] = Field(None, ge=30, le=220)
    temperature: Optional[float] = Field(None, ge=90, le=110)
    blood_glucose: Optional[int] = Field(None, ge=30, le=600)
    weight: Optional[float] = Field(None, ge=50, le=700)

class VisitUpdate(BaseModel):
    doctor_id: Optional[str] = None
    visit_date: Optional[datetime] = None
    reason: Optional[str] = None
    notes: Optional[str] = None
    follow_up_date: Optional[date] = None
    is_active: Optional[bool] = None

class IncidentCreate(BaseModel):
    patient_id: str
    incident_date: Optional[datetime] = None
    severity: Optional[str] = "medium"
    incident_type: Optional[str] = None
    location: Optional[str] = None
    description: Optional[str] = None
    outcome: Optional[str] = None
    follow_up_needed: bool = False
    follow_up_notes: Optional[str] = None

class IncidentUpdate(BaseModel):
    incident_date: Optional[datetime] = None
    severity: Optional[str] = None
    incident_type: Optional[str] = None
    location: Optional[str] = None
    description: Optional[str] = None
    outcome: Optional[str] = None
    follow_up_needed: Optional[bool] = None
    follow_up_notes: Optional[str] = None
    is_active: Optional[bool] = None

class NoteCreate(BaseModel):
    patient_id: str
    note_type: str = "general"
    title: Optional[str] = None
    body: Optional[str] = None

class NoteUpdate(BaseModel):
    note_type: Optional[str] = None
    title: Optional[str] = None
    body: Optional[str] = None
    is_active: Optional[bool] = None

class GoogleAuthRequest(BaseModel):
    id_token: str

class RegisterRequest(BaseModel):
    email: str
    password: str
    display_name: str

class LoginRequest(BaseModel):
    email: str
    password: str

class ResendVerificationRequest(BaseModel):
    email: str

class HouseholdInviteRequest(BaseModel):
    invitee_email: str

class HouseholdJoinRequest(BaseModel):
    invite_code: str

class HouseholdTierRequest(BaseModel):
    tier: str  # "individual" | "family" | "free"

# --------------------
# Utility functions
# --------------------
def parse_daily_frequency(schedule: str):
    if not schedule:
        return None
    s = schedule.lower().strip()
    num_match = re.search(r'(\d+)', s)
    if num_match:
        return int(num_match.group(1))
    keyword_map = {
        "once": 1, "daily": 1, "qd": 1,
        "bid": 2, "twice": 2,
        "tid": 3, "three": 3,
        "qid": 4, "four": 4
    }
    for k, v in keyword_map.items():
        if k in s:
            return v
    return None

def calculate_refill(fill_date, qty, schedule):
    if not fill_date or not qty:
        return None
    if schedule and "as needed" in schedule.lower():
        return None
    freq = parse_daily_frequency(schedule)
    if not freq or freq <= 0:
        return None
    days_supply = qty // freq
    return fill_date + timedelta(days=days_supply)

# =====================================================
# LOESS SMOOTHER
# =====================================================
def loess_smooth(x: np.ndarray, y: np.ndarray, frac: float = 0.4) -> np.ndarray:
    n = len(x)
    smoothed = np.zeros(n)
    window = max(int(np.ceil(frac * n)), 3)
    for i in range(n):
        distances = np.abs(x - x[i])
        idx = np.argsort(distances)[:window]
        x_local = x[idx]
        y_local = y[idx]
        max_dist = distances[idx[-1]] + 1e-10
        w = (1 - (distances[idx] / max_dist) ** 3) ** 3
        W = np.diag(w)
        X_mat = np.column_stack([np.ones(window), x_local])
        try:
            beta = np.linalg.lstsq(W @ X_mat, W @ y_local, rcond=None)[0]
            smoothed[i] = beta[0] + beta[1] * x[i]
        except np.linalg.LinAlgError:
            smoothed[i] = y[i]
    return smoothed

# =====================================================
# SHARED VITALS ANALYSIS FUNCTIONS
# =====================================================

def _trend_label(slope: float, significant: bool) -> str:
    if slope > 0.3 and significant:  return "rising_significant"
    elif slope > 0.3:                return "rising"
    elif slope < -0.3 and significant: return "falling_significant"
    elif slope < -0.3:               return "falling"
    else:                            return "stable"

def _momentum_label(values: np.ndarray, times: np.ndarray) -> str:
    if len(values) >= 3:
        first_deriv  = np.gradient(values, times)
        second_deriv = np.gradient(first_deriv, times)
        avg_momentum = float(np.mean(second_deriv))
    else:
        avg_momentum = 0.0
    if avg_momentum > 0.05:   return "accelerating"
    elif avg_momentum < -0.05: return "decelerating"
    else:                      return "stable"

def _consistency_label(r2: float) -> str:
    if r2 >= 0.7:   return "high"
    elif r2 >= 0.4: return "moderate"
    else:           return "low"

def classify_bp(avg_sys: float, avg_dia: float) -> str:
    if avg_sys < 90 or avg_dia < 60:
        return "hypotension"
    elif avg_sys < 100 and avg_dia >= 60:
        return "borderline_hypotension"
    elif avg_sys < 120 and avg_dia < 80:
        return "normal"
    elif avg_sys < 130 and avg_dia < 80:
        return "elevated"
    elif avg_sys < 140 or avg_dia < 90:
        return "stage1"
    else:
        return "stage2"

def classify_hr(avg: float) -> str:
    if avg < 60:      return "bradycardia"
    elif avg <= 100:  return "normal"
    elif avg <= 120:  return "mild_tachycardia"
    else:             return "tachycardia"

def classify_spo2(avg: float) -> str:
    if avg >= 95:    return "normal"
    elif avg >= 92:  return "mild_hypoxemia"
    elif avg >= 88:  return "moderate_hypoxemia"
    else:            return "severe_hypoxemia"

def classify_temp(avg: float) -> str:
    if avg < 96.8:    return "hypothermia"
    elif avg <= 98.9: return "normal"
    elif avg <= 100.3: return "slightly_elevated"
    elif avg <= 103.0: return "fever"
    else:             return "high_fever"

def analyze_vital_series(rows: list, vital_type: str = "generic") -> dict | None:
    if len(rows) < 7:
        return None

    origin = rows[0][0]
    t = np.array([(r[0] - origin).total_seconds() / 86400 for r in rows])
    v = np.array([float(r[1]) for r in rows])

    slope, _, r_val, p_val, _ = stats.linregress(t, v)
    r2  = float(r_val ** 2)
    avg = float(np.mean(v))
    sig = bool(p_val < 0.05)
    s   = float(slope)

    trend       = _trend_label(s, sig)
    consistency = _consistency_label(r2)
    momentum    = _momentum_label(v, t)

    total_area = float(np.trapezoid(np.ones(len(t)), t))

    def pct(mask):
        return round(float(np.trapezoid(mask.astype(float), t)) / total_area * 100, 1) \
               if total_area > 0 else 0.0

    if vital_type == "hr":
        burden = {
            "bradycardia_pct": pct(v < 60),
            "normal_pct":      pct((v >= 60) & (v <= 100)),
            "mild_tachy_pct":  pct((v > 100) & (v <= 120)),
            "tachycardia_pct": pct(v > 120),
        }
        classification = classify_hr(avg)
    elif vital_type == "spo2":
        burden = {
            "normal_pct":             pct(v >= 95),
            "mild_hypoxemia_pct":     pct((v >= 92) & (v < 95)),
            "moderate_hypoxemia_pct": pct((v >= 88) & (v < 92)),
            "severe_hypoxemia_pct":   pct(v < 88),
        }
        classification = classify_spo2(avg)
    elif vital_type == "temp":
        burden = {
            "hypothermia_pct": pct(v < 96.8),
            "normal_pct":      pct((v >= 96.8) & (v <= 98.9)),
            "elevated_pct":    pct((v > 98.9) & (v <= 100.3)),
            "fever_pct":       pct((v > 100.3) & (v <= 103.0)),
            "high_fever_pct":  pct(v > 103.0),
        }
        classification = classify_temp(avg)
    else:
        burden = {}
        classification = None

    result = {
        "avg":           round(avg, 1),
        "slope":         round(s, 2),
        "r2":            round(r2, 2),
        "p_value":       round(float(p_val), 3),
        "significant":   sig,
        "trend":         trend,
        "consistency":   consistency,
        "momentum":      momentum,
        "reading_count": len(rows),
        "burden":        burden,
    }
    if classification is not None:
        result["classification"] = classification

    return result

def run_bp_analysis(rows: list) -> dict | None:
    if len(rows) < 7:
        return None

    origin    = rows[0][0]
    times     = np.array([(r[0] - origin).total_seconds() / 86400 for r in rows])
    systolic  = np.array([float(r[1]) for r in rows])
    diastolic = np.array([float(r[2]) for r in rows])

    sys_slope, _, sys_r, sys_p, _ = stats.linregress(times, systolic)
    sys_r2  = float(sys_r ** 2)
    avg_sys = float(np.mean(systolic))
    sys_sig = bool(sys_p < 0.05)

    dia_slope, _, dia_r, dia_p, _ = stats.linregress(times, diastolic)
    dia_r2  = float(dia_r ** 2)
    avg_dia = float(np.mean(diastolic))
    dia_sig = bool(dia_p < 0.05)

    sys_trend = _trend_label(float(sys_slope), sys_sig)
    dia_trend = _trend_label(float(dia_slope), dia_sig)

    sys_consistency = _consistency_label(sys_r2)
    dia_consistency = _consistency_label(dia_r2)

    sys_momentum = _momentum_label(systolic, times)
    dia_momentum = _momentum_label(diastolic, times)

    map_values = (systolic + 2 * diastolic) / 3
    avg_map    = float(np.mean(map_values))

    total_time_days = float(times[-1] - times[0]) if len(times) > 1 else 1.0

    def interpolated_time_and_area_above(values, times_arr, threshold):
        time_above = 0.0
        area_above = 0.0
        n = len(values)
        for i in range(n - 1):
            t0, t1 = times_arr[i], times_arr[i + 1]
            v0, v1 = values[i], values[i + 1]
            dt = t1 - t0
            if dt <= 0:
                continue
            if v0 >= threshold and v1 >= threshold:
                time_above += dt
                area_above += dt * ((v0 - threshold) + (v1 - threshold)) / 2
            elif v0 < threshold and v1 < threshold:
                pass
            else:
                t_cross = t0 + dt * (threshold - v0) / (v1 - v0)
                if v0 < threshold:
                    time_above += (t1 - t_cross)
                    area_above += (t1 - t_cross) * (0 + (v1 - threshold)) / 2
                else:
                    time_above += (t_cross - t0)
                    area_above += (t_cross - t0) * ((v0 - threshold) + 0) / 2
        return time_above, area_above

    def interpolated_time_in_range(values, times_arr, low, high):
        time_in = 0.0
        n = len(values)
        for i in range(n - 1):
            t0, t1 = times_arr[i], times_arr[i + 1]
            v0, v1 = values[i], values[i + 1]
            dt = t1 - t0
            if dt <= 0:
                continue
            in0 = low <= v0 <= high
            in1 = low <= v1 <= high
            if in0 and in1:
                time_in += dt
            elif not in0 and not in1:
                frac_low  = (low  - v0) / (v1 - v0) if v1 != v0 else None
                frac_high = (high - v0) / (v1 - v0) if v1 != v0 else None
                fracs = sorted([f for f in [frac_low, frac_high]
                                if f is not None and 0 < f < 1])
                if len(fracs) == 2:
                    mid_frac = (fracs[0] + fracs[1]) / 2
                    v_mid = v0 + (v1 - v0) * mid_frac
                    if low <= v_mid <= high:
                        time_in += dt * (fracs[1] - fracs[0])
            else:
                if in0 and not in1:
                    if v1 < low:
                        frac = (low - v0) / (v1 - v0)
                    else:
                        frac = (high - v0) / (v1 - v0)
                    time_in += dt * frac
                else:
                    if v0 < low:
                        frac = (low - v0) / (v1 - v0)
                    else:
                        frac = (high - v0) / (v1 - v0)
                    time_in += dt * (1 - frac)
        return time_in

    time_above_130, area_above_130 = interpolated_time_and_area_above(systolic, times, 130.0)
    time_in_ttr  = interpolated_time_in_range(systolic, times, 100.0, 130.0)
    time_below_100, _ = interpolated_time_and_area_above(-systolic, times, -100.0)
    time_below_100 = abs(time_below_100)

    total_obs_time = total_time_days
    total_sys_auc = float(np.trapezoid(systolic, times))
    area_at_130   = 130.0 * total_obs_time
    sa = area_above_130
    sb = max(total_sys_auc - area_at_130, 0.0)

    prop_above = sa / (sa + sb) if (sa + sb) > 0 else 0.0
    prop_time_above = time_above_130 / total_obs_time if total_obs_time > 0 else 0.0

    sbp_burden_pct = round(prop_above * prop_time_above * 100, 1)
    ttr_pct = round((time_in_ttr / total_obs_time * 100), 1) if total_obs_time > 0 else 0.0

    sbp_burden_data = {
        "pct":            sbp_burden_pct,
        "auc_above_130":  round(sa, 2),
        "total_sys_auc":  round(total_sys_auc, 2),
        "time_above_pct": round(prop_time_above * 100, 1),
        "prop_above":     round(prop_above * 100, 1),
    }

    ttr_data = {
        "pct":          ttr_pct,
        "time_in_days": round(time_in_ttr, 2),
        "total_days":   round(total_obs_time, 2),
    }

    time_above_80, area_above_80 = interpolated_time_and_area_above(diastolic, times, 80.0)
    dbp_burden_annualized = area_above_80 / 365.25
    total_dia_auc = float(np.trapezoid(diastolic, times))
    dbp_burden_proportional_pct = round(
        (area_above_80 / total_dia_auc * 100), 1) if total_dia_auc > 0 else 0.0

    dbp_burden_data = {
        "pct":                  dbp_burden_proportional_pct,
        "auc_above_80":         round(area_above_80, 3),
        "annualized_mmhg_year": round(dbp_burden_annualized, 3),
        "time_above_pct":       round(time_above_80 / total_obs_time * 100, 1) if total_obs_time > 0 else 0.0,
        "total_dia_auc":        round(total_dia_auc, 2),
    }

    time_below_60, area_below_60 = interpolated_time_and_area_above(-diastolic, times, -60.0)
    low_dbp_annualized = round(area_below_60 / 365.25, 4)
    total_dia_auc_low = float(np.trapezoid(np.maximum(0.0, 60.0 - diastolic), times))
    low_dbp_burden_pct = round(
        (area_below_60 / total_dia_auc_low * 100), 1) if total_dia_auc_low > 0 else 0.0

    lowest_dia = round(float(np.min(diastolic)), 1)
    has_critical = bool(np.any(diastolic < 50))
    critical_readings = sorted([round(float(v), 1) for v in diastolic if v < 50])

    total_area = float(np.trapezoid(np.ones(len(times)), times))

    def pct(mask):
        return round(float(np.trapezoid(mask.astype(float), times)) / total_area * 100, 1) \
               if total_area > 0 else 0.0

    burden = {
        "normal_pct":   pct(systolic < 120),
        "elevated_pct": pct((systolic >= 120) & (systolic < 130)),
        "stage1_pct":   pct((systolic >= 130) & (systolic < 140)),
        "stage2_pct":   pct(systolic >= 140),
    }

    hypo_burden = {
        "normal_pct":   pct(systolic >= 90),
        "moderate_pct": pct((systolic >= 80) & (systolic < 90)),
        "severe_pct":   pct(systolic < 80),
    }

    low_dbp_burden_data = {
        "normal_pct":           pct(diastolic >= 70),
        "low_pct":              pct((diastolic >= 60) & (diastolic < 70)),
        "severe_pct":           pct(diastolic < 60),
        "critical_pct":         pct(diastolic < 50),
        "auc_below_60":         round(area_below_60, 3),
        "annualized_mmhg_year": low_dbp_annualized,
        "burden_pct":           low_dbp_burden_pct,
        "lowest_dia":           lowest_dia,
        "has_critical":         has_critical,
        "critical_readings":    critical_readings,
        "time_below_60_pct":    round(time_below_60 / total_obs_time * 100, 1) if total_obs_time > 0 else 0.0,
    }

    classification = classify_bp(avg_sys, avg_dia)

    return {
        "systolic": {
            "avg":         round(avg_sys, 1),
            "slope":       round(float(sys_slope), 2),
            "r2":          round(sys_r2, 2),
            "p_value":     round(float(sys_p), 3),
            "significant": sys_sig,
            "trend":       sys_trend,
            "consistency": sys_consistency,
            "momentum":    sys_momentum,
        },
        "diastolic": {
            "avg":         round(avg_dia, 1),
            "slope":       round(float(dia_slope), 2),
            "r2":          round(dia_r2, 2),
            "p_value":     round(float(dia_p), 3),
            "significant": dia_sig,
            "trend":       dia_trend,
            "consistency": dia_consistency,
            "momentum":    dia_momentum,
        },
        "map":            {"avg": round(avg_map, 1)},
        "sbp_burden":     sbp_burden_data,
        "ttr":            ttr_data,
        "dbp_burden":     dbp_burden_data,
        "burden":         burden,
        "hypo_burden":    hypo_burden,
        "low_dbp_burden": low_dbp_burden_data,
        "classification": classification,
        "reading_count":  len(rows),
    }

def _hr_local_datetime(row: dict) -> datetime:
    """
    Converts a row's UTC recorded_at to its LOCAL datetime, using the
    reading's own stored local_offset_minutes when available (real,
    per-reading accuracy — captured by the client at entry time, see
    VitalCreate.local_offset_minutes / VitalEntry.LocalOffsetMinutes).
    Falls back to a hardcoded America/Chicago conversion for readings
    recorded before this field existed, rather than erroring or
    excluding them — same graceful-degradation pattern already used for
    activity_context/posture being null on pre-migration rows.
    """
    if row["local_offset_minutes"] is not None:
        return row["recorded_at"] + timedelta(minutes=row["local_offset_minutes"])
    return row["recorded_at"].astimezone(ZoneInfo("America/Chicago"))

def run_hr_analysis(rows: list, baseline_rows: list | None = None, medication_changes: list | None = None, prior_period_rows: list | None = None) -> dict | None:
    """
    Heart Rate Analysis Spec — merges every implemented analysis set into
    one result, the same pattern run_bp_analysis already uses: one row
    fetch, multiple calculations, one combined dict. New sections get
    added here as they're implemented, not as separate registry entries.

    rows come from VITAL_ANALYSIS_REGISTRY['heart_rate'], already ordered
    ASC by recorded_at. Converted to named dicts immediately below (see
    _row_dict) rather than accessed by tuple position throughout this
    function — adding a new column later means adding one field here,
    not re-indexing every list comprehension in this file.

    A LEFT JOIN means context fields can be SQL NULL for a reading that
    predates heart_rate_context existing — displayed as "unknown" per
    the spec's own language ("unknown-context readings may be shown as
    raw history"), not treated as missing/invalid data. Same graceful
    fallback for local_offset_minutes — see _hr_local_datetime.

    §6.1 — "Latest rate + context". No resting-context gate: activation
    is just "one valid observation" (argmax by recorded_at).

    §6.2 — "Resting Central Tendency and Range". Filtered to
    activity_context == 'resting' from this SAME row set (no second
    query). Gate: n >= 3 eligible resting readings across >= 3 distinct
    LOCAL calendar dates. Below the gate, §6.1's snapshot still returns
    — resting_summary is just null, not the whole result.

    Distinct-day/local-time calculations (§6.2, §6.6, §6.8) use each
    reading's own stored local_offset_minutes when available, falling
    back to a hardcoded America/Chicago conversion only for readings
    that predate this field — see _hr_local_datetime.
    """
    if not rows:
        return None  # activation gate: at least one valid observation

    def _row_dict(r):
        return {
            "recorded_at":          r[0],
            "local_offset_minutes": r[1],
            "bpm":                  r[2],
            "activity_context":     r[3],
            "posture":              r[4],
            "symptom_tags":         r[5],
            "source_type":          r[6],
            "irregular_pulse_flag": r[7],
            "systolic":             r[8],
            "diastolic":            r[9],
            "oxygen_saturation":    r[10],
            "temperature":          r[11],
            "weight":               r[12],
            "blood_glucose":        r[13],
        }

    rows_d = [_row_dict(r) for r in rows]
    latest = rows_d[-1]  # ASC order — last row is the most recent

    # Linked measurement event: whichever OTHER vitals were captured in
    # this same row (§4.2 — vital_id itself IS the event, no separate
    # linkage column needed for the common case). Only non-null values
    # are included, so a heart-rate-only entry reports an empty context
    # rather than a block of misleading nulls.
    linked_context = {}
    if latest["systolic"] is not None and latest["diastolic"] is not None:
        linked_context["blood_pressure"] = {"systolic": latest["systolic"], "diastolic": latest["diastolic"]}
    if latest["oxygen_saturation"] is not None:
        linked_context["oxygen_saturation"] = latest["oxygen_saturation"]
    if latest["temperature"] is not None:
        linked_context["temperature"] = float(latest["temperature"])
    if latest["weight"] is not None:
        linked_context["weight"] = float(latest["weight"])
    if latest["blood_glucose"] is not None:
        linked_context["blood_glucose"] = latest["blood_glucose"]

    result = {
        "bpm":                  latest["bpm"],
        "recorded_at":          latest["recorded_at"].isoformat(),
        "activity_context":     latest["activity_context"] or "unknown",
        "posture":              latest["posture"] or "unknown",
        "symptom_tags":         latest["symptom_tags"] or [],
        "source_type":          latest["source_type"] or "unknown",
        "irregular_pulse_flag": latest["irregular_pulse_flag"],  # preserved as-is; None means "not reported", not "false"
        "linked_context":       linked_context,
        "reading_count":        len(rows_d),
    }

    resting_rows = [r for r in rows_d if r["activity_context"] == "resting"]
    resting_bpms = [r["bpm"] for r in resting_rows]
    distinct_days = len({_hr_local_datetime(r).date() for r in resting_rows})

    if len(resting_bpms) >= 3 and distinct_days >= 3:
        result["resting_summary"] = {
            "n":             len(resting_bpms),
            "distinct_days": distinct_days,
            "mean":          round(statistics.mean(resting_bpms), 1),
            "median":        round(statistics.median(resting_bpms), 1),
            "min":           min(resting_bpms),
            "max":           max(resting_bpms),
            "range":         max(resting_bpms) - min(resting_bpms),
        }
    else:
        # Explicit null, not an omitted key — the client can check
        # `resting_summary is None` rather than handle a missing field.
        result["resting_summary"] = None

    # §6.3 — Between-Reading Dispersion. Reuses resting_bpms from §6.2
    # above (same eligible set, no separate query). Gate is independent
    # of §6.2's — n >= 5 here, with no distinct-day requirement — so it's
    # checked on its own rather than assumed from resting_summary's gate.
    #
    # IQR uses linear-interpolation percentiles (numpy's default method),
    # matching the spec's own formula exactly — deliberately not
    # hand-rolled, since a subtle off-by-one here would be easy to miss.
    #
    # cv_spot is retained per the spec but must NEVER be labeled or
    # displayed as HRV — spot-reading dispersion between separate
    # measurements is not beat-to-beat heart rate variability, which
    # requires RR/NN interval data this app doesn't collect (§2, §9).
    #
    # "Comparison with the prior equal-length period" (§6.3's own
    # suggested output) is implemented below as dispersion_trend — a
    # fixed current-30-vs-prior-30 comparison, not tied to whichever
    # window is requested here. See that block for why it's separate.
    if len(resting_bpms) >= 5:
        q1 = float(np.percentile(resting_bpms, 25))
        q3 = float(np.percentile(resting_bpms, 75))
        mean_bpm = statistics.mean(resting_bpms)
        result["dispersion"] = {
            "n":       len(resting_bpms),
            "sd":      round(statistics.stdev(resting_bpms), 1),
            "iqr":     round(q3 - q1, 1),
            "q1":      round(q1, 1),
            "q3":      round(q3, 1),
            "cv_spot": round(100 * statistics.stdev(resting_bpms) / mean_bpm, 1) if mean_bpm else None,
        }
    else:
        result["dispersion"] = None

    # §6.3 dispersion trend — "most recent 30 days vs the 30 days before
    # that," fixed regardless of whichever window (15/30/45/60) is
    # currently being viewed — genuinely separate from the dispersion
    # block above, which reflects the REQUESTED window. Uses
    # prior_period_rows, a separate 60-day fetch (see
    # VITAL_ANALYSIS_REGISTRY['heart_rate']['prior_period_lookback_days']).
    #
    # Rolling, not anchored to a calendar date: "current" = last 30 days
    # from right now, "prior" = the 30 days before that — recomputed
    # fresh on every call, same as every other window in this app.
    # Two independently-gated failure states, not one: not enough data
    # for the current period at all vs. current is fine but there's no
    # full prior period yet to compare against.
    def _dispersion_block(bpms):
        if len(bpms) < 5:
            return None
        blk_q1 = float(np.percentile(bpms, 25))
        blk_q3 = float(np.percentile(bpms, 75))
        return {"n": len(bpms), "sd": round(statistics.stdev(bpms), 1), "iqr": round(blk_q3 - blk_q1, 1)}

    result["dispersion_trend"] = None
    if prior_period_rows:
        now_utc = datetime.now(timezone.utc)
        prior_resting = [r for r in [_row_dict(pr) for pr in prior_period_rows] if r["activity_context"] == "resting"]

        current_30_bpms = [r["bpm"] for r in prior_resting
                            if now_utc - timedelta(days=30) <= r["recorded_at"] <= now_utc]
        prior_30_bpms = [r["bpm"] for r in prior_resting
                          if now_utc - timedelta(days=60) <= r["recorded_at"] < now_utc - timedelta(days=30)]

        current_block = _dispersion_block(current_30_bpms)
        prior_block = _dispersion_block(prior_30_bpms)

        if current_block is None:
            result["dispersion_trend"] = {
                "status": "insufficient_current",
                "message": "Not enough data for this calculation.",
                "current": None, "prior": None, "sd_delta": None,
            }
        elif prior_block is None:
            result["dispersion_trend"] = {
                "status": "insufficient_prior",
                "message": "Not enough data for a comparison yet.",
                "current": current_block, "prior": None, "sd_delta": None,
            }
        else:
            result["dispersion_trend"] = {
                "status": "ok",
                "message": None,
                "current": current_block,
                "prior": prior_block,
                "sd_delta": round(current_block["sd"] - prior_block["sd"], 1),
            }

    # §6.4 — Resting Trend: Ordinary Least Squares. Same resting_rows as
    # §6.2/§6.3, no new query. Reuses scipy.stats.linregress (already
    # proven for Blood Pressure) rather than hand-deriving slope/R²/p.
    #
    # Two-tier gate, per spec: the slope/trend itself needs n>=5 across
    # >=5 distinct days and a >=7 day span. The p-value specifically
    # needs a STRICTER n>=8 and >=14 day span; below that, slope and
    # modeled change still show, p_value is just omitted.
    #
    # t/v/origin/span_days are computed once here, independent of §6.4's
    # own gate below, so §6.5 (a genuinely looser gate) can use the same
    # arrays without recomputing them.
    span_days = None
    t = v = None
    if resting_rows:
        span_days = (resting_rows[-1]["recorded_at"] - resting_rows[0]["recorded_at"]).total_seconds() / 86400
        origin = resting_rows[0]["recorded_at"]
        t = np.array([(r["recorded_at"] - origin).total_seconds() / 86400 for r in resting_rows])
        v = np.array([float(r["bpm"]) for r in resting_rows])

    if len(resting_bpms) >= 5 and distinct_days >= 5 and span_days is not None and span_days >= 7:
        if np.std(v) == 0:
            # Perfectly flat — no variance means no meaningful trend to
            # test, and SST=0 would make R² undefined (spec's own note).
            result["trend"] = {
                "slope_bpm_per_day": 0.0,
                "modeled_change":    0.0,
                "span_days":         round(span_days, 1),
                "r2":                None,
                "p_value":           None,
                "trend_label":       "stable",
                "consistency":       None,
            }
        else:
            slope, intercept, r_val, p_val, std_err = stats.linregress(t, v)
            r2 = float(r_val ** 2)
            show_p = len(resting_bpms) >= 8 and span_days >= 14
            sig = bool(show_p and p_val < 0.05)

            result["trend"] = {
                "slope_bpm_per_day": round(float(slope), 2),
                "modeled_change":    round(float(slope) * (t.max() - t.min()), 1),
                "span_days":         round(span_days, 1),
                "r2":                round(r2, 2),
                "p_value":           round(float(p_val), 3) if show_p else None,
                "trend_label":       _trend_label(float(slope), sig),
                "consistency":       _consistency_label(r2),
            }
    else:
        result["trend"] = None

    # §6.5 — LOESS Smoothed Trend. "Visualization only" per the spec —
    # this is chart-overlay support, not a statistical claim like §6.4's
    # trend. Reuses loess_smooth() already built and proven for Blood
    # Pressure, called with frac=0.6 to match the spec's span q=0.60.
    #
    # Gate: n>=7 and span>=7 days — looser than §6.4's, no distinct-day
    # requirement. "If d=0 or geometry is singular, omit smoothing at
    # that point" is handled inside loess_smooth itself.
    if len(resting_bpms) >= 7 and span_days is not None and span_days >= 7:
        smoothed = loess_smooth(t, v, frac=0.6)
        result["loess"] = [
            {"recorded_at": r["recorded_at"].isoformat(), "smoothed_bpm": round(float(s), 1)}
            for r, s in zip(resting_rows, smoothed)
        ]
    else:
        result["loess"] = None

    # §6.6 — Personal Baseline Deviation. Uses baseline_rows — a
    # SEPARATE, fixed 37-day lookback from right now (see
    # VITAL_ANALYSIS_REGISTRY['heart_rate']['baseline_lookback_days']),
    # not resting_rows/rows above, which are bounded by whatever window
    # the caller requested. Recent = last 7 days; Baseline = the 30 days
    # before that (days -37 through -8) — a deliberate 1-day gap between
    # them, not touching, matching the spec's exact boundaries.
    result["baseline_deviation"] = None
    if baseline_rows:
        baseline_rows_d = [_row_dict(r) for r in baseline_rows]
        now_utc = datetime.now(timezone.utc)
        baseline_resting = [r for r in baseline_rows_d if r["activity_context"] == "resting"]

        recent_set   = [r for r in baseline_resting if now_utc - timedelta(days=7)  <= r["recorded_at"] <= now_utc]
        baseline_set = [r for r in baseline_resting if now_utc - timedelta(days=37) <= r["recorded_at"] <= now_utc - timedelta(days=8)]

        baseline_bpms = [r["bpm"] for r in baseline_set]
        recent_bpms   = [r["bpm"] for r in recent_set]
        baseline_days = len({_hr_local_datetime(r).date() for r in baseline_set})
        recent_days   = len({_hr_local_datetime(r).date() for r in recent_set})

        # Per spec: "if insufficient, suppress baseline claim" — a
        # distinct, stricter gate from §6.2's, checked independently.
        if len(baseline_bpms) >= 7 and baseline_days >= 7 and len(recent_bpms) >= 3 and recent_days >= 3:
            m_baseline = statistics.median(baseline_bpms)
            m_recent   = statistics.median(recent_bpms)
            delta_bpm  = m_recent - m_baseline
            delta_pct  = round(100 * delta_bpm / m_baseline, 1) if m_baseline != 0 else None

            z_score = None
            if len(baseline_bpms) >= 7:
                s_baseline = statistics.stdev(baseline_bpms)
                if s_baseline > 0:
                    z_score = round((m_recent - statistics.mean(baseline_bpms)) / s_baseline, 2)

            result["baseline_deviation"] = {
                "baseline_median": round(m_baseline, 1),
                "recent_median":   round(m_recent, 1),
                "baseline_n":      len(baseline_bpms),
                "recent_n":        len(recent_bpms),
                "delta_bpm":       round(delta_bpm, 1),
                "delta_pct":       delta_pct,
                "z_score":         z_score,  # internal/clinician use — not for consumer "abnormal" framing (spec §6.6 engineering note)
            }

    # §6.7 — High/Low Resting-Rate Event Analysis. Same resting_rows as
    # §6.2 onward, no new query. Default descriptive thresholds only —
    # no clinician/patient custom-threshold config UI exists yet.
    #
    # Counts and percentages deliberately — never duration (same
    # principle already applied when the PDF's HR burden table was
    # removed).
    #
    # Gate: shown whenever there's at least one resting reading in the
    # window (n_resting >= 1) — counts that come back zero are still a
    # real, useful result, not a missing one. Percentages specifically
    # need the stricter n_resting >= 5, checked independently.
    HIGH_THRESHOLD = 100
    LOW_THRESHOLD = 60
    MARKED_LOW_THRESHOLD = 50

    def cluster_episodes(readings):
        """
        Sort by time; group same-direction readings <=15 minutes apart
        into one episode — three readings taken minutes apart during one
        stressful event should count as one episode, not three.
        """
        if not readings:
            return 0
        ordered = sorted(readings, key=lambda r: r["recorded_at"])
        episodes = 1
        for i in range(1, len(ordered)):
            gap_minutes = (ordered[i]["recorded_at"] - ordered[i - 1]["recorded_at"]).total_seconds() / 60
            if gap_minutes > 15:
                episodes += 1
        return episodes

    n_resting = len(resting_bpms)
    if n_resting >= 1:
        show_pct = n_resting >= 5
        high_readings = [r for r in resting_rows if r["bpm"] > HIGH_THRESHOLD]
        low_readings  = [r for r in resting_rows if r["bpm"] < LOW_THRESHOLD]
        marked_low_readings = [r for r in resting_rows if r["bpm"] < MARKED_LOW_THRESHOLD]

        def reading_detail(r):
            return {"recorded_at": r["recorded_at"].isoformat(), "bpm": r["bpm"], "symptom_tags": r["symptom_tags"] or []}

        result["rate_events"] = {
            "n_resting_in_window": n_resting,
            "thresholds": {"high": HIGH_THRESHOLD, "low": LOW_THRESHOLD, "marked_low": MARKED_LOW_THRESHOLD},
            "high": {
                "count":         len(high_readings),
                "pct":           round(100 * len(high_readings) / n_resting, 1) if show_pct else None,
                "episode_count": cluster_episodes(high_readings),
                "readings":      [reading_detail(r) for r in high_readings],
            },
            "low": {
                "count":         len(low_readings),
                "pct":           round(100 * len(low_readings) / n_resting, 1) if show_pct else None,
                "episode_count": cluster_episodes(low_readings),
                "readings":      [reading_detail(r) for r in low_readings],
            },
            "marked_low_count": len(marked_low_readings),
        }
    else:
        result["rate_events"] = None

    # §6.8 — Time-of-Day Pattern Analysis. Same resting_rows as elsewhere
    # in this function, no new query. Bucket boundaries are local-time,
    # via _hr_local_datetime (per-reading offset when available).
    #
    # Each bucket is gated independently (>=3 readings across >=3
    # distinct days) — a caregiver who only ever logs in the morning
    # should see morning's numbers even if evening never qualifies. The
    # overall pattern summary additionally needs at least 2 qualified
    # buckets to mean anything as a comparison.
    BUCKETS = [
        ("morning",   lambda h: 5 <= h <= 11),
        ("afternoon", lambda h: 12 <= h <= 16),
        ("evening",   lambda h: 17 <= h <= 21),
        ("overnight", lambda h: h >= 22 or h <= 4),
    ]

    median_all = statistics.median(resting_bpms) if resting_bpms else None
    buckets_out = {}
    qualified = []

    for name, in_bucket in BUCKETS:
        bucket_rows = [r for r in resting_rows if in_bucket(_hr_local_datetime(r).hour)]
        bucket_bpms = [r["bpm"] for r in bucket_rows]
        bucket_days = len({_hr_local_datetime(r).date() for r in bucket_rows})
        n = len(bucket_bpms)

        if n >= 3 and bucket_days >= 3:
            mean_b = statistics.mean(bucket_bpms)
            median_b = statistics.median(bucket_bpms)
            buckets_out[name] = {
                "n":             n,
                "distinct_days": bucket_days,
                "mean":          round(mean_b, 1),
                "median":        round(median_b, 1),
                "min":           min(bucket_bpms),
                "max":           max(bucket_bpms),
                "delta_from_overall_median": round(median_b - median_all, 1),
            }
            qualified.append((name, mean_b))
        else:
            # Explicit null for an unqualified bucket, not an omitted
            # key — the client can tell "not enough data yet" apart from
            # "this bucket doesn't exist."
            buckets_out[name] = None

    pattern_summary = None
    if len(qualified) >= 2:
        highest = max(qualified, key=lambda q: q[1])
        lowest  = min(qualified, key=lambda q: q[1])
        pattern_summary = {"highest_period": highest[0], "lowest_period": lowest[0]}

    result["time_of_day"] = {
        "buckets":         buckets_out,
        "pattern_summary": pattern_summary,
    }

    # §6.9 — Symptom Association. Direct association only: a reading's
    # own symptom_tags (stored on heart_rate_context — the SAME
    # measurement event) is currently the only mechanism in the app for
    # logging a symptom at all. The spec's other case — a standalone
    # symptom timestamp matched to the nearest HR reading within ±15
    # minutes — needs a separate symptom-event log that doesn't exist;
    # nothing to decouple from yet, so that fallback isn't implemented.
    #
    # Verified against synthetic data only (same approach already used
    # for §6.7's episode clustering) — no client UI exists yet to
    # actually collect hr_symptom_tags (VitalsEntryPage only has
    # Activity Context/Posture pickers so far), so no real accumulated
    # data exists to check this against. Backend-ready now; the UI to
    # populate it is a separate, later task.
    #
    # Reuses HIGH_THRESHOLD/LOW_THRESHOLD from §6.7 above (same
    # function scope, defined unconditionally there).
    symptom_events_by_tag: dict = {}
    for r in resting_rows:
        for tag in (r["symptom_tags"] or []):
            status = "high" if r["bpm"] > HIGH_THRESHOLD else "low" if r["bpm"] < LOW_THRESHOLD else "normal"
            symptom_events_by_tag.setdefault(tag, []).append({
                "recorded_at":         r["recorded_at"].isoformat(),
                "bpm":                 r["bpm"],
                "time_diff_minutes":   0,  # direct association — same measurement event, never estimated
                "posture":             r["posture"] or "unknown",
                "activity_context":    r["activity_context"] or "unknown",
                "status":              status,
            })

    if symptom_events_by_tag:
        aggregates = {}
        for tag, events in symptom_events_by_tag.items():
            a_k = len(events)
            low_count  = sum(1 for e in events if e["status"] == "low")
            high_count = sum(1 for e in events if e["status"] == "high")
            aggregates[tag] = {
                "associated_count": a_k,  # individual associations may display with just 1 event
                "events":           events,
                # Aggregate proportion only meaningful with >=2 events of
                # this specific symptom category (spec's own gate) — a
                # single dizziness episode doesn't support a "% of the
                # time" statement.
                "pct_low":  round(100 * low_count / a_k, 1) if a_k >= 2 else None,
                "pct_high": round(100 * high_count / a_k, 1) if a_k >= 2 else None,
            }
        result["symptom_association"] = aggregates
    else:
        result["symptom_association"] = None

    # §6.11 — Cross-Vital Same-Event Context. Reuses the EXISTING,
    # already-proven classify_bp/classify_spo2/classify_temp functions
    # to determine each OTHER vital's own exception state — per the
    # spec's explicit architectural principle: never hardcode another
    # vital's clinical thresholds inside heart-rate's own code, defer to
    # that vital's own classification strategy. HR's own exception
    # predicate reuses HIGH_THRESHOLD/LOW_THRESHOLD from §6.7 above.
    #
    # Genuinely different from §6.1's linked_context, which only shows
    # the single LATEST reading's same-event data — this aggregates
    # co-occurrence across EVERY resting reading in the window (e.g.
    # "3 of 8 high-HR readings also had an elevated temperature").
    def hr_exception(bpm):
        if bpm > HIGH_THRESHOLD:
            return "high"
        if bpm < LOW_THRESHOLD:
            return "low"
        return None

    def cooccurrence_block(paired_rows, classify_fn):
        """
        paired_rows: resting_rows already filtered to ones where the
        OTHER vital is present. classify_fn(r) -> category string for
        that single reading, "normal" meaning no exception.
        """
        n_h = 0
        co_occurrence = {"high": 0, "low": 0}
        for r in paired_rows:
            hr_state = hr_exception(r["bpm"])
            if hr_state is None:
                continue
            n_h += 1
            if classify_fn(r) != "normal":
                co_occurrence[hr_state] += 1

        if n_h < 1:
            return None
        show_pct = n_h >= 5
        return {
            "n_hr_condition_events_with_pair": n_h,
            "co_occurrence_high": co_occurrence["high"],
            "co_occurrence_low":  co_occurrence["low"],
            "pct_high": round(100 * co_occurrence["high"] / n_h, 1) if show_pct else None,
            "pct_low":  round(100 * co_occurrence["low"]  / n_h, 1) if show_pct else None,
        }

    bp_pairs   = [r for r in resting_rows if r["systolic"] is not None and r["diastolic"] is not None]
    spo2_pairs = [r for r in resting_rows if r["oxygen_saturation"] is not None]
    temp_pairs = [r for r in resting_rows if r["temperature"] is not None]

    result["cross_vital_context"] = {
        "blood_pressure":    cooccurrence_block(bp_pairs,   lambda r: classify_bp(r["systolic"], r["diastolic"])),
        "oxygen_saturation": cooccurrence_block(spo2_pairs, lambda r: classify_spo2(r["oxygen_saturation"])),
        "temperature":       cooccurrence_block(temp_pairs, lambda r: classify_temp(float(r["temperature"]))),
    }

    # Optional Pearson correlation, physician-only — continuous paired
    # values, not the binary exception states above. Gate: n>=10 paired
    # events across >=7 days, hidden if either variable has zero
    # variance (spec's own note — a flat line correlates trivially with
    # anything and means nothing).
    correlations = {}
    for name, pairs, value_fn in [
        ("temperature",       temp_pairs, lambda r: float(r["temperature"])),
        ("oxygen_saturation", spo2_pairs, lambda r: float(r["oxygen_saturation"])),
        ("systolic",          bp_pairs,   lambda r: float(r["systolic"])),
        ("diastolic",         bp_pairs,   lambda r: float(r["diastolic"])),
    ]:
        if len(pairs) < 10:
            continue
        span = (pairs[-1]["recorded_at"] - pairs[0]["recorded_at"]).total_seconds() / 86400
        if span < 7:
            continue
        hr_vals = np.array([r["bpm"] for r in pairs])
        other_vals = np.array([value_fn(r) for r in pairs])
        if np.std(hr_vals) == 0 or np.std(other_vals) == 0:
            continue
        correlations[name] = round(float(np.corrcoef(hr_vals, other_vals)[0, 1]), 2)

    result["cross_vital_correlations"] = correlations if correlations else None

    # §6.12 — Data Support and Density. A meta-summary of confidence for
    # the CURRENT window — reuses n (len(resting_bpms)), distinct_days,
    # and span_days already computed above rather than re-deriving them.
    # support_state is the HIGHEST gate actually cleared, kept exactly
    # consistent with the real thresholds §6.2/§6.4/§6.5 already enforce
    # above — not an independently redefined ladder.
    #
    # Distinct-day coverage approximates the denominator using the
    # OBSERVED span rather than the originally-requested window length
    # (15/30/45/60 days) — this function only receives rows, not that
    # requested value. A patient with readings clustered in just the last
    # 10 days of a 30-day window would show ~100% coverage of their own
    # observed span here, not ~33% of the full requested window the spec
    # actually describes. Flagged as a real approximation, not built to
    # look more precise than it is — a proper fix means threading the
    # requested `days` value through the cache/registry call chain.
    n = len(resting_bpms)

    if n == 0:
        support_state = "none"
    elif n >= 8 and span_days is not None and span_days >= 14:
        support_state = "significance"
    elif n >= 7 and span_days is not None and span_days >= 7:
        support_state = "loess"
    elif n >= 5 and distinct_days >= 5 and span_days is not None and span_days >= 7:
        support_state = "trend"
    elif n >= 3 and distinct_days >= 3:
        support_state = "descriptive"
    else:
        support_state = "snapshot"

    calendar_days_approx = max(int(span_days) + 1, 1) if span_days is not None else None
    coverage_pct = round(100 * distinct_days / calendar_days_approx, 1) if calendar_days_approx else None

    unavailable = []
    if result["resting_summary"] is None:
        unavailable.append({"analysis": "resting_summary",
                             "reason": f"needs >=3 readings across >=3 distinct days (have n={n}, days={distinct_days})"})
    if result["dispersion"] is None:
        unavailable.append({"analysis": "dispersion",
                             "reason": f"needs >=5 readings (have n={n})"})
    if result["trend"] is None:
        unavailable.append({"analysis": "trend",
                             "reason": f"needs >=5 readings across >=5 distinct days and >=7 day span "
                                       f"(have n={n}, days={distinct_days}, span={round(span_days, 1) if span_days else 0})"})
    elif result["trend"].get("p_value") is None:
        unavailable.append({"analysis": "trend.p_value",
                             "reason": f"needs >=8 readings and >=14 day span "
                                       f"(have n={n}, span={round(span_days, 1) if span_days else 0})"})
    if result["loess"] is None:
        unavailable.append({"analysis": "loess",
                             "reason": f"needs >=7 readings and >=7 day span "
                                       f"(have n={n}, span={round(span_days, 1) if span_days else 0})"})
    if result["baseline_deviation"] is None:
        unavailable.append({"analysis": "baseline_deviation",
                             "reason": "needs its own separate baseline (>=7 readings/>=7 days, 8-37 days ago) "
                                       "and recent (>=3 readings/>=3 days, last 7 days) data — independent of this window"})

    result["data_support"] = {
        "support_state":             support_state,
        "n":                         n,
        "distinct_days":             distinct_days,
        "span_days":                 round(span_days, 1) if span_days is not None else None,
        "distinct_day_coverage_pct": coverage_pct,
        "unavailable_analyses":      unavailable,
    }

    # §6.10 — Medication-Change Association. Uses medication_changes — a
    # completely separate table, fetched once by the caller (see
    # VITAL_ANALYSIS_REGISTRY['heart_rate']['needs_medication_changes']),
    # same "caller fetches, function receives" pattern as baseline_rows.
    #
    # PRE/POST are 14-day windows straddling each change's effective_date,
    # with the change date itself excluded from BOTH — spec's own exact
    # boundary: PRE = [change-14d, change), POST = (change, change+14d].
    # Uses resting_rows (already computed above, same eligible set as
    # every other section) rather than a new query — a medication's
    # effect on heart rate is only meaningful against resting readings,
    # same reasoning as everywhere else in this function.
    #
    # "Confounded" checks the FULL patient medication history, not just
    # this one medication — a heart-rate shift 10 days after a dose
    # change could just as easily be explained by a completely different
    # drug starting the same week. Narrowing the confounded check to only
    # the medication being analyzed would miss exactly that case.
    associations = []
    if medication_changes and resting_rows:
        for med_id, med_name, change_type, effective_date in medication_changes:
            pre_start = effective_date - timedelta(days=14)
            post_end = effective_date + timedelta(days=14)

            pre_rows  = [r for r in resting_rows if pre_start <= _hr_local_datetime(r).date() < effective_date]
            post_rows = [r for r in resting_rows if effective_date < _hr_local_datetime(r).date() <= post_end]

            pre_bpms  = [r["bpm"] for r in pre_rows]
            post_bpms = [r["bpm"] for r in post_rows]
            pre_days  = len({_hr_local_datetime(r).date() for r in pre_rows})
            post_days = len({_hr_local_datetime(r).date() for r in post_rows})

            if len(pre_bpms) >= 3 and pre_days >= 3 and len(post_bpms) >= 3 and post_days >= 3:
                confounded = any(
                    other_date != effective_date and pre_start <= other_date <= post_end
                    for _, _, _, other_date in medication_changes
                )

                m_pre = statistics.median(pre_bpms)
                m_post = statistics.median(post_bpms)
                delta = m_post - m_pre

                associations.append({
                    "medication_id":   str(med_id),
                    "medication_name": med_name,
                    "change_type":     change_type,
                    "effective_date":  effective_date.isoformat(),
                    "pre_n":           len(pre_bpms),
                    "post_n":          len(post_bpms),
                    "pre_median":      round(m_pre, 1),
                    "post_median":     round(m_post, 1),
                    "delta_bpm":       round(delta, 1),
                    "delta_pct":       round(100 * delta / m_pre, 1) if m_pre != 0 else None,
                    "confounded":      confounded,
                })

    result["medication_associations"] = associations if associations else None

    return result
# --------------------
# Vitals analysis cache — eager, per-vital-type
# --------------------
# Standard windows are computed and cached the moment a relevant vital is
# recorded (see the BackgroundTasks hook in record_vitals), so every
# dashboard read against 15/30/45/60 days is a cache hit — compute cost
# scales with how often people log readings, not how often they open the
# app. Anything outside these four (a custom range) is never cached and
# always computed fresh — see get_vitals_analysis.
ANALYSIS_WINDOWS = [15, 30, 45, 60]

# Registry mapping each vital type to how to fetch its rows and analyze
# them. "from_clause" defaults to just "vitals" (blood_pressure needs
# nothing else), but a vital with its own context table — like Heart
# Rate — points this at a LEFT JOIN instead. LEFT, not INNER: a reading
# recorded before heart_rate_context existed has no context row at all,
# and should still show up (with context fields simply unknown) rather
# than silently vanishing from analysis entirely. Deliberately a plain
# dict, not a class hierarchy — nothing here needs polymorphism, just a
# lookup.
VITAL_ANALYSIS_REGISTRY = {
    "blood_pressure": {
        "from_clause": "vitals",
        "columns": "recorded_at, systolic, diastolic, heart_rate, oxygen_saturation, temperature",
        "where_clause": "systolic IS NOT NULL AND diastolic IS NOT NULL",
        "analysis_fn": run_bp_analysis,
    },
    "heart_rate": {
        "from_clause": "vitals LEFT JOIN heart_rate_context ON heart_rate_context.vital_id = vitals.vital_id",
        # Includes the other same-row vitals (systolic/diastolic/spo2/temperature/
        # weight/blood_glucose) — §6.1 ("Latest rate + context") explicitly wants
        # the linked measurement event, not just the BPM value in isolation.
        "columns": (
            "vitals.recorded_at, vitals.local_offset_minutes, vitals.heart_rate, "
            "heart_rate_context.activity_context, heart_rate_context.posture, "
            "heart_rate_context.symptom_tags, heart_rate_context.source_type, "
            "heart_rate_context.device_irregular_pulse_flag, "
            "vitals.systolic, vitals.diastolic, vitals.oxygen_saturation, "
            "vitals.temperature, vitals.weight, vitals.blood_glucose"
        ),
        # A NULL is_invalidated (no context row at all, e.g. a pre-migration
        # reading) is treated the same as "not invalidated" — never excluded
        # just for lacking context, only when explicitly flagged bad.
        "where_clause": (
            "vitals.heart_rate IS NOT NULL "
            "AND (heart_rate_context.is_invalidated IS NULL OR heart_rate_context.is_invalidated = false)"
        ),
        "analysis_fn": run_hr_analysis,
        # §6.6 needs a FIXED 37-day lookback from right now (Recent =
        # last 7 days, Baseline = the 30 days before that) — genuinely
        # independent of whichever standard window (15/30/45/60) is
        # being computed. Optional key: only present when an analysis
        # set actually needs it. When set, recompute_vital_cache and
        # get_cached_or_compute_analysis fetch this ONCE (not once per
        # window — it's the same 37-day data regardless of which
        # window's cache entry is being built) and pass it to
        # analysis_fn as a second argument.
        "baseline_lookback_days": 37,
        # §6.10 needs the patient's full medication_changes history — a
        # completely different table from anything else this function
        # touches, fetched ONCE by the caller (not per-window, same
        # reasoning as baseline_rows) and passed as a third argument.
        "needs_medication_changes": True,
        # §6.3's dispersion trend — fixed "most recent 30 days vs the 30
        # days before that," independent of whichever standard window
        # (15/30/45/60) is currently being viewed. Rolling, not anchored
        # to a calendar date — recomputed relative to now() every time,
        # same as every other window in this app already is.
        "prior_period_lookback_days": 60,
    },
    # "spo2":    {...},   # TODO once the SpO2 spec is implemented
    # "weight":  {...},   # TODO once the Weight spec is implemented
    # "glucose": {...},   # TODO once the Glucose spec is implemented
}

def recompute_vital_cache(patient_id: str, household_id: str, vital_type: str):
    """
    Runs in the background (see BackgroundTasks in record_vitals) — never
    blocks the "vitals recorded" response the user is waiting on. Computes
    all four standard windows for ONE vital type and upserts each into
    vitals_analysis_cache. Scoped to a single vital_type deliberately: a
    new temperature reading has no reason to trigger a Weight or Glucose
    recompute, so record_vitals only schedules this for the vital types
    actually present in that specific submission.
    """
    entry = VITAL_ANALYSIS_REGISTRY.get(vital_type)
    if entry is None:
        return  # not implemented yet for this vital type — nothing to do

    conn = get_conn()
    cur = conn.cursor()
    try:
        # Fetched ONCE, outside the per-window loop — a fixed lookback
        # from right now, not tied to any of the four standard windows.
        # None when the vital type has no such requirement (e.g. blood_pressure).
        baseline_rows = None
        lookback = entry.get("baseline_lookback_days")
        if lookback:
            cur.execute(f"""
                SELECT {entry['columns']}
                FROM {entry['from_clause']}
                WHERE vitals.patient_id = %s
                  AND vitals.household_id = %s
                  AND {entry['where_clause']}
                  AND vitals.recorded_at >= now() - interval '%s days'
                ORDER BY vitals.recorded_at ASC;
            """, (patient_id, household_id, lookback))
            baseline_rows = cur.fetchall()

        # Full medication-change history for this patient — a different
        # table entirely, fetched once, same reasoning as baseline_rows.
        medication_changes = None
        if entry.get("needs_medication_changes"):
            cur.execute("""
                SELECT mc.medication_id, m.name, mc.change_type, mc.effective_date
                FROM medication_changes mc
                JOIN medications m ON m.medication_id = mc.medication_id
                WHERE mc.patient_id = %s
                ORDER BY mc.effective_date ASC;
            """, (patient_id,))
            medication_changes = cur.fetchall()

        # Fixed 60-day lookback for §6.3's dispersion trend — split into
        # current-30/prior-30 inside run_hr_analysis itself, not here;
        # this just fetches the raw 60 days once, same reusable pattern.
        prior_period_rows = None
        prior_lookback = entry.get("prior_period_lookback_days")
        if prior_lookback:
            cur.execute(f"""
                SELECT {entry['columns']}
                FROM {entry['from_clause']}
                WHERE vitals.patient_id = %s
                  AND vitals.household_id = %s
                  AND {entry['where_clause']}
                  AND vitals.recorded_at >= now() - interval '%s days'
                ORDER BY vitals.recorded_at ASC;
            """, (patient_id, household_id, prior_lookback))
            prior_period_rows = cur.fetchall()

        # Built generically so any combination of optional extra datasets
        # works without a combinatorial chain of if/else branches — a
        # future vital needing two or three of these just adds its own
        # registry flag, no changes needed here.
        extra_kwargs = {}
        if lookback:
            extra_kwargs["baseline_rows"] = baseline_rows
        if entry.get("needs_medication_changes"):
            extra_kwargs["medication_changes"] = medication_changes
        if prior_lookback:
            extra_kwargs["prior_period_rows"] = prior_period_rows

        for days in ANALYSIS_WINDOWS:
            cur.execute(f"""
                SELECT {entry['columns']}
                FROM {entry['from_clause']}
                WHERE vitals.patient_id = %s
                  AND vitals.household_id = %s
                  AND {entry['where_clause']}
                  AND vitals.recorded_at >= now() - interval '%s days'
                ORDER BY vitals.recorded_at ASC;
            """, (patient_id, household_id, days))
            rows = cur.fetchall()

            result = entry["analysis_fn"](rows, **extra_kwargs)

            cur.execute("""
                INSERT INTO vitals_analysis_cache (patient_id, vital_type, window_days, result, computed_at)
                VALUES (%s, %s, %s, %s, now())
                ON CONFLICT (patient_id, vital_type, window_days)
                DO UPDATE SET result = EXCLUDED.result, computed_at = now();
            """, (patient_id, vital_type, days, json.dumps(result)))
        conn.commit()
    except Exception as e:
        conn.rollback()
        print(f"=== ANALYSIS CACHE RECOMPUTE ERROR ({vital_type}, patient {patient_id}): {e}")
    finally:
        cur.close()
        conn.close()

def get_cached_or_compute_analysis(patient_id: str, household_id: str, vital_type: str, days: int) -> dict | None:
    """
    Read path used by get_vitals_analysis. Standard windows (15/30/45/60)
    are served from cache — expected to already be there from the eager
    background recompute, but falls back to computing inline on a miss
    (e.g. the very first reading ever recorded for a patient, before any
    background job has run, or right after this cache table's own
    migration) rather than erroring. A custom range always computes fresh
    and is never written to the cache table.
    """
    entry = VITAL_ANALYSIS_REGISTRY.get(vital_type)
    if entry is None:
        return None

    conn = get_conn()
    cur = conn.cursor()
    try:
        if days in ANALYSIS_WINDOWS:
            cur.execute("""
                SELECT result FROM vitals_analysis_cache
                WHERE patient_id = %s AND vital_type = %s AND window_days = %s;
            """, (patient_id, vital_type, days))
            row = cur.fetchone()
            if row is not None:
                return row[0]  # cache hit

        # Cache miss on a standard window, or a custom range — compute now.
        baseline_rows = None
        lookback = entry.get("baseline_lookback_days")
        if lookback:
            cur.execute(f"""
                SELECT {entry['columns']}
                FROM {entry['from_clause']}
                WHERE vitals.patient_id = %s
                  AND vitals.household_id = %s
                  AND {entry['where_clause']}
                  AND vitals.recorded_at >= now() - interval '%s days'
                ORDER BY vitals.recorded_at ASC;
            """, (patient_id, household_id, lookback))
            baseline_rows = cur.fetchall()

        medication_changes = None
        if entry.get("needs_medication_changes"):
            cur.execute("""
                SELECT mc.medication_id, m.name, mc.change_type, mc.effective_date
                FROM medication_changes mc
                JOIN medications m ON m.medication_id = mc.medication_id
                WHERE mc.patient_id = %s
                ORDER BY mc.effective_date ASC;
            """, (patient_id,))
            medication_changes = cur.fetchall()

        prior_period_rows = None
        prior_lookback = entry.get("prior_period_lookback_days")
        if prior_lookback:
            cur.execute(f"""
                SELECT {entry['columns']}
                FROM {entry['from_clause']}
                WHERE vitals.patient_id = %s
                  AND vitals.household_id = %s
                  AND {entry['where_clause']}
                  AND vitals.recorded_at >= now() - interval '%s days'
                ORDER BY vitals.recorded_at ASC;
            """, (patient_id, household_id, prior_lookback))
            prior_period_rows = cur.fetchall()

        extra_kwargs = {}
        if lookback:
            extra_kwargs["baseline_rows"] = baseline_rows
        if entry.get("needs_medication_changes"):
            extra_kwargs["medication_changes"] = medication_changes
        if prior_lookback:
            extra_kwargs["prior_period_rows"] = prior_period_rows

        cur.execute(f"""
            SELECT {entry['columns']}
            FROM {entry['from_clause']}
            WHERE vitals.patient_id = %s
              AND vitals.household_id = %s
              AND {entry['where_clause']}
              AND vitals.recorded_at >= now() - interval '%s days'
            ORDER BY vitals.recorded_at ASC;
        """, (patient_id, household_id, days))
        rows = cur.fetchall()
        result = entry["analysis_fn"](rows, **extra_kwargs)

        # Backfill the cache on a standard-window miss, so the NEXT read
        # is fast too — but never for a custom range, which by definition
        # isn't one of the four windows this cache is keyed on.
        if days in ANALYSIS_WINDOWS and result is not None:
            cur.execute("""
                INSERT INTO vitals_analysis_cache (patient_id, vital_type, window_days, result, computed_at)
                VALUES (%s, %s, %s, %s, now())
                ON CONFLICT (patient_id, vital_type, window_days)
                DO UPDATE SET result = EXCLUDED.result, computed_at = now();
            """, (patient_id, vital_type, days, json.dumps(result)))
            conn.commit()

        return result
    finally:
        cur.close()
        conn.close()

# --------------------
# JWT helper
# --------------------
def create_jwt(user_id: str, household_id: Optional[str], email: str) -> str:
    expire = datetime.now(timezone.utc) + timedelta(hours=JWT_EXPIRE_HOURS)
    payload = {
        "sub":          user_id,
        "household_id": household_id,  # None until tier selection / join completes
        "email":        email,
        "exp":          expire,
    }
    return jose_jwt.encode(payload, JWT_SECRET, algorithm=JWT_ALGORITHM)

# --------------------
# Password hashing (email/password auth)
# --------------------
def hash_password(password: str) -> str:
    return bcrypt.hashpw(password.encode("utf-8"), bcrypt.gensalt()).decode("utf-8")

def verify_password(password: str, password_hash: str) -> bool:
    return bcrypt.checkpw(password.encode("utf-8"), password_hash.encode("utf-8"))

# --------------------
# Household invites
# --------------------
def generate_invite_code() -> str:
    """
    Short, human-typeable code (e.g. A7K9-2XPQ) rather than a long opaque
    token — this gets read off an email and typed into the Personalization
    screen, not clicked as a link, so it needs to be short enough to
    reasonably type by hand without errors.
    """
    alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"  # no 0/O/1/I — easy to misread
    part1 = "".join(secrets.choice(alphabet) for _ in range(4))
    part2 = "".join(secrets.choice(alphabet) for _ in range(4))
    return f"{part1}-{part2}"

def resolve_invite_household(cur, invite_code: str):
    """
    Validates an invite code (exists, unexpired, unused) and returns
    (household_id, invite_id). Raises HTTPException on any failure.
    """
    cur.execute("""
        SELECT invite_id, household_id, expires_at, used_at
        FROM household_invites
        WHERE code = %s
    """, (invite_code.strip().upper(),))
    row = cur.fetchone()

    if not row:
        raise HTTPException(status_code=400, detail="That invite code isn't valid. Double-check it and try again.")

    invite_id, household_id, expires_at, used_at = row

    if used_at is not None:
        raise HTTPException(status_code=400, detail="This invite has already been used.")

    if expires_at < datetime.now(timezone.utc):
        raise HTTPException(status_code=400, detail="This invite code has expired. Ask for a new one.")

    return str(household_id), str(invite_id)

def mark_invite_used(cur, invite_id: str):
    cur.execute("UPDATE household_invites SET used_at = now() WHERE invite_id = %s", (invite_id,))

def count_reserved_slots(cur, household_id: str) -> tuple[int, int, int]:
    """
    Returns (patient_limit, actual_patient_count, active_pending_invite_count).
    Every unused, unexpired invite reserves one slot against the household's
    patient_limit — worst case, every invitee chooses "create a new
    patient" rather than attaching to an existing one, so the primary
    account holder can never issue more invites than the household could
    actually accommodate if all of them were redeemed that way. An invite
    stops reserving a slot the moment it's used, cancelled (both set
    used_at), or naturally expires (excluded here by the expires_at check,
    no cleanup job needed for correctness).
    """
    cur.execute("SELECT patient_limit FROM households WHERE household_id = %s", (household_id,))
    row = cur.fetchone()
    patient_limit = row[0] if row and row[0] is not None else 2

    cur.execute("SELECT COUNT(*) FROM patients WHERE household_id = %s", (household_id,))
    patient_count = cur.fetchone()[0]

    cur.execute("""
        SELECT COUNT(*) FROM household_invites
        WHERE household_id = %s AND used_at IS NULL AND expires_at > now()
    """, (household_id,))
    pending_invite_count = cur.fetchone()[0]

    return patient_limit, patient_count, pending_invite_count

# --------------------
# Verification email (Resend)
# --------------------
def send_verification_email(to_email: str, token: str):
    verify_url = f"https://vitals-wellness.com/api/auth/verify-email?token={token}"
    try:
        response = requests.post(
            "https://api.resend.com/emails",
            headers={
                "Authorization": f"Bearer {RESEND_API_KEY}",
                "Content-Type": "application/json",
            },
            json={
                "from": EMAIL_FROM,
                "to": [to_email],
                "subject": "Verify your Vitals account",
                # Inline styles throughout — email clients (Gmail especially)
                # commonly strip <style> blocks and external stylesheets, so
                # anything not inlined won't render. Matches the landing
                # page's navy/teal palette; DM Serif Display/DM Sans won't
                # load in most mail clients, so this falls back to Georgia
                # (serif, same fallback the site's own CSS already uses)
                # and a plain sans-serif stack.
                "html": f"""
                <body style="margin:0; padding:0; background:#F4F6F9; font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Arial,sans-serif;">
                  <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:#F4F6F9; padding:40px 16px;">
                    <tr>
                      <td align="center">
                        <table role="presentation" width="480" cellpadding="0" cellspacing="0" style="max-width:480px; width:100%; background:#FFFFFF; border:1px solid rgba(26,38,64,0.1); border-radius:16px; padding:40px 36px;">
                          <tr>
                            <td align="center" style="padding-bottom:28px;">
                              <img src="https://vitals-wellness.com/logo.png" alt="Vitals" height="30" style="height:30px;">
                            </td>
                          </tr>
                          <tr>
                            <td style="font-family:Georgia,'Times New Roman',serif; font-size:24px; color:#1A2640; text-align:center; padding-bottom:16px;">
                              Welcome to Vitals
                            </td>
                          </tr>
                          <tr>
                            <td style="font-size:15px; color:#5A6A82; line-height:1.7; text-align:center; padding-bottom:28px;">
                              Click below to verify your email and finish setting up your account.
                            </td>
                          </tr>
                          <tr>
                            <td align="center" style="padding-bottom:28px;">
                              <a href="{verify_url}" style="display:inline-block; background:#00A8C8; color:#FFFFFF; font-size:15px; font-weight:500; text-decoration:none; padding:14px 32px; border-radius:8px;">
                                Verify my email
                              </a>
                            </td>
                          </tr>
                          <tr>
                            <td style="font-size:13px; color:#5A6A82; line-height:1.6; text-align:center; border-top:1px solid rgba(26,38,64,0.1); padding-top:20px;">
                              This link expires in 24 hours. If you didn't create a Vitals account, you can safely ignore this email.
                            </td>
                          </tr>
                        </table>
                        <table role="presentation" width="480" cellpadding="0" cellspacing="0" style="max-width:480px; width:100%;">
                          <tr>
                            <td align="center" style="font-size:12px; color:#5A6A82; padding-top:20px;">
                              &copy; 2026 Vitals-Wellness.com
                            </td>
                          </tr>
                        </table>
                      </td>
                    </tr>
                  </table>
                </body>
                """,
            },
            timeout=10,
        )
        print(f"=== RESEND STATUS: {response.status_code} {response.text}")
    except Exception as e:
        # Don't let a failed email send crash registration itself — the
        # account still exists; the user can request a new verification
        # email via /api/auth/resend-verification if this silently failed.
        print(f"=== RESEND ERROR: {e}")


def send_household_invite_email(to_email: str, code: str, inviter_name: str):
    """
    Sends the invite code by email — the recipient reads/copies the code
    and enters it manually on the Personalization screen during their own
    normal sign-up (email/Google/Apple, whichever they choose). No deep
    link, no dependency on domain-verified App Links/Universal Links.
    """
    try:
        response = requests.post(
            "https://api.resend.com/emails",
            headers={
                "Authorization": f"Bearer {RESEND_API_KEY}",
                "Content-Type": "application/json",
            },
            json={
                "from": EMAIL_FROM,
                "to": [to_email],
                "subject": f"{inviter_name} invited you to Vitals",
                "html": f"""
                <body style="margin:0; padding:0; background:#F4F6F9; font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Arial,sans-serif;">
                  <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:#F4F6F9; padding:40px 16px;">
                    <tr>
                      <td align="center">
                        <table role="presentation" width="480" cellpadding="0" cellspacing="0" style="max-width:480px; width:100%; background:#FFFFFF; border:1px solid rgba(26,38,64,0.1); border-radius:16px; padding:40px 36px;">
                          <tr>
                            <td align="center" style="padding-bottom:28px;">
                              <img src="https://vitals-wellness.com/logo.png" alt="Vitals" height="30" style="height:30px;">
                            </td>
                          </tr>
                          <tr>
                            <td style="font-family:Georgia,'Times New Roman',serif; font-size:24px; color:#1A2640; text-align:center; padding-bottom:16px;">
                              {inviter_name} invited you to Vitals
                            </td>
                          </tr>
                          <tr>
                            <td style="font-size:15px; color:#5A6A82; line-height:1.7; text-align:center; padding-bottom:24px;">
                              Download Vitals, create your account, and when you get to "Who are you tracking for?", choose "Join an existing household" and enter this code:
                            </td>
                          </tr>
                          <tr>
                            <td align="center" style="padding-bottom:24px;">
                              <div style="display:inline-block; background:#F4F6F9; border:1px solid rgba(26,38,64,0.15); border-radius:10px; padding:16px 28px; font-family:Georgia,'Times New Roman',serif; font-size:26px; letter-spacing:0.08em; color:#1A2640; font-weight:bold;">
                                {code}
                              </div>
                            </td>
                          </tr>
                          <tr>
                            <td style="font-size:13px; color:#5A6A82; line-height:1.6; text-align:center; border-top:1px solid rgba(26,38,64,0.1); padding-top:20px;">
                              This code expires in 24 hours. If you weren't expecting this, you can ignore this email.
                            </td>
                          </tr>
                        </table>
                      </td>
                    </tr>
                  </table>
                </body>
                """,
            },
            timeout=10,
        )
        print(f"=== RESEND (INVITE) STATUS: {response.status_code} {response.text}")
    except Exception as e:
        print(f"=== RESEND (INVITE) ERROR: {e}")

def verification_page(title: str, message: str, is_error: bool = False) -> str:
    """
    Shared styled HTML shell for every /api/auth/verify-email outcome
    (success, already-verified, expired, invalid). Matches the actual
    vitals-wellness.com landing page's palette and typography (navy/teal,
    DM Serif Display headline, DM Sans body) rather than the app's dark UI,
    since this page is reached from an email link, in a browser — it
    should look like the brand's public site, not the mobile app.
    """
    accent = "#d32f2f" if is_error else "#00A8C8"
    icon = "!" if is_error else "&#10003;"
    return f"""
    <!DOCTYPE html>
    <html>
    <head>
        <meta name="viewport" content="width=device-width, initial-scale=1.0">
        <title>Vitals</title>
        <link rel="preconnect" href="https://fonts.googleapis.com">
        <link href="https://fonts.googleapis.com/css2?family=DM+Serif+Display:ital@0;1&family=DM+Sans:wght@300;400;500&display=swap" rel="stylesheet">
        <style>
            * {{ box-sizing: border-box; margin: 0; padding: 0; }}
            body {{
                min-height: 100vh; display: flex; align-items: center; justify-content: center;
                background: #F4F6F9;
                font-family: 'DM Sans', -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;
                padding: 24px;
            }}
            .card {{
                background: #FFFFFF; border: 1px solid rgba(26,38,64,0.1); border-radius: 16px;
                padding: 48px 40px; max-width: 420px; width: 100%; text-align: center;
                box-shadow: 0 4px 24px rgba(13,21,37,0.06);
            }}
            .logo {{ height: 32px; margin-bottom: 32px; }}
            .icon {{
                width: 56px; height: 56px; border-radius: 50%;
                background: {accent}; color: white; font-size: 24px; font-weight: 500;
                display: flex; align-items: center; justify-content: center;
                margin: 0 auto 24px;
            }}
            h1 {{
                font-family: 'DM Serif Display', Georgia, 'Times New Roman', serif;
                font-weight: 400; font-size: 26px; letter-spacing: -0.02em;
                color: #1A2640; margin-bottom: 12px;
            }}
            p {{ color: #5A6A82; font-size: 15px; font-weight: 300; line-height: 1.7; }}
        </style>
    </head>
    <body>
        <div class="card">
            <img class="logo" src="https://vitals-wellness.com/logo.png" alt="Vitals">
            <div class="icon">{icon}</div>
            <h1>{title}</h1>
            <p>{message}</p>
        </div>
    </body>
    </html>
    """

# =====================================================
# ENDPOINTS
# =====================================================

# --------------------
# VITALS ENDPOINTS
# --------------------
@app.post("/api/vitals")
def record_vitals(
    vital: VitalCreate,
    background_tasks: BackgroundTasks,
    x_api_key: str = Header(..., alias="X-API-KEY"),
    household_id: str = Depends(get_household_id)
):
    check_key(x_api_key)
    conn = get_conn()
    cur = conn.cursor()
    verify_patient_household(cur, vital.patient_id, household_id)
    cur.execute("""
        INSERT INTO vitals (
            household_id, patient_id, recorded_at, local_offset_minutes,
            systolic, diastolic, oxygen_saturation,
            heart_rate, temperature, blood_glucose,
            weight, source, notes
        )
        VALUES (%s,%s,COALESCE(%s, now()),%s,%s,%s,%s,%s,%s,%s,%s,%s,%s)
        RETURNING vital_id;
    """, (
        household_id, vital.patient_id, vital.recorded_at, vital.local_offset_minutes,
        vital.systolic, vital.diastolic, vital.oxygen_saturation,
        vital.heart_rate, vital.temperature, vital.blood_glucose,
        vital.weight, vital.source, vital.notes
    ))
    vital_id = cur.fetchone()[0]

    # A heart-rate reading always gets a context row, even when every
    # field is unknown/null — this means a FUTURE reading (once the
    # Entry-form UI collects real context) has something to update rather
    # than insert, and it keeps every heart-rate-containing vitals row
    # consistently joinable, instead of some having a context row and
    # others silently not. Same transaction as the vitals insert above —
    # both succeed together or neither does.
    if vital.heart_rate is not None:
        cur.execute("""
            INSERT INTO heart_rate_context (
                vital_id, activity_context, posture, symptom_tags,
                source_type, device_irregular_pulse_flag, is_invalidated
            )
            VALUES (%s, %s, %s, %s, %s, %s, false);
        """, (
            vital_id, vital.hr_activity_context, vital.hr_posture,
            vital.hr_symptom_tags, vital.hr_source_type,
            vital.hr_device_irregular_pulse_flag
        ))

    conn.commit()
    cur.close()
    conn.close()

    # Only recompute the vital types this specific submission actually
    # touched — e.g. a temperature-only reading has no reason to trigger
    # a Weight or Glucose recompute, since that data hasn't changed.
    # Runs after the response would otherwise be sent, so "vitals
    # recorded" comes back immediately regardless of how long the four
    # window computations take.
    if vital.systolic is not None or vital.diastolic is not None:
        background_tasks.add_task(recompute_vital_cache, vital.patient_id, household_id, "blood_pressure")
    if vital.heart_rate is not None:
        background_tasks.add_task(recompute_vital_cache, vital.patient_id, household_id, "heart_rate")
    # Uncomment each block below as its analysis function is implemented:
    # if vital.oxygen_saturation is not None:
    #     background_tasks.add_task(recompute_vital_cache, vital.patient_id, household_id, "spo2")
    # if vital.weight is not None:
    #     background_tasks.add_task(recompute_vital_cache, vital.patient_id, household_id, "weight")
    # if vital.blood_glucose is not None:
    #     background_tasks.add_task(recompute_vital_cache, vital.patient_id, household_id, "glucose")

    return {"status": "success", "vital_id": vital_id, "message": "Vitals recorded"}

@app.get("/api/vitals/latest")
def get_latest_vitals(
    patient_id: str,
    x_api_key: str = Header(...),
    household_id: str = Depends(get_household_id)
):
    check_key(x_api_key)
    try:
        UUID(patient_id)
    except:
        return {}
    conn = get_conn()
    cur = conn.cursor()
    verify_patient_household(cur, patient_id, household_id)
    cur.execute("""
        SELECT recorded_at, systolic, diastolic, oxygen_saturation,
               heart_rate, temperature, weight, blood_glucose
        FROM vitals
        WHERE patient_id = %s
        ORDER BY recorded_at DESC
        LIMIT 1;
    """, (patient_id,))
    row = cur.fetchone()
    cur.close()
    conn.close()
    if not row:
        return {}
    return {
        "recorded_at": row[0],
        "systolic": row[1],
        "diastolic": row[2],
        "oxygen_saturation": row[3],
        "heart_rate": row[4],
        "temperature": row[5],
        "weight": float(row[6]) if row[6] else None,
        "blood_glucose": row[7]
    }

@app.get("/api/vitals/history")
def get_vitals_history(
    patient_id: str,
    days: int = 30,
    x_api_key: str = Header(...),
    household_id: str = Depends(get_household_id)
):
    check_key(x_api_key)
    try:
        UUID(patient_id)
    except:
        return {"rows": []}
    conn = get_conn()
    cur = conn.cursor()
    verify_patient_household(cur, patient_id, household_id)
    cur.execute("""
        SELECT recorded_at, systolic, diastolic, oxygen_saturation,
               heart_rate, round(temperature, 1), weight, blood_glucose
        FROM vitals
        WHERE patient_id = %s
          AND recorded_at >= now() - interval '%s days'
        ORDER BY recorded_at;
    """, (patient_id, days))
    rows = cur.fetchall()
    cur.close()
    conn.close()
    return {
        "rows": [
            {
                "date": r[0].isoformat(),
                "systolic": r[1],
                "diastolic": r[2],
                "spo2": r[3],
                "heart_rate": r[4],
                "temperature": float(r[5]) if r[5] else None,
                "weight": float(r[6]) if r[6] else None,
                "blood_glucose": r[7]
            }
            for r in rows
        ]
    }

@app.get("/api/vitals/averages")
def get_vitals_averages(
    patient_id: str,
    days: int = 15,
    x_api_key: str = Header(...),
    household_id: str = Depends(get_household_id)
):
    check_key(x_api_key)
    try:
        UUID(patient_id)
    except:
        return {}
    conn = get_conn()
    cur = conn.cursor()
    verify_patient_household(cur, patient_id, household_id)
    cur.execute("""
        SELECT
            round(avg(systolic), 1),
            round(avg(diastolic), 1),
            round(avg(heart_rate), 1),
            round(avg(oxygen_saturation), 1),
            round(avg(temperature), 1),
            round(avg(weight), 1),
            round(avg(blood_glucose), 1)
        FROM vitals
        WHERE patient_id = %s
          AND household_id = %s
          AND recorded_at >= now() - interval '%s days'
    """, (patient_id, household_id, days))
    row = cur.fetchone()
    cur.close()
    conn.close()
    return {
        "days": days,
        "systolic": float(row[0]) if row[0] else None,
        "diastolic": float(row[1]) if row[1] else None,
        "heart_rate": float(row[2]) if row[2] else None,
        "oxygen_saturation": float(row[3]) if row[3] else None,
        "temperature": float(row[4]) if row[4] else None,
        "weight": float(row[5]) if row[5] else None,
        "blood_glucose": float(row[6]) if row[6] else None
    }

@app.get("/api/vitals/analysis")
def get_vitals_analysis(
    patient_id: str,
    days: int = 30,
    x_api_key: str = Header(...),
    household_id: str = Depends(get_household_id)
):
    check_key(x_api_key)
    try:
        UUID(patient_id)
    except:
        return {"error": "invalid_patient"}

    # Server-side floor, independent of whatever the client already
    # enforced — same defense-in-depth pattern used elsewhere (patient
    # limits, invite slots). Trend analysis below 15 days of data isn't
    # something we can stand behind, so this is a hard reject, not a
    # soft warning. The four standard windows (15/30/45/60) never hit
    # this — it only matters for a manually-entered custom range.
    if days < 15:
        raise HTTPException(
            status_code=400,
            detail="Custom date ranges must cover at least 15 days — shorter windows aren't reliable enough for trend analysis."
        )

    conn = get_conn()
    cur = conn.cursor()
    verify_patient_household(cur, patient_id, household_id)

    # Each vital gets its own independent row fetch now — previously,
    # heart_rate/oxygen_saturation/temperature were all derived from a
    # single query filtered to "systolic IS NOT NULL AND diastolic IS NOT
    # NULL", meaning a heart-rate-only reading (no BP alongside it) was
    # invisible to this endpoint from the very first line, regardless of
    # anything downstream. Each vital should only depend on its OWN data
    # existing, not on BP happening to be recorded in the same entry.
    cur.execute("""
        SELECT recorded_at, systolic, diastolic
        FROM vitals
        WHERE patient_id = %s AND household_id = %s
          AND systolic IS NOT NULL AND diastolic IS NOT NULL
          AND recorded_at >= now() - interval '%s days'
        ORDER BY recorded_at ASC;
    """, (patient_id, household_id, days))
    bp_row_count = len(cur.fetchall())

    cur.execute("""
        SELECT recorded_at, oxygen_saturation
        FROM vitals
        WHERE patient_id = %s AND household_id = %s
          AND oxygen_saturation IS NOT NULL
          AND recorded_at >= now() - interval '%s days'
        ORDER BY recorded_at ASC;
    """, (patient_id, household_id, days))
    spo2_rows = cur.fetchall()

    cur.execute("""
        SELECT recorded_at, temperature
        FROM vitals
        WHERE patient_id = %s AND household_id = %s
          AND temperature IS NOT NULL
          AND recorded_at >= now() - interval '%s days'
        ORDER BY recorded_at ASC;
    """, (patient_id, household_id, days))
    temp_rows = cur.fetchall()

    cur.execute("""
        SELECT d.name, v.follow_up_date
        FROM patient_doctors pd
        JOIN doctors d ON pd.doctor_id = d.doctor_id
        LEFT JOIN (
            SELECT doctor_id, follow_up_date
            FROM visit_logs
            WHERE patient_id = %s
              AND household_id = %s
              AND is_active = true
              AND follow_up_date IS NOT NULL
              AND follow_up_date >= current_date
            ORDER BY follow_up_date ASC
            LIMIT 1
        ) v ON v.doctor_id = pd.doctor_id
        WHERE pd.patient_id = %s
          AND pd.is_primary = true
          AND d.is_active = true
        LIMIT 1;
    """, (patient_id, household_id, patient_id))
    pcp = cur.fetchone()

    cur.close()
    conn.close()

    # Heart Rate now goes through the real engine (run_hr_analysis via the
    # cache) instead of the older analyze_vital_series — independently
    # computed and gated on ITS OWN reading count, not BP's. SpO2 and
    # Temperature still use analyze_vital_series for now (no dedicated
    # spec/engine built for them yet), just with correctly independent
    # row fetches rather than ones filtered through BP.
    hr_analysis   = get_cached_or_compute_analysis(patient_id, household_id, "heart_rate", days)
    spo2_analysis = analyze_vital_series(list(spo2_rows), vital_type="spo2")
    temp_analysis = analyze_vital_series(list(temp_rows), vital_type="temp")

    pcp_name      = pcp[0] if pcp else None
    next_followup = pcp[1].strftime("%b %-d, %Y") if pcp and pcp[1] else None

    # BP's own insufficient-data state no longer blocks the rest of the
    # response — heart_rate/spo2/temperature/pcp info are included either
    # way, computed above before this check even runs.
    if bp_row_count < 7:
        return {
            "status": "insufficient_data",
            "reading_count": bp_row_count,
            "readings_needed": 7,
            "message": (
                f"You have {bp_row_count} BP reading{'s' if bp_row_count != 1 else ''} in this period. "
                "Keep recording daily — analysis unlocks after 7 readings. "
                "The more consistent you are, the more accurate your trends become."
            ),
            "heart_rate":    hr_analysis,
            "spo2":          spo2_analysis,
            "temperature":   temp_analysis,
            "pcp_name":      pcp_name,
            "next_followup": next_followup,
        }

    bp = get_cached_or_compute_analysis(patient_id, household_id, "blood_pressure", days)

    return {
        "status":         "ok",
        "reading_count":  bp["reading_count"],
        "days":           int(days),
        "systolic":       bp["systolic"],
        "diastolic":      bp["diastolic"],
        "burden":         bp["burden"],
        "hypo_burden":    bp["hypo_burden"],
        "classification": bp["classification"],
        "map":            bp["map"],
        "sbp_burden":     bp["sbp_burden"],
        "ttr":            bp["ttr"],
        "dbp_burden":     bp["dbp_burden"],
        "low_dbp_burden": bp["low_dbp_burden"],
        "heart_rate":     hr_analysis,
        "spo2":           spo2_analysis,
        "temperature":    temp_analysis,
        "pcp_name":       pcp_name,
        "next_followup":  next_followup,
    }

# --------------------
# PATIENTS ENDPOINTS
# --------------------
@app.get("/api/patients", response_model=list[PatientOut])
def list_patients(
    household_id: str = Depends(get_household_id),
    auth: dict = Depends(get_auth),
    exclude_self_claimed: bool = False,
):
    conn = get_conn()
    cur = conn.cursor()

    caller_user_id = auth.get("sub") if auth.get("type") != "api_key" else None

    if exclude_self_claimed:
        # Used specifically by the Join flow's "attach to an existing
        # patient" screen — a patient that ANY user has already claimed
        # with relationship='self' should never appear as an option for
        # someone else to attach to. Otherwise a second user could
        # mistakenly confirm "this is me" on a patient that's already
        # someone else's own identity.
        cur.execute("""
            SELECT p.patient_id, p.first_name, p.last_name, p.dob, p.gender
            FROM patients p
            WHERE p.household_id = %s
              AND NOT EXISTS (
                  SELECT 1 FROM patient_users pu
                  WHERE pu.patient_id = p.patient_id AND pu.relationship = 'self'
              )
            ORDER BY p.first_name
        """, (household_id,))
    else:
        # Two accounts sharing a household (via invite) both see the same
        # patient list — plain alphabetical order has no idea which
        # patient, if any, corresponds to the person actually asking.
        # Prioritizing a patient_users 'self' match for the calling user
        # means the client's existing "default to the first patient in
        # the list" fallback (PatientStateService.InitializeAsync)
        # naturally lands on the right patient for whoever's logged in,
        # without any client-side change.
        cur.execute("""
            SELECT p.patient_id, p.first_name, p.last_name, p.dob, p.gender
            FROM patients p
            LEFT JOIN patient_users pu
                ON pu.patient_id = p.patient_id
                AND pu.user_id = %s
                AND pu.relationship = 'self'
            WHERE p.household_id = %s
            ORDER BY (pu.patient_id IS NULL), p.first_name
        """, (caller_user_id, household_id))

    rows = cur.fetchall()
    cur.close()
    conn.close()
    return [
        {"patient_id": r[0], "first_name": r[1], "last_name": r[2], "dob": r[3], "gender": r[4]}
        for r in rows
    ]

@app.post("/api/patients", response_model=PatientOut)
def create_patient(
    p: PatientCreate,
    household_id: str = Depends(get_household_id),
    auth: dict = Depends(get_auth),
):
    conn = get_conn()
    cur = conn.cursor()

    cur.execute("SELECT patient_limit FROM households WHERE household_id = %s", (household_id,))
    row = cur.fetchone()
    # 2 is the safe fallback if a household somehow has no limit set at all
    # (shouldn't happen post-migration — patient_limit is NOT NULL with a
    # default — but better to fail safe than let an edge case go unlimited).
    patient_limit = row[0] if row and row[0] is not None else 2

    cur.execute("SELECT COUNT(*) FROM patients WHERE household_id = %s", (household_id,))
    current_count = cur.fetchone()[0]
    if current_count >= patient_limit:
        cur.close()
        conn.close()
        raise HTTPException(
            status_code=403,
            detail=f"You've reached your plan's limit of {patient_limit} patient{'s' if patient_limit != 1 else ''}. Upgrade to add more."
        )

    cur.execute("""
        INSERT INTO patients (first_name, last_name, dob, gender, household_id)
        VALUES (%s, %s, %s, %s, %s)
        RETURNING patient_id, first_name, last_name, dob, gender
    """, (p.first_name, p.last_name, p.dob, p.gender, household_id))
    row = cur.fetchone()

    # Record who created this patient and how they relate to them. Not an
    # access-control mechanism — household-wide access is still the model —
    # just relationship metadata (see PatientCreate.relationship) so a
    # future feature (primary caregiver, per-patient notifications, a real
    # visibility-restriction mode) has real data to work from instead of
    # needing a backfill migration first. Only meaningful for real
    # JWT-authenticated users; the legacy API-key path (Home Assistant) has
    # no actual user_id to link, so it's skipped there.
    if auth.get("type") != "api_key":
        creating_user_id = auth.get("sub")
        if creating_user_id:
            cur.execute("""
                INSERT INTO patient_users (patient_id, user_id, relationship, created_at)
                VALUES (%s, %s, %s, now())
            """, (row[0], creating_user_id, p.relationship or "caregiver"))
    conn.commit()
    cur.close()
    conn.close()
    return {"patient_id": row[0], "first_name": row[1], "last_name": row[2], "dob": row[3], "gender": row[4]}


@app.post("/api/patients/{patient_id}/claim")
def claim_patient(
    patient_id: str,
    household_id: str = Depends(get_household_id),
    auth: dict = Depends(get_auth),
):
    """
    Links the calling user to an existing patient as 'self' — this is what
    "attach to an existing patient" during Join actually does now,
    called only after the user has confirmed (via the DOB/gender
    verification prompt) that the patient really is them. Re-checks
    server-side that the patient isn't already self-claimed, rather than
    trusting the client's earlier exclude_self_claimed list fetch — closes
    the gap where two people could otherwise both attempt to claim the
    same patient moments apart.
    """
    if auth.get("type") == "api_key":
        raise HTTPException(status_code=401, detail="This requires a signed-in account")

    user_id = auth.get("sub")
    if not user_id:
        raise HTTPException(status_code=401, detail="This requires a signed-in account")

    conn = get_conn()
    cur = conn.cursor()
    try:
        cur.execute(
            "SELECT 1 FROM patients WHERE patient_id = %s AND household_id = %s",
            (patient_id, household_id)
        )
        if not cur.fetchone():
            raise HTTPException(status_code=404, detail="Patient not found in your household")

        cur.execute(
            "SELECT 1 FROM patient_users WHERE patient_id = %s AND relationship = 'self'",
            (patient_id,)
        )
        if cur.fetchone():
            raise HTTPException(
                status_code=409,
                detail="This patient has already been claimed by someone else. Please choose another."
            )

        cur.execute("""
            INSERT INTO patient_users (patient_id, user_id, relationship, created_at)
            VALUES (%s, %s, 'self', now())
        """, (patient_id, user_id))
        conn.commit()
        return {"status": "claimed"}
    except HTTPException:
        conn.rollback()
        raise
    except Exception as e:
        conn.rollback()
        raise HTTPException(status_code=500, detail=f"Claim error: {e}")
    finally:
        cur.close()
        conn.close()

@app.get("/api/patients_wrapped")
def list_patients_wrapped(household_id: str = Depends(get_household_id)):
    conn = get_conn()
    cur = conn.cursor()
    cur.execute("""
        SELECT patient_id, first_name, last_name, dob, gender
        FROM patients
        WHERE household_id = %s
        ORDER BY first_name
    """, (household_id,))
    rows = cur.fetchall()
    cur.close()
    conn.close()
    return {
        "patients": [
            {"patient_id": r[0], "first_name": r[1], "last_name": r[2], "dob": r[3], "gender": r[4]}
            for r in rows
        ]
    }

# --------------------
# MEDICATION ENDPOINTS
# --------------------
@app.post("/api/medications")
async def create_medication(
    request: Request,
    m: MedicationCreate,
    x_api_key: str = Header(...),
    household_id: str = Depends(get_household_id),
    auth: dict = Depends(get_auth)
):
    raw = await request.json()
    print("RAW PAYLOAD FROM HA:", raw)
    check_key(x_api_key)

    est_refill = None
    if m.fill_date and m.days_supply:
        est_refill = calculate_refill(m.fill_date, m.days_supply, m.time_of_day)

    # Editable in the UI, defaults to today there — not silently derived
    # from created_at, since logging a medication weeks after actually
    # starting it is the normal case for a manually-tracked medication.
    start_date = m.start_date or date.today()

    conn = get_conn()
    cur = conn.cursor()
    verify_patient_household(cur, str(m.patient_id), household_id)
    time_of_day = [t.lower() for t in m.time_of_day] if m.time_of_day else None

    cur.execute("""
        INSERT INTO medications (
            patient_id, name, dosage, time_of_day, qty, days_supply,
            fill_date, est_refill, prescribing_doctor_id, is_active,
            household_id, rxotc, purpose, start_date
        )
        VALUES (%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s)
        RETURNING medication_id;
    """, (
        str(m.patient_id), m.name, m.dosage, time_of_day,
        m.qty, m.days_supply, m.fill_date, est_refill,
        str(m.prescribing_doctor_id) if m.prescribing_doctor_id else None,
        m.is_active, household_id, m.rxotc or "rx", m.purpose, start_date
    ))
    med_id = cur.fetchone()[0]

    # No changed_by_user_id for the legacy API-key path (Home Assistant)
    # — there's no real user_id to attribute it to, same pattern already
    # used for patient_users. Home Assistant no longer touches
    # medications at all in practice, but the endpoint still technically
    # accepts the legacy auth path, so this stays defensive.
    changed_by = auth.get("sub") if auth.get("type") != "api_key" else None
    cur.execute("""
        INSERT INTO medication_changes (
            medication_id, patient_id, household_id, change_type,
            new_value, effective_date, changed_by_user_id
        )
        VALUES (%s, %s, %s, 'started', %s, %s, %s);
    """, (med_id, str(m.patient_id), household_id, m.name, start_date, changed_by))

    conn.commit()
    cur.close()
    conn.close()
    return {"status": "success", "medication_id": med_id, "est_refill": est_refill}

@app.get("/api/medications")
def get_medications(
    patient_id: str,
    x_api_key: str = Header(...),
    household_id: str = Depends(get_household_id)
):
    if patient_id in (None, "", "unknown"):
        return {"medications": []}
    check_key(x_api_key)
    conn = get_conn()
    cur = conn.cursor()
    verify_patient_household(cur, patient_id, household_id)
    # No discontinued/is_active filter here anymore — the client's own
    # "hide inactive" toggle (MedicationsViewModel.HideInactiveMedications)
    # already exists and correctly filters on is_active, but could never
    # actually show inactive medications before this: the old
    # `discontinued = false` filter meant they never reached the client
    # in the first place, regardless of the toggle's state.
    cur.execute("""
        SELECT m.medication_id, m.patient_id, m.name, m.dosage, m.time_of_day,
               m.qty, m.days_supply, m.est_refill, m.fill_date,
               m.prescribing_doctor_id, d.name AS prescribing_doctor,
               m.rxotc, m.created_at, m.purpose, m.is_active,
               m.start_date, m.discontinued_date
        FROM medications m
        LEFT JOIN doctors d ON m.prescribing_doctor_id = d.doctor_id
        WHERE m.patient_id = %s AND m.household_id = %s
        ORDER BY m.name;
    """, (patient_id, household_id))
    rows = cur.fetchall()
    cur.close()
    conn.close()
    return {
        "medications": [
            {
                "medication_id": r[0], "patient_id": r[1], "name": r[2],
                "dosage": r[3], "time_of_day": r[4] or [], "qty": r[5],
                "days_supply": r[6], "est_refill": r[7], "fill_date": r[8],
                "prescribing_doctor_id": r[9], "prescribing_doctor": r[10],
                "rxotc": r[11], "created_at": r[12],
                "purpose": r[13], "is_active": r[14],
                "start_date": r[15], "discontinued_date": r[16]
            }
            for r in rows
        ]
    }

@app.patch("/api/medications/{medication_id}")
def update_medication(
    medication_id: UUID,
    m: MedicationUpdate,
    x_api_key: str = Header(...),
    household_id: str = Depends(get_household_id),
    auth: dict = Depends(get_auth)
):
    check_key(x_api_key)
    conn = get_conn()
    cur = conn.cursor()
    verify_child_record_household(cur, "medications", "medication_id", str(medication_id), household_id)

    # Fetch BEFORE values first — change detection (is_active flipping,
    # dosage/time_of_day actually differing) needs something to compare
    # the incoming payload against, not just what's being set.
    cur.execute("""
        SELECT patient_id, is_active, dosage, time_of_day
        FROM medications WHERE medication_id = %s;
    """, (str(medication_id),))
    before = cur.fetchone()
    if not before:
        cur.close()
        conn.close()
        return {"status": "not_found"}
    before_patient_id, before_active, before_dosage, before_tod = before

    payload = m.model_dump(exclude_unset=True)
    # change_effective_date is metadata for medication_changes, not a
    # medications column — must never reach the dynamic UPDATE below.
    change_effective_date = payload.pop("change_effective_date", None) or date.today()
    discontinued_date_sent = "discontinued_date" in payload

    updates = []
    values = []
    for field, value in payload.items():
        if field == "time_of_day" and isinstance(value, list):
            updates.append(f"{field} = %s")
            values.append(value)
        elif isinstance(value, UUID):
            updates.append(f"{field} = %s")
            values.append(str(value))
        else:
            updates.append(f"{field} = %s")
            values.append(value)

    # is_active flipping false->true (a restart) clears any prior
    # discontinued_date automatically — a medication resumed after being
    # paused shouldn't keep showing a stale "stopped" date. Only applies
    # when the client didn't already explicitly send its own
    # discontinued_date in this same call.
    if payload.get("is_active") is True and before_active is False and not discontinued_date_sent:
        updates.append("discontinued_date = %s")
        values.append(None)

    if not updates:
        cur.close()
        conn.close()
        return {"status": "no_changes"}

    values.append(str(medication_id))
    sql = f"UPDATE medications SET {', '.join(updates)} WHERE medication_id = %s RETURNING medication_id;"
    cur.execute(sql, tuple(values))
    result = cur.fetchone()

    # No changed_by_user_id for the legacy API-key path — see the same
    # note in create_medication.
    changed_by = auth.get("sub") if auth.get("type") != "api_key" else None

    def log_change(change_type, field_changed=None, old_value=None, new_value=None, effective_date=None):
        cur.execute("""
            INSERT INTO medication_changes (
                medication_id, patient_id, household_id, change_type,
                field_changed, old_value, new_value, effective_date, changed_by_user_id
            )
            VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s);
        """, (str(medication_id), str(before_patient_id), household_id, change_type,
              field_changed, old_value, new_value, effective_date, changed_by))

    # Stopped
    if payload.get("is_active") is False and before_active is not False:
        log_change("stopped", effective_date=payload.get("discontinued_date") or date.today())
    # Restarted
    if payload.get("is_active") is True and before_active is False:
        log_change("restarted", effective_date=change_effective_date)
    # Dosage changed
    if "dosage" in payload and payload["dosage"] != before_dosage:
        log_change("dosage_changed", field_changed="dosage",
                    old_value=before_dosage, new_value=payload["dosage"],
                    effective_date=change_effective_date)
    # Frequency (time_of_day) changed
    if "time_of_day" in payload and payload["time_of_day"] != (before_tod or []):
        log_change("frequency_changed", field_changed="time_of_day",
                    old_value=", ".join(before_tod) if before_tod else None,
                    new_value=", ".join(payload["time_of_day"]) if payload["time_of_day"] else None,
                    effective_date=change_effective_date)

    conn.commit()
    cur.close()
    conn.close()
    if not result:
        return {"status": "not_found"}
    return {"status": "success", "medication_id": result[0]}

@app.get("/api/medications/{patient_id}/pdf")
def export_medications_pdf(
    patient_id: UUID,
    days: int = Query(default=15),
    x_api_key: str = Header(..., alias="X-API-KEY"),
    household_id: str = Depends(get_household_id)
):
    check_key(x_api_key)

    buffer = BytesIO()
    pdf = canvas.Canvas(buffer, pagesize=LETTER)
    width, height = LETTER
    LEFT = 50
    RIGHT = width - 50
    USABLE_WIDTH = RIGHT - LEFT

    conn = get_conn()
    cur = conn.cursor()
    verify_patient_household(cur, str(patient_id), household_id)

    cur.execute("""
        SELECT first_name, last_name, dob
        FROM patients WHERE patient_id = %s AND household_id = %s;
    """, (str(patient_id), household_id))
    p = cur.fetchone()
    patient_name = f"{p[0]} {p[1]}" if p else "Unknown Patient"
    patient_dob  = p[2].strftime("%m/%d/%Y") if p and p[2] else "Unknown DOB"

    cur.execute("""
        SELECT recorded_at, systolic, diastolic, heart_rate, oxygen_saturation, temperature
        FROM vitals WHERE patient_id = %s AND household_id = %s
        ORDER BY recorded_at DESC LIMIT 1;
    """, (str(patient_id), household_id))
    latest = cur.fetchone()

    cur.execute("""
        SELECT recorded_at, systolic, diastolic, heart_rate, oxygen_saturation, temperature
        FROM vitals WHERE patient_id = %s AND household_id = %s
          AND recorded_at >= now() - interval '%s days'
        ORDER BY recorded_at DESC;
    """, (str(patient_id), household_id, days))
    history = cur.fetchall()

    cur.execute("""
        SELECT m.name, m.dosage, m.purpose, d.name AS prescribing_doctor, m.rxotc
        FROM medications m
        LEFT JOIN doctors d ON m.prescribing_doctor_id = d.doctor_id
        WHERE m.patient_id = %s AND m.household_id = %s AND m.discontinued = false
        ORDER BY m.name;
    """, (str(patient_id), household_id))
    meds = cur.fetchall()

    cur.execute("""
        SELECT d.name, d.specialty, d.phone, d.fax, d.email, d.address, d.notes, pd.is_primary
        FROM patient_doctors pd
        JOIN doctors d ON d.doctor_id = pd.doctor_id
        WHERE pd.patient_id = %s AND d.household_id = %s AND d.is_active = true
        ORDER BY pd.is_primary DESC, d.name;
    """, (str(patient_id), household_id))
    doctors = cur.fetchall()

    cur.execute("""
        SELECT allergen, allergy_type, reaction, severity, notes
        FROM allergies WHERE patient_id = %s AND household_id = %s AND is_active = true
        ORDER BY allergy_type, allergen;
    """, (str(patient_id), household_id))
    allergies = cur.fetchall()

    cur.execute("""
        SELECT recorded_at, systolic, diastolic, heart_rate, oxygen_saturation, temperature
        FROM vitals WHERE patient_id = %s AND household_id = %s
          AND recorded_at >= now() - interval '%s days'
        ORDER BY recorded_at;
    """, (str(patient_id), household_id, days))
    chart_data = cur.fetchall()

    cur.execute("""
        SELECT recorded_at, systolic, diastolic
        FROM vitals WHERE patient_id = %s AND household_id = %s
          AND systolic IS NOT NULL AND diastolic IS NOT NULL
          AND recorded_at >= now() - interval '%s days'
        ORDER BY recorded_at ASC;
    """, (str(patient_id), household_id, days))
    bp_analysis_rows = cur.fetchall()
    bp = run_bp_analysis(bp_analysis_rows)

    cur.execute("""
        SELECT recorded_at, heart_rate, oxygen_saturation, temperature
        FROM vitals WHERE patient_id = %s AND household_id = %s
          AND recorded_at >= now() - interval '%s days'
          AND (heart_rate IS NOT NULL OR oxygen_saturation IS NOT NULL OR temperature IS NOT NULL)
        ORDER BY recorded_at ASC;
    """, (str(patient_id), household_id, days))
    secondary_rows = cur.fetchall()

    spo2_rows_pdf = [(r[0], r[2]) for r in secondary_rows if r[2] is not None]
    temp_rows_pdf = [(r[0], float(r[3])) for r in secondary_rows if r[3] is not None]

    # Heart Rate now uses the real engine (run_hr_analysis via the
    # cache) — the same one powering the app's Analysis tab — instead
    # of analyze_vital_series. SpO2/Temperature stay on the older
    # function; no dedicated spec/engine built for them yet.
    hr_analysis = get_cached_or_compute_analysis(str(patient_id), household_id, "heart_rate", days)
    spo2_data = analyze_vital_series(spo2_rows_pdf, vital_type="spo2")
    temp_data = analyze_vital_series(temp_rows_pdf, vital_type="temp")

    spo2_class = None
    if spo2_data:
        c = spo2_data["classification"]
        spo2_class = {
            "normal":             ("Normal",             "#388e3c"),
            "mild_hypoxemia":     ("Mild Hypoxemia",     "#f57c00"),
            "moderate_hypoxemia": ("Moderate Hypoxemia", "#d32f2f"),
            "severe_hypoxemia":   ("Severe Hypoxemia",   "#7b1fa2"),
        }.get(c, ("Unknown", "#888888"))

    temp_class = None
    if temp_data:
        c = temp_data["classification"]
        temp_class = {
            "hypothermia":       ("Hypothermia",       "#1976d2"),
            "normal":            ("Normal",            "#388e3c"),
            "slightly_elevated": ("Slightly Elevated", "#f57c00"),
            "fever":             ("Fever",             "#d32f2f"),
            "high_fever":        ("High Fever",        "#7b1fa2"),
        }.get(c, ("Unknown", "#888888"))

    cur.close()
    conn.close()

    # =====================================================
    # PDF HELPERS
    # =====================================================
    def check_page_break(y, needed=80):
        if y < needed:
            pdf.showPage()
            return height - 50
        return y

    def wrap_text(text, col_width, fontsize=9):
        char_width = fontsize * 0.55
        max_chars = max(1, int((col_width - 8) / char_width))
        words = str(text or "").split()
        lines = []
        line = ""
        for word in words:
            test = (line + " " + word).strip()
            if len(test) <= max_chars:
                line = test
            else:
                if line:
                    lines.append(line)
                line = word
        if line:
            lines.append(line)
        return lines if lines else [""]

    def draw_table_row(y, cols, widths, fontsize=9, bold=False, fill_bg=False):
        line_height = 12
        pad = 4
        wrapped = [wrap_text(col, w, fontsize) for col, w in zip(cols, widths)]
        num_lines = max(len(lines) for lines in wrapped)
        row_height = num_lines * line_height + pad * 2
        x = LEFT
        if fill_bg:
            pdf.setFillColorRGB(0.85, 0.85, 0.85)
            pdf.rect(x, y - row_height + pad, USABLE_WIDTH, row_height, fill=1, stroke=0)
            pdf.setFillColorRGB(0, 0, 0)
        pdf.setFont("Helvetica-Bold" if bold else "Helvetica", fontsize)
        for lines, w in zip(wrapped, widths):
            pdf.rect(x, y - row_height + pad, w, row_height, fill=0, stroke=1)
            text_y = y - line_height + 2
            for line in lines:
                pdf.drawString(x + pad, text_y, line)
                text_y -= line_height
            x += w
        return y - row_height

    def draw_wrapped_line(y, text, fontsize=9, indent=0, line_spacing=13):
        char_width = fontsize * 0.55
        max_chars  = max(1, int((USABLE_WIDTH - indent) / char_width))
        words = text.split()
        line  = ""
        pdf.setFont("Helvetica", fontsize)
        for word in words:
            test = (line + " " + word).strip()
            if len(test) <= max_chars:
                line = test
            else:
                pdf.drawString(LEFT + indent, y, line)
                y -= line_spacing
                line = word
        if line:
            pdf.drawString(LEFT + indent, y, line)
            y -= line_spacing
        return y

    def build_clinical_summary(bp) -> list:
        if bp is None:
            return []

        sys = bp["systolic"]
        dia = bp["diastolic"]
        cls = bp["classification"]
        hb  = bp["hypo_burden"]
        b   = bp["burden"]
        sb  = bp["sbp_burden"]
        tt  = bp["ttr"]
        db  = bp["dbp_burden"]
        m   = bp["map"]
        paragraphs = []

        cls_display = {
            "hypotension":            "Hypotension",
            "borderline_hypotension": "Borderline Hypotension",
            "normal":                 "Normal",
            "elevated":               "Elevated",
            "stage1":                 "Stage 1 Hypertension",
            "stage2":                 "Stage 2 Hypertension",
        }.get(cls, cls.replace("_", " ").title())

        if cls == "hypotension":
            p1 = (
                f"Average BP {sys['avg']:.1f}/{dia['avg']:.1f} mmHg. "
                f"Classification: {cls_display}. "
                f"{hb['severe_pct']:.0f}% of readings fell below 80 mmHg systolic "
                f"and {hb['moderate_pct'] + hb['severe_pct']:.0f}% below 90 mmHg, "
                f"indicating persistent hypotension. "
                f"MAP {m['avg']:.1f} mmHg"
                + (" \u2014 below the 70 mmHg perfusion threshold." if m['avg'] < 70
                   else " (normal range 70\u2013100 mmHg).")
            )
        elif cls == "borderline_hypotension":
            p1 = (
                f"Average BP {sys['avg']:.1f}/{dia['avg']:.1f} mmHg. "
                f"Classification: {cls_display}. "
                f"{hb['moderate_pct'] + hb['severe_pct']:.0f}% of readings fell below "
                f"90 mmHg systolic, indicating intermittent hypotension. "
                f"MAP {m['avg']:.1f} mmHg (normal range 70\u2013100 mmHg)."
            )
        elif cls in ("stage1", "stage2"):
            p1 = (
                f"Average BP {sys['avg']:.1f}/{dia['avg']:.1f} mmHg. "
                f"Classification: {cls_display}. "
                f"{b['stage1_pct'] + b['stage2_pct']:.0f}% of readings were at or above "
                f"Stage 1 threshold (130 mmHg systolic). "
                f"MAP {m['avg']:.1f} mmHg"
                + (" \u2014 elevated above normal range." if m['avg'] > 100
                   else " (normal range 70\u2013100 mmHg).")
            )
        else:
            p1 = (
                f"Average BP {sys['avg']:.1f}/{dia['avg']:.1f} mmHg. "
                f"Classification: {cls_display}. "
                f"MAP {m['avg']:.1f} mmHg (normal range 70\u2013100 mmHg)."
            )
        paragraphs.append(p1)

        sys_dir = "+" if sys["slope"] >= 0 else ""
        sys_sig = "statistically significant" if sys["significant"] else "not statistically significant"
        paragraphs.append(
            f"Systolic trend: {sys['trend'].replace('_', ' ')} at "
            f"{sys_dir}{sys['slope']:.2f} mmHg/day "
            f"({sys_sig}, p={sys['p_value']:.3f}, R\u00b2={sys['r2']:.2f}). "
            f"Consistency: {sys['consistency']}. Momentum: {sys['momentum']}."
        )

        dia_dir = "+" if dia["slope"] >= 0 else ""
        dia_sig = "statistically significant" if dia["significant"] else "not statistically significant"
        paragraphs.append(
            f"Diastolic trend: {dia['trend'].replace('_', ' ')} at "
            f"{dia_dir}{dia['slope']:.2f} mmHg/day "
            f"({dia_sig}, p={dia['p_value']:.3f}, R\u00b2={dia['r2']:.2f}). "
            f"Consistency: {dia['consistency']}. Momentum: {dia['momentum']}."
        )

        if dia["significant"] and dia["slope"] > 0.2:
            momentum_note = ""
            if dia["momentum"] == "accelerating":
                momentum_note = " The rate of increase is accelerating."
            elif dia["momentum"] == "decelerating":
                momentum_note = " The rate of increase is decelerating."
            if not sys["significant"]:
                paragraphs.append(
                    f"Notable: diastolic pressure is rising at a statistically significant "
                    f"rate ({dia_dir}{dia['slope']:.2f} mmHg/day) while systolic remains "
                    f"stable. This systolic-diastolic divergence may reflect early isolated "
                    f"diastolic hypertension and warrants clinical attention.{momentum_note}"
                )
            else:
                paragraphs.append(
                    f"Notable: diastolic pressure is rising at a statistically significant "
                    f"rate ({dia_dir}{dia['slope']:.2f} mmHg/day).{momentum_note} "
                    f"This should be considered alongside the systolic trend."
                )
        elif dia["significant"] and dia["slope"] < -0.2:
            paragraphs.append(
                f"Diastolic pressure shows a statistically significant downward trend "
                f"({dia_dir}{dia['slope']:.2f} mmHg/day, p={dia['p_value']:.3f}) \u2014 "
                f"a favorable pattern if not accompanied by hypotensive symptoms."
            )

        if tt["pct"] >= 70:
            ttr_note = "BP consistency is within acceptable range."
        elif tt["pct"] >= 50:
            ttr_note = "BP consistency is below ideal; consider reviewing contributing factors."
        else:
            ttr_note = (
                f"BP consistency is notably low, with only {tt['pct']:.1f}% of time "
                f"within the 100\u2013130 mmHg target range. Contributing factors "
                f"including medication timing, hydration, and orthostatic symptoms "
                f"may warrant review."
            )

        if cls in ("hypotension", "borderline_hypotension"):
            paragraphs.append(
                f"SBP Time in Target Range (100\u2013130 mmHg): {tt['pct']:.1f}% "
                f"({tt['time_in_days']:.1f} of {tt['total_days']:.1f} days). "
                f"SBP Burden (AUC-weighted above 130 mmHg): {sb['pct']:.1f}% \u2014 "
                f"below-threshold readings dominate. {ttr_note}"
            )
        else:
            paragraphs.append(
                f"SBP Time in Target Range (100\u2013130 mmHg): {tt['pct']:.1f}% "
                f"({tt['time_in_days']:.1f} of {tt['total_days']:.1f} days). "
                f"SBP Burden (AUC-weighted above 130 mmHg): {sb['pct']:.1f}%. {ttr_note}"
            )

        if db["pct"] >= 25:
            paragraphs.append(
                f"Cumulative diastolic burden is elevated at {db['pct']:.1f}% of total "
                f"diastolic AUC above 80 mmHg ({db['annualized_mmhg_year']:.3f} mmHg\u00b7year). "
                f"Per Cho et al. (Hypertension 2024), elevated cumulative diastolic burden "
                f"independently predicts MACE in patients with normal systolic BP "
                f"(HR 1.06 per SD increase, p=0.037)."
            )
        elif db["pct"] >= 10:
            paragraphs.append(
                f"Cumulative diastolic burden: {db['pct']:.1f}% of total diastolic AUC "
                f"above 80 mmHg ({db['annualized_mmhg_year']:.3f} mmHg\u00b7year). "
                f"Ongoing monitoring recommended per Cho et al. (Hypertension 2024)."
            )
        else:
            paragraphs.append(
                f"Cumulative diastolic burden is low at {db['pct']:.1f}% of total "
                f"diastolic AUC above 80 mmHg ({db['annualized_mmhg_year']:.3f} mmHg\u00b7year) "
                f"\u2014 reassuring per Cho et al. (Hypertension 2024) risk stratification."
            )

        if cls == "hypotension":
            paragraphs.append(
                "Clinical impression: Persistent hypotension with recurrent sub-threshold "
                "readings. Orthostatic symptoms, medication review, and volume status "
                "assessment may be indicated."
            )
        elif cls == "borderline_hypotension":
            paragraphs.append(
                "Clinical impression: Borderline hypotension with below-target BP "
                "consistency. Pattern may warrant monitoring, particularly in the context "
                "of antihypertensive medications, autonomic dysfunction, or orthostatic "
                "symptoms. No acute intervention indicated based on trend data alone."
            )
        elif cls in ("stage1", "stage2"):
            if tt["pct"] >= 70 and sb["pct"] < 10:
                paragraphs.append(
                    f"Clinical impression: {cls_display} classification based on average, "
                    f"however BP burden is low ({sb['pct']:.1f}%) and time in target range "
                    f"is high ({tt['pct']:.1f}%), suggesting generally well-controlled "
                    f"pressure with occasional excursions. Continued monitoring recommended."
                )
            elif tt["pct"] >= 50:
                paragraphs.append(
                    f"Clinical impression: {cls_display} with moderate BP consistency "
                    f"(TTR {tt['pct']:.1f}%, burden {sb['pct']:.1f}%). "
                    f"Review of antihypertensive regimen and contributing lifestyle factors "
                    f"may be warranted."
                )
            else:
                paragraphs.append(
                    "Clinical impression: Sustained above-threshold systolic readings with "
                    "meaningful BP burden and low time in target range. Review of "
                    "antihypertensive regimen, sodium intake, and adherence is recommended."
                )
        else:
            paragraphs.append(
                "Clinical impression: BP within acceptable range with no statistically "
                "significant adverse trend. Continued monitoring recommended."
            )

        return paragraphs

    def build_hr_clinical_summary(hr) -> list:
        """
        Mirrors build_clinical_summary(bp) in depth and style, but for
        Heart Rate's actual data shape (run_hr_analysis via the cache —
        the same engine powering the app's Analysis tab). Covers every
        implemented section (§6.1-§6.12); each block is independently
        gated exactly as the underlying analysis is, so a paragraph only
        appears when there's real data behind it.

        Deliberately NOT modeled on BP's AUC/duration-weighted burden
        methodology for the rate-events block — spot heart-rate readings
        don't support a continuous-coverage assumption, so that section
        is framed explicitly as reading counts, not time-weighted burden.
        """
        if hr is None:
            return []

        paragraphs = []

        rs = hr.get("resting_summary")
        if rs:
            paragraphs.append(
                f"Resting heart rate averaged {rs['mean']:.0f} BPM (median {rs['median']:.0f}) "
                f"across {rs['n']} eligible resting readings over {rs['distinct_days']} "
                f"distinct days, ranging {rs['min']}\u2013{rs['max']} BPM."
            )
        else:
            paragraphs.append(
                f"Most recent heart rate: {hr.get('bpm', 'N/A')} BPM "
                f"({hr.get('activity_context', 'unknown')} context). Insufficient "
                f"resting-tagged readings for trend analysis at this time (requires "
                f"at least 3 resting readings across 3 distinct days)."
            )
            return paragraphs  # nothing further to report without a resting baseline

        disp = hr.get("dispersion")
        if disp:
            paragraphs.append(
                f"Reading-to-reading variability: SD {disp['sd']:.1f} BPM, "
                f"IQR {disp['iqr']:.1f} BPM (Q1 {disp['q1']:.1f}, Q3 {disp['q3']:.1f})."
            )

        dtrend = hr.get("dispersion_trend")
        if dtrend and dtrend.get("status") == "ok":
            direction = "narrowed" if dtrend["sd_delta"] < 0 else "widened"
            paragraphs.append(
                f"Variability has {direction} compared to the prior 30-day period "
                f"(SD change: {dtrend['sd_delta']:+.1f} BPM)."
            )

        trend = hr.get("trend")
        if trend:
            sig_note = ""
            if trend.get("p_value") is not None:
                sig_note = (
                    f", statistically significant (p={trend['p_value']:.3f})"
                    if trend["p_value"] < 0.05 else
                    f", not statistically significant (p={trend['p_value']:.3f})"
                )
            r2_note = (
                f", R\u00b2={trend['r2']:.2f} ({trend.get('consistency') or 'n/a'} consistency)."
                if trend.get("r2") is not None else "."
            )
            paragraphs.append(
                f"Resting-rate trend: {trend['trend_label'].replace('_', ' ')} at "
                f"{trend['slope_bpm_per_day']:+.2f} BPM/day over a "
                f"{trend['span_days']:.0f}-day span{sig_note}{r2_note}"
            )

        bd = hr.get("baseline_deviation")
        if bd:
            direction = "higher" if bd["delta_bpm"] >= 0 else "lower"
            pct_note = f" ({bd['delta_pct']:+.1f}%)." if bd.get("delta_pct") is not None else "."
            paragraphs.append(
                f"The most recent 7 days (median {bd['recent_median']:.0f} BPM) are "
                f"{direction} than the prior 30-day baseline (median "
                f"{bd['baseline_median']:.0f} BPM) by {abs(bd['delta_bpm']):.1f} BPM{pct_note}"
            )

        re_ = hr.get("rate_events")
        if re_ and re_.get("n_resting_in_window", 0) >= 1:
            th = re_.get("thresholds", {}) or {}
            high = re_.get("high", {}) or {}
            low = re_.get("low", {}) or {}
            parts = []
            for label, bucket, thresh_key, direction_word in [
                ("above", high, "high", "above"), ("below", low, "low", "below")
            ]:
                if bucket.get("count", 0) > 0:
                    detail = []
                    if bucket.get("pct") is not None:
                        detail.append(f"{bucket['pct']:.0f}% of readings")
                    if bucket.get("episode_count"):
                        detail.append(f"{bucket['episode_count']} distinct episode(s)")
                    detail_str = f" ({', '.join(detail)})" if detail else ""
                    parts.append(
                        f"{bucket['count']} reading(s) {label} {th.get(thresh_key)} BPM{detail_str}"
                    )
            if parts:
                paragraphs.append(
                    "Threshold events (reading counts, not duration-weighted \u2014 spot "
                    "measurements do not support a continuous-coverage burden calculation "
                    "the way BP's does): " + "; ".join(parts) + "."
                )
            if re_.get("marked_low_count", 0) > 0:
                paragraphs.append(
                    f"{re_['marked_low_count']} of the low reading(s) were markedly low "
                    f"(below {th.get('marked_low')} BPM) and warrant closer review."
                )

        tod = hr.get("time_of_day")
        if tod and tod.get("pattern_summary"):
            ps = tod["pattern_summary"]
            paragraphs.append(
                f"Time-of-day pattern: resting rate is highest during the "
                f"{ps['highest_period']} and lowest during the {ps['lowest_period']}."
            )

        cvc = hr.get("cross_vital_context") or {}
        cv_parts = []
        for label, key in [("blood pressure", "blood_pressure"),
                            ("oxygen saturation", "oxygen_saturation"),
                            ("temperature", "temperature")]:
            entry = cvc.get(key)
            if entry and (entry.get("co_occurrence_high", 0) > 0 or entry.get("co_occurrence_low", 0) > 0):
                cv_parts.append(
                    f"{label} was abnormal alongside {entry.get('co_occurrence_high', 0)} "
                    f"high and {entry.get('co_occurrence_low', 0)} low heart-rate reading(s)"
                )
        if cv_parts:
            paragraphs.append("Same-event context: " + "; ".join(cv_parts) + ".")

        sa = hr.get("symptom_association")
        if sa:
            sa_parts = []
            for tag, entry in sa.items():
                note = f"{tag} logged with {entry['associated_count']} reading(s)"
                if entry.get("pct_low") is not None:
                    note += f", {entry['pct_low']:.0f}% low"
                if entry.get("pct_high") is not None:
                    note += f", {entry['pct_high']:.0f}% high"
                sa_parts.append(note)
            if sa_parts:
                paragraphs.append("Symptom correlation: " + "; ".join(sa_parts) + ".")

        meds = hr.get("medication_associations")
        if meds:
            for m in meds:
                direction = "higher" if m["delta_bpm"] >= 0 else "lower"
                confound_note = (
                    " (another medication change occurred in this same window \u2014 "
                    "treat this comparison as less certain)" if m.get("confounded") else ""
                )
                paragraphs.append(
                    f"Following {m['change_type'].replace('_', ' ')} of "
                    f"{m['medication_name']} ({m['effective_date']}), resting rate was "
                    f"{abs(m['delta_bpm']):.1f} BPM {direction} in the following 14 days "
                    f"(median {m['post_median']:.0f} BPM) compared to the preceding 14 "
                    f"days (median {m['pre_median']:.0f} BPM){confound_note}."
                )

        ds = hr.get("data_support")
        if ds:
            coverage_note = (
                f", {ds['distinct_day_coverage_pct']:.0f}% of window days logged"
                if ds.get("distinct_day_coverage_pct") is not None else ""
            )
            paragraphs.append(
                f"Data confidence: {ds['support_state'].replace('_', ' ')} "
                f"({ds['n']} readings across {ds['distinct_days']} days{coverage_note})."
            )

        return paragraphs

    # =====================================================
    # PAGE 1 — HEADER, VITALS, ALLERGIES, MEDS, CARE TEAM
    # =====================================================
    y = height - 50

    pdf.setFont("Helvetica-Bold", 18)
    pdf.drawString(LEFT, y, "Vitals & Medication Summary")
    y -= 25

    pdf.setFont("Helvetica", 12)
    from datetime import datetime as dt
    pdf.drawString(LEFT, y,
        f"{patient_name}   |   DOB: {patient_dob}   |   "
        f"Generated: {dt.utcnow().strftime('%m/%d/%Y')}")
    y -= 30

    pdf.setFont("Helvetica-Bold", 13)
    pdf.drawString(LEFT, y, "Most Recent Vitals")
    y -= 18

    conn2 = get_conn()
    cur2 = conn2.cursor()
    cur2.execute("""
        SELECT round(avg(systolic),1), round(avg(diastolic),1), round(avg(heart_rate),1),
               round(avg(oxygen_saturation),1), round(avg(temperature),1)
        FROM vitals WHERE patient_id = %s AND household_id = %s
          AND recorded_at >= now() - interval '%s days'
    """, (str(patient_id), household_id, days))
    avg = cur2.fetchone()
    cur2.close()
    conn2.close()

    DIVIDER_X = 310

    if latest:
        taken, sys, dia, hr, spo2, temp = latest
        avg_sys, avg_dia, avg_hr, avg_spo2, avg_temp = avg if avg else (None,)*5

        pdf.setFont("Helvetica-Bold", 9)
        pdf.drawString(LEFT, y, "Latest")
        pdf.drawString(DIVIDER_X + 10, y, f"{days}-Day Average")
        y -= 14

        pdf.setFont("Helvetica", 10)
        pdf.drawString(LEFT, y, f"Taken: {taken.strftime('%m/%d/%Y %I:%M %p')}")
        y -= 14
        pdf.drawString(LEFT,           y, f"BP: {sys}/{dia} mmHg")
        pdf.drawString(220,            y, f"Heart Rate: {hr} BPM")
        pdf.drawString(DIVIDER_X + 10, y, f"BP: {avg_sys}/{avg_dia} mmHg")
        pdf.drawString(460,            y, f"HR: {avg_hr} BPM")
        y -= 14
        pdf.drawString(LEFT,           y, f"O2 Saturation: {spo2}%")
        pdf.drawString(220,            y, f"Temperature: {temp} F")
        pdf.drawString(DIVIDER_X + 10, y, f"O2 Saturation: {avg_spo2}%")
        pdf.drawString(460,            y, f"Temp: {avg_temp} F")
        y -= 25
    else:
        pdf.setFont("Helvetica", 10)
        pdf.drawString(LEFT, y, "No vitals recorded.")
        y -= 25

    y = check_page_break(y, needed=60)
    pdf.setFont("Helvetica-Bold", 13)
    pdf.drawString(LEFT, y, "Allergies")
    y -= 18

    if allergies:
        al_widths  = [130, 100, 150, 80, 52]
        al_headers = ["Allergen", "Type", "Reaction", "Severity", "Notes"]
        y = draw_table_row(y, al_headers, al_widths, bold=True, fill_bg=True)
        for a in allergies:
            allergen, allergy_type, reaction, severity, notes = a
            y = check_page_break(y, needed=40)
            y = draw_table_row(
                y,
                [allergen, (allergy_type or "").capitalize(), reaction or "",
                 (severity or "").capitalize(), notes or ""],
                al_widths
            )
    else:
        pdf.setFont("Helvetica", 9)
        pdf.drawString(LEFT, y, "No known allergies.")
        y -= 16

    y -= 20
    pdf.setFont("Helvetica-Bold", 13)
    pdf.drawString(LEFT, y, "Medications")
    y -= 18

    col_widths = [120, 100, 115, 120, 57]
    headers = ["Name", "Dosage", "Purpose", "Prescribing Doctor", "Rx/OTC"]
    y = draw_table_row(y, headers, col_widths, bold=True, fill_bg=True)

    if meds:
        for row in meds:
            name, dosage, purpose, prescribing_doctor, rxotc = row
            y = check_page_break(y, needed=80)
            y = draw_table_row(
                y,
                [name, dosage or "", purpose or "", prescribing_doctor or "",
                 (rxotc or "").upper()],
                col_widths
            )
    else:
        y -= 5
        pdf.setFont("Helvetica", 9)
        pdf.drawString(LEFT, y, "No active medications.")
        y -= 16

    y -= 20
    y = check_page_break(y, needed=80)
    pdf.setFont("Helvetica-Bold", 13)
    pdf.drawString(LEFT, y, "Care Team")
    y -= 20

    for doc in doctors:
        doc_name, specialty, phone, fax, email, address, notes, is_primary = doc
        y = check_page_break(y, needed=80)
        pdf.setFont("Helvetica-Bold", 11)
        pdf.drawString(LEFT, y, doc_name + ("  (PCP)" if is_primary else ""))
        y -= 14
        pdf.setStrokeColorRGB(0.6, 0.6, 0.6)
        pdf.line(LEFT, y + 4, RIGHT, y + 4)
        pdf.setStrokeColorRGB(0, 0, 0)
        y -= 8
        col1_x = LEFT
        col2_x = LEFT + int(USABLE_WIDTH // 2)
        col_top = y
        left_y  = col_top
        for label_text, value in [
            ("Specialty:", specialty), ("Phone:", phone), ("Fax:", fax),
            ("Email:", email), ("Notes:", notes),
        ]:
            if value:
                pdf.setFont("Helvetica-Bold", 9)
                pdf.drawString(col1_x, left_y, label_text)
                pdf.setFont("Helvetica", 9)
                max_chars = int((USABLE_WIDTH // 2 - 58) / (9 * 0.55))
                words = value.split()
                line = ""
                for word in words:
                    test = (line + " " + word).strip()
                    if len(test) <= max_chars:
                        line = test
                    else:
                        pdf.drawString(col1_x + 55, left_y, line)
                        left_y -= 12
                        line = word
                if line:
                    pdf.drawString(col1_x + 55, left_y, line)
                    left_y -= 13
        right_y = col_top
        if address:
            pdf.setFont("Helvetica-Bold", 9)
            pdf.drawString(col2_x, right_y, "Address:")
            right_y -= 13
            pdf.setFont("Helvetica", 9)
            max_chars = int((USABLE_WIDTH // 2 - 8) / (9 * 0.55))
            words = address.split()
            line = ""
            for word in words:
                test = (line + " " + word).strip()
                if len(test) <= max_chars:
                    line = test
                else:
                    pdf.drawString(col2_x, right_y, line)
                    right_y -= 12
                    line = word
            if line:
                pdf.drawString(col2_x, right_y, line)
                right_y -= 12
        y = min(left_y, right_y) - 18

    # =====================================================
    # PAGE 2 — VITAL TREND CHARTS
    # =====================================================
    if chart_data:
        pdf.showPage()
        y = height - 50
        pdf.setFont("Helvetica-Bold", 13)
        pdf.drawString(LEFT, y, f"Vital Trends (Last {days} Days)")
        y -= 20

        dates     = [r[0] for r in chart_data]
        sys_vals  = [r[1] for r in chart_data]
        dia_vals  = [r[2] for r in chart_data]
        hr_vals   = [r[3] for r in chart_data]
        spo2_vals = [r[4] for r in chart_data]
        temp_vals = [float(r[5]) if r[5] else None for r in chart_data]

        def make_chart(title, datasets, ylabel, chart_width=480, chart_height=160):
            fig, ax = plt.subplots(figsize=(chart_width/72, chart_height/72))
            for label, values, color in datasets:
                paired = [(d, v) for d, v in zip(dates, values) if v is not None]
                if not paired:
                    continue
                d_clean, v_clean = zip(*paired)
                d_clean = list(d_clean)
                v_clean = list(v_clean)
                ax.scatter(d_clean, v_clean, color=color, alpha=0.25, s=14, zorder=2)
                if len(v_clean) >= 4:
                    x_ord = np.array([d.toordinal() for d in d_clean], dtype=float)
                    y_arr = np.array(v_clean, dtype=float)
                    sort_idx = np.argsort(x_ord)
                    y_loess = loess_smooth(x_ord[sort_idx], y_arr[sort_idx], frac=0.4)
                    ax.plot([d_clean[i] for i in sort_idx], y_loess,
                            color=color, linewidth=2.0, zorder=3, label=label)
                else:
                    ax.plot(d_clean, v_clean, color=color, linewidth=1.5,
                            marker='o', markersize=3, zorder=3, label=label)
            ax.set_title(title, fontsize=10, fontweight='bold')
            ax.set_ylabel(ylabel, fontsize=8)
            ax.xaxis.set_major_formatter(mdates.DateFormatter('%m/%d'))
            ax.xaxis.set_major_locator(mdates.AutoDateLocator())
            plt.xticks(fontsize=7, rotation=45)
            plt.yticks(fontsize=7)
            ax.legend(fontsize=7)
            ax.grid(True, alpha=0.3)
            plt.tight_layout()
            buf = BytesIO()
            fig.savefig(buf, format='png', dpi=120)
            plt.close(fig)
            buf.seek(0)
            return buf

        chart_w = 480
        chart_h = 160

        bp_buf = make_chart(
            "Blood Pressure (mmHg)",
            [("Systolic", sys_vals, "#d32f2f"), ("Diastolic", dia_vals, "#1976d2")],
            "mmHg"
        )
        pdf.drawImage(ImageReader(bp_buf), LEFT, y - chart_h, width=chart_w, height=chart_h)
        y -= chart_h + 20

        y = check_page_break(y, needed=chart_h + 20)
        hr_buf = make_chart("Heart Rate (BPM)", [("Heart Rate", hr_vals, "#388e3c")], "BPM")
        pdf.drawImage(ImageReader(hr_buf), LEFT, y - chart_h, width=chart_w, height=chart_h)
        y -= chart_h + 20

        y = check_page_break(y, needed=chart_h + 20)
        spo2_buf = make_chart("Oxygen Saturation (%)", [("SpO2", spo2_vals, "#7b1fa2")], "%")
        pdf.drawImage(ImageReader(spo2_buf), LEFT, y - chart_h, width=chart_w, height=chart_h)
        y -= chart_h + 20

        y = check_page_break(y, needed=chart_h + 20)
        temp_buf = make_chart("Temperature (F)", [("Temp", temp_vals, "#f57c00")], "F")
        pdf.drawImage(ImageReader(temp_buf), LEFT, y - chart_h, width=chart_w, height=chart_h)

    # =====================================================
    # PAGE 3 — VITALS ANALYSIS
    # =====================================================
    if bp is not None:
        pdf.showPage()
        y = height - 50

        pdf.setFont("Helvetica-Bold", 13)
        pdf.drawString(LEFT, y, f"Vitals Analysis (Last {days} Days)")
        y -= 20

        pdf.setFont("Helvetica-Bold", 11)
        pdf.drawString(LEFT, y, "Clinical Summary")
        y -= 4
        pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
        pdf.line(LEFT, y, RIGHT, y)
        pdf.setStrokeColorRGB(0, 0, 0)
        y -= 12

        for para in build_clinical_summary(bp):
            y = check_page_break(y, needed=40)
            y = draw_wrapped_line(y, para, fontsize=9, indent=0, line_spacing=13)
            y -= 6

        y -= 6
        pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
        pdf.line(LEFT, y, RIGHT, y)
        pdf.setStrokeColorRGB(0, 0, 0)
        y -= 14

        pdf.setFont("Helvetica-Bold", 11)
        pdf.drawString(LEFT, y, "Detailed Metrics")
        y -= 14

        classification_display = {
            "hypotension":            "Hypotension",
            "borderline_hypotension": "Borderline Hypotension",
            "normal":                 "Normal",
            "elevated":               "Elevated",
            "stage1":                 "Stage 1 Hypertension",
            "stage2":                 "Stage 2 Hypertension",
        }.get(bp["classification"], bp["classification"].replace("_", " ").title())

        pdf.setFont("Helvetica-Bold", 10)
        pdf.drawString(LEFT, y, "Classification:")
        pdf.setFont("Helvetica", 10)
        pdf.drawString(LEFT + 90, y, classification_display)
        y -= 14

        pdf.setFont("Helvetica-Bold", 10)
        pdf.drawString(LEFT, y, "Readings analyzed:")
        pdf.setFont("Helvetica", 10)
        pdf.drawString(LEFT + 120, y, str(bp["reading_count"]))
        y -= 14

        map_avg   = bp["map"]["avg"]
        map_range = "Normal" if 70 <= map_avg <= 100 else \
                    "Low \u2014 risk of hypoperfusion" if map_avg < 70 else "Elevated"
        pdf.setFont("Helvetica-Bold", 10)
        pdf.drawString(LEFT, y, "Mean Arterial Pressure:")
        pdf.setFont("Helvetica", 10)
        pdf.drawString(LEFT + 150, y,
            f"{map_avg:.1f} mmHg  ({map_range})  |  Normal: 70\u2013100 mmHg  |  "
            f"Formula: (SBP + 2\u00d7DBP) / 3")
        y -= 20

        pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
        pdf.line(LEFT, y, RIGHT, y)
        pdf.setStrokeColorRGB(0, 0, 0)
        y -= 14

        sys = bp["systolic"]
        sys_dir = "+" if sys["slope"] >= 0 else ""
        sig_sys = " (statistically significant)" if sys["p_value"] < 0.05 \
                  else " (not statistically significant)"
        pdf.setFont("Helvetica-Bold", 10)
        pdf.drawString(LEFT, y, "Systolic Blood Pressure Trend")
        y -= 14
        pdf.setFont("Helvetica", 9)
        pdf.drawString(LEFT + 10, y,
            f"Trend: {sys['trend'].replace('_', ' ').title()}   |   "
            f"Rate of change: {sys_dir}{sys['slope']} mmHg/day{sig_sys}")
        y -= 12
        pdf.drawString(LEFT + 10, y,
            f"Consistency ({sys['consistency'].title()}, R\u00b2={sys['r2']})   |   "
            f"p-value: {sys['p_value']}   |   Momentum: {sys['momentum'].title()}")
        y -= 20

        dia = bp["diastolic"]
        dia_dir = "+" if dia["slope"] >= 0 else ""
        sig_dia = " (statistically significant)" if dia["p_value"] < 0.05 \
                  else " (not statistically significant)"
        pdf.setFont("Helvetica-Bold", 10)
        pdf.drawString(LEFT, y, "Diastolic Blood Pressure Trend")
        y -= 14
        pdf.setFont("Helvetica", 9)
        pdf.drawString(LEFT + 10, y,
            f"Trend: {dia['trend'].replace('_', ' ').title()}   |   "
            f"Rate of change: {dia_dir}{dia['slope']} mmHg/day{sig_dia}")
        y -= 12
        pdf.drawString(LEFT + 10, y,
            f"Consistency ({dia['consistency'].title()}, R\u00b2={dia['r2']})   |   "
            f"p-value: {dia['p_value']}   |   Momentum: {dia['momentum'].title()}")
        y -= 20

        pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
        pdf.line(LEFT, y, RIGHT, y)
        pdf.setStrokeColorRGB(0, 0, 0)
        y -= 14

        if bp["classification"] in ("hypotension", "borderline_hypotension"):
            hb = bp["hypo_burden"]
            pdf.setFont("Helvetica-Bold", 10)
            pdf.drawString(LEFT, y, "Low BP Burden \u2014 Time Spent in Each Range")
            y -= 14
            hypo_widths  = [130, 120, 120, 142]
            hypo_headers = ["Range", "Normal (>=90)", "Low (80-89)", "Severe (<80)"]
            y = draw_table_row(y, hypo_headers, hypo_widths, bold=True, fill_bg=True)
            y = draw_table_row(
                y,
                ["% of readings", f"{hb['normal_pct']}%",
                 f"{hb['moderate_pct']}%", f"{hb['severe_pct']}%"],
                hypo_widths
            )
        else:
            b = bp["burden"]
            pdf.setFont("Helvetica-Bold", 10)
            pdf.drawString(LEFT, y, "BP Burden \u2014 Time Spent in Each Range")
            y -= 14
            burden_widths  = [130, 100, 100, 100, 82]
            burden_headers = ["Range", "Normal (<120)", "Elevated (120-129)",
                              "Stage 1 (130-139)", "Stage 2 (>=140)"]
            y = draw_table_row(y, burden_headers, burden_widths, bold=True, fill_bg=True)
            y = draw_table_row(
                y,
                ["% of readings", f"{b['normal_pct']}%", f"{b['elevated_pct']}%",
                 f"{b['stage1_pct']}%", f"{b['stage2_pct']}%"],
                burden_widths
            )
        y -= 20

        y = check_page_break(y, needed=80)
        pdf.setFont("Helvetica-Bold", 10)
        pdf.drawString(LEFT, y, "SBP Burden & Time in Target Range (SPRINT Methodology)")
        y -= 14
        sb = bp["sbp_burden"]
        tt = bp["ttr"]
        pdf.setFont("Helvetica", 9)
        pdf.drawString(LEFT + 10, y,
            f"SBP Burden: {sb['pct']:.1f}%   "
            f"(AUC above 130: {sb['auc_above_130']:.1f} mmHg\u00b7day   |   "
            f"Time above 130: {sb['time_above_pct']:.1f}%   |   "
            f"Weighted excess proportion: {sb['prop_above']:.1f}%)")
        y -= 12
        pdf.drawString(LEFT + 10, y,
            f"SBP TTR: {tt['pct']:.1f}%   "
            f"({tt['time_in_days']:.1f} of {tt['total_days']:.1f} days in target "
            f"100\u2013130 mmHg   |   Rosendaal linear interpolation approximation)")
        y -= 12
        pdf.setFont("Helvetica-Oblique", 8)
        pdf.setFillColorRGB(0.4, 0.4, 0.4)
        pdf.drawString(LEFT + 10, y,
            "Burden = (Sa / [Sa+Sb]) \u00d7 (T1 / [T1+T2+T3])  where Sa = AUC above 130, "
            "T1 = time above 130, T2 = TTR, T3 = time below 100.")
        y -= 10
        pdf.drawString(LEFT + 10, y,
            "Reference: Wang et al. / SPRINT supplementary materials. "
            "Target range: 100\u2013130 mmHg (AHA guideline).")
        pdf.setFillColorRGB(0, 0, 0)
        y -= 20

        y = check_page_break(y, needed=70)
        pdf.setFont("Helvetica-Bold", 10)
        pdf.drawString(LEFT, y, "Cumulative Diastolic BP Burden (Cho et al. 2024 Methodology)")
        y -= 14
        db = bp["dbp_burden"]
        pdf.setFont("Helvetica", 9)
        pdf.drawString(LEFT + 10, y,
            f"Absolute burden: {db['annualized_mmhg_year']:.3f} mmHg\u00b7year   |   "
            f"Proportional: {db['pct']:.1f}% of total DBP AUC   |   "
            f"Time above 80 mmHg: {db['time_above_pct']:.1f}%")
        y -= 12
        pdf.drawString(LEFT + 10, y,
            f"AUC above 80 mmHg (re-zeroed at threshold): "
            f"{db['auc_above_80']:.3f} mmHg\u00b7days   |   "
            f"Total DBP AUC: {db['total_dia_auc']:.1f} mmHg\u00b7days")
        y -= 12
        pdf.setFont("Helvetica-Oblique", 8)
        pdf.setFillColorRGB(0.4, 0.4, 0.4)
        pdf.drawString(LEFT + 10, y,
            "Methodology: AUC of (DBP \u2212 80) where DBP \u2265 80 mmHg, "
            "re-zeroed at threshold, annualized to mmHg\u00b7year.")
        y -= 10
        pdf.drawString(LEFT + 10, y,
            "Reference: Cho et al. Hypertension. 2024;81:273\u2013281. "
            "DOI: 10.1161/HYPERTENSIONAHA.123.22160.")
        pdf.setFillColorRGB(0, 0, 0)
        y -= 20

        pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
        pdf.line(LEFT, y, RIGHT, y)
        pdf.setStrokeColorRGB(0, 0, 0)
        y -= 14

        # =====================================================
        # HEART RATE — CLINICAL ANALYSIS
        # Mirrors the Blood Pressure section above in depth (Clinical
        # Summary + Detailed Metrics), using hr_analysis — the same
        # run_hr_analysis engine powering the app's Analysis tab — not
        # the older analyze_vital_series output SpO2/Temperature below
        # still use. Rate-events methodology is deliberately NOT
        # AUC/duration-weighted like BP's burden — spot readings don't
        # support that continuous-coverage assumption (see the note
        # drawn with that block below).
        # =====================================================
        if hr_analysis is not None:
            y = check_page_break(y, needed=200)
            pdf.setFont("Helvetica-Bold", 13)
            pdf.drawString(LEFT, y, "Heart Rate Clinical Analysis")
            y -= 20

            pdf.setFont("Helvetica-Bold", 11)
            pdf.drawString(LEFT, y, "Clinical Summary")
            y -= 4
            pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
            pdf.line(LEFT, y, RIGHT, y)
            pdf.setStrokeColorRGB(0, 0, 0)
            y -= 12

            for para in build_hr_clinical_summary(hr_analysis):
                y = check_page_break(y, needed=40)
                y = draw_wrapped_line(y, para, fontsize=9, indent=0, line_spacing=13)
                y -= 6

            y -= 6
            pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
            pdf.line(LEFT, y, RIGHT, y)
            pdf.setStrokeColorRGB(0, 0, 0)
            y -= 14

            rs = hr_analysis.get("resting_summary")
            if rs:
                pdf.setFont("Helvetica-Bold", 11)
                pdf.drawString(LEFT, y, "Detailed Metrics")
                y -= 14

                pdf.setFont("Helvetica-Bold", 10)
                pdf.drawString(LEFT, y, "Readings analyzed:")
                pdf.setFont("Helvetica", 10)
                pdf.drawString(LEFT + 130, y, f"{rs['n']} ({rs['distinct_days']} distinct days)")
                y -= 14

                pdf.setFont("Helvetica-Bold", 10)
                pdf.drawString(LEFT, y, "Resting mean / median:")
                pdf.setFont("Helvetica", 10)
                pdf.drawString(LEFT + 150, y,
                    f"{rs['mean']:.0f} / {rs['median']:.0f} BPM  (range {rs['min']}\u2013{rs['max']})")
                y -= 20

                disp = hr_analysis.get("dispersion")
                if disp:
                    y = check_page_break(y, needed=50)
                    pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
                    pdf.line(LEFT, y, RIGHT, y)
                    pdf.setStrokeColorRGB(0, 0, 0)
                    y -= 14
                    pdf.setFont("Helvetica-Bold", 10)
                    pdf.drawString(LEFT, y, "Variability")
                    y -= 14
                    pdf.setFont("Helvetica", 9)
                    pdf.drawString(LEFT + 10, y,
                        f"SD: {disp['sd']:.1f} BPM   |   IQR: {disp['iqr']:.1f} BPM "
                        f"(Q1={disp['q1']:.1f}, Q3={disp['q3']:.1f})")
                    y -= 20

                trend = hr_analysis.get("trend")
                if trend:
                    y = check_page_break(y, needed=60)
                    pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
                    pdf.line(LEFT, y, RIGHT, y)
                    pdf.setStrokeColorRGB(0, 0, 0)
                    y -= 14
                    pdf.setFont("Helvetica-Bold", 10)
                    pdf.drawString(LEFT, y, "Resting-Rate Trend")
                    y -= 14
                    pdf.setFont("Helvetica", 9)
                    p_display = f"{trend['p_value']:.3f}" if trend.get("p_value") is not None else "n/a (insufficient span/n)"
                    r2_display = f"{trend['r2']:.2f}" if trend.get("r2") is not None else "n/a"
                    pdf.drawString(LEFT + 10, y,
                        f"Trend: {trend['trend_label'].replace('_', ' ').title()}   |   "
                        f"Rate: {trend['slope_bpm_per_day']:+.2f} BPM/day   |   "
                        f"Span: {trend['span_days']:.0f} days")
                    y -= 12
                    pdf.drawString(LEFT + 10, y,
                        f"p-value: {p_display}   |   R\u00b2: {r2_display}   |   "
                        f"Consistency: {trend.get('consistency') or 'n/a'}")
                    y -= 20

                bd = hr_analysis.get("baseline_deviation")
                if bd:
                    y = check_page_break(y, needed=60)
                    pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
                    pdf.line(LEFT, y, RIGHT, y)
                    pdf.setStrokeColorRGB(0, 0, 0)
                    y -= 14
                    pdf.setFont("Helvetica-Bold", 10)
                    pdf.drawString(LEFT, y, "Personal Baseline Comparison")
                    y -= 14
                    pdf.setFont("Helvetica", 9)
                    pdf.drawString(LEFT + 10, y,
                        f"Prior 30-day baseline: {bd['baseline_median']:.0f} BPM (n={bd['baseline_n']})   |   "
                        f"Recent 7 days: {bd['recent_median']:.0f} BPM (n={bd['recent_n']})")
                    y -= 12
                    z_note = f"   |   z-score: {bd['z_score']:.2f} (clinician reference only)" if bd.get("z_score") is not None else ""
                    pct_note = f" ({bd['delta_pct']:+.1f}%)" if bd.get("delta_pct") is not None else ""
                    pdf.drawString(LEFT + 10, y, f"Change: {bd['delta_bpm']:+.1f} BPM{pct_note}{z_note}")
                    y -= 20

                re_ = hr_analysis.get("rate_events")
                if re_ and re_.get("n_resting_in_window", 0) >= 1:
                    y = check_page_break(y, needed=70)
                    pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
                    pdf.line(LEFT, y, RIGHT, y)
                    pdf.setStrokeColorRGB(0, 0, 0)
                    y -= 14
                    pdf.setFont("Helvetica-Bold", 10)
                    pdf.drawString(LEFT, y, "Threshold Events (Reading Counts)")
                    y -= 14
                    th = re_.get("thresholds", {}) or {}
                    high = re_.get("high", {}) or {}
                    low = re_.get("low", {}) or {}
                    pdf.setFont("Helvetica", 9)
                    high_pct = f", {high['pct']:.0f}%" if high.get("pct") is not None else ""
                    low_pct = f", {low['pct']:.0f}%" if low.get("pct") is not None else ""
                    pdf.drawString(LEFT + 10, y,
                        f"Above {th.get('high')} BPM: {high.get('count', 0)} reading(s){high_pct}   |   "
                        f"Below {th.get('low')} BPM: {low.get('count', 0)} reading(s){low_pct}")
                    y -= 12
                    pdf.setFont("Helvetica-Oblique", 8)
                    pdf.setFillColorRGB(0.4, 0.4, 0.4)
                    pdf.drawString(LEFT + 10, y,
                        "Methodology: discrete reading counts against fixed thresholds \u2014 "
                        "not AUC/duration-weighted (spot measurements do not support a "
                        "continuous-coverage assumption).")
                    pdf.setFillColorRGB(0, 0, 0)
                    y -= 20

                meds = hr_analysis.get("medication_associations")
                if meds:
                    y = check_page_break(y, needed=40 + 24 * len(meds))
                    pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
                    pdf.line(LEFT, y, RIGHT, y)
                    pdf.setStrokeColorRGB(0, 0, 0)
                    y -= 14
                    pdf.setFont("Helvetica-Bold", 10)
                    pdf.drawString(LEFT, y, "Medication-Change Associations")
                    y -= 14
                    pdf.setFont("Helvetica", 9)
                    for m in meds:
                        y = check_page_break(y, needed=24)
                        confound_flag = "  [CONFOUNDED \u2014 another change occurred nearby]" if m.get("confounded") else ""
                        pdf.drawString(LEFT + 10, y,
                            f"{m['medication_name']} \u2014 {m['change_type'].replace('_', ' ').title()} "
                            f"({m['effective_date']}): {m['pre_median']:.0f} \u2192 "
                            f"{m['post_median']:.0f} BPM ({m['delta_bpm']:+.1f}){confound_flag}")
                        y -= 12
                    y -= 8

            y -= 6
            pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
            pdf.line(LEFT, y, RIGHT, y)
            pdf.setStrokeColorRGB(0, 0, 0)
            y -= 14

        def draw_secondary_section(y, title, data, class_tuple, unit, normal_range,
                                   burden_headers, burden_keys):
            y = check_page_break(y, needed=110)
            pdf.setFont("Helvetica-Bold", 10)
            pdf.drawString(LEFT, y, title)
            y -= 14

            if data is None:
                pdf.setFont("Helvetica", 9)
                pdf.setFillColorRGB(0.5, 0.5, 0.5)
                pdf.drawString(LEFT + 10, y,
                    "Insufficient data for analysis (minimum 7 readings required).")
                pdf.setFillColorRGB(0, 0, 0)
                y -= 18
                return y

            label, _ = class_tuple
            slope_str = f"+{data['slope']}" if data['slope'] >= 0 else str(data['slope'])
            sig_str   = " (significant)" if data['significant'] else " (not significant)"
            consist   = data['consistency'].title()
            trend_str = data['trend'].replace('_', ' ').title()

            pdf.setFont("Helvetica-Bold", 9)
            pdf.drawString(LEFT + 10, y, "Classification:")
            pdf.setFont("Helvetica", 9)
            pdf.drawString(LEFT + 90, y, label)
            y -= 12

            pdf.setFont("Helvetica-Bold", 9)
            pdf.drawString(LEFT + 10, y, "Average:")
            pdf.setFont("Helvetica", 9)
            pdf.drawString(LEFT + 90, y, f"{data['avg']:,.1f} {unit}   (normal: {normal_range})")
            y -= 12

            pdf.setFont("Helvetica-Bold", 9)
            pdf.drawString(LEFT + 10, y, "Trend:")
            pdf.setFont("Helvetica", 9)
            pdf.drawString(LEFT + 90, y, f"{trend_str}  \u2014  {slope_str} {unit}/day{sig_str}")
            y -= 12

            pdf.setFont("Helvetica-Bold", 9)
            pdf.drawString(LEFT + 10, y, "Consistency:")
            pdf.setFont("Helvetica", 9)
            pdf.drawString(LEFT + 90, y,
                f"{consist} (R\u00b2={data['r2']:.2f})   |   "
                f"p-value: {data['p_value']:.3f}   |   n={data['reading_count']}")
            y -= 14

            if data.get("burden") and burden_headers and burden_keys:
                col_w      = USABLE_WIDTH / len(burden_headers)
                col_widths = [col_w] * len(burden_headers)
                y = draw_table_row(y, burden_headers, col_widths, bold=True, fill_bg=True)
                values = [f"{data['burden'].get(k, 0.0):.1f}%" for k in burden_keys]
                y = draw_table_row(y, values, col_widths)

            y -= 14
            return y

        y = draw_secondary_section(
            y, "Oxygen Saturation (SpO2)", spo2_data,
            spo2_class or ("Unknown", "#888888"),
            "%", ">=95%",
            burden_headers=["Normal (>=95%)", "Mild Hypoxemia (92-94%)",
                            "Moderate (88-91%)", "Severe (<88%)"],
            burden_keys=["normal_pct", "mild_hypoxemia_pct",
                         "moderate_hypoxemia_pct", "severe_hypoxemia_pct"]
        )

        pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
        pdf.line(LEFT, y, RIGHT, y)
        pdf.setStrokeColorRGB(0, 0, 0)
        y -= 14

        y = draw_secondary_section(
            y, "Temperature", temp_data, temp_class or ("Unknown", "#888888"),
            "F", "96.8-98.9F",
            burden_headers=["Hypothermia (<96.8)", "Normal (96.8-98.9)",
                            "Elevated (99-100.3)", "Fever (100.4-103)", "High Fever (>103)"],
            burden_keys=["hypothermia_pct", "normal_pct", "elevated_pct",
                         "fever_pct", "high_fever_pct"]
        )

        y -= 10
        y = check_page_break(y, needed=40)
        pdf.setFont("Helvetica-Oblique", 8)
        pdf.setFillColorRGB(0.4, 0.4, 0.4)
        pdf.drawString(LEFT, y,
            "Note: Trend analysis uses OLS linear regression. "
            "R\u00b2 indicates consistency of readings (0=variable, 1=consistent).")
        y -= 10
        pdf.drawString(LEFT, y,
            "p<0.05 indicates the trend is statistically significant. "
            "Burden calculated via linear interpolation (Rosendaal method approximation).")
        y -= 10
        pdf.drawString(LEFT, y,
            "SBP Burden: Wang et al./SPRINT supplementary materials. "
            "DBP Burden: Cho et al. Hypertension 2024;81:273\u2013281.")
        pdf.setFillColorRGB(0, 0, 0)

    # =====================================================
    # PAGE 4 — HISTORICAL VITALS TABLE
    # =====================================================
    pdf.showPage()
    y = height - 50
    pdf.setFont("Helvetica-Bold", 13)
    pdf.drawString(LEFT, y, f"Historical Vitals (Last {days} Days)")
    y -= 25

    v_widths  = [80, 88, 88, 88, 80, 88]
    v_headers = ["Date", "Systolic", "Diastolic", "Heart Rate", "SpO2", "Temp (F)"]
    y = draw_table_row(y, v_headers, v_widths, bold=True, fill_bg=True)

    if history:
        for v in history:
            taken, sys, dia, hr, spo2, temp = v
            y = check_page_break(y, needed=80)
            y = draw_table_row(
                y,
                [taken.strftime("%m/%d/%Y"), str(sys), str(dia),
                 f"{hr} BPM", f"{spo2}%", str(temp)],
                v_widths
            )
    else:
        y -= 5
        pdf.setFont("Helvetica", 9)
        pdf.drawString(LEFT, y, "No vitals recorded in the last 15 days.")

    pdf.save()
    buffer.seek(0)

    return StreamingResponse(
        buffer,
        media_type="application/pdf",
        headers={"Content-Disposition": "attachment; filename=care-summary.pdf"}
    )


@app.get("/api/medications/{patient_id}/schedule-pdf")
def export_medication_schedule_pdf(
    patient_id: UUID,
    x_api_key: str = Header(..., alias="X-API-KEY"),
    household_id: str = Depends(get_household_id)
):
    """
    Medications-only PDF, organized by time of day (Morning / Midday /
    Evening / Night / Other) rather than a flat alphabetical list. A
    medication taken more than once a day (e.g. morning and night)
    appears under EVERY relevant section, not just once — the point is a
    quick reference for whoever's actually administering doses, not a
    catalog. Standalone for now (not yet linked from the Medications
    screen) — same auth/styling conventions as the full care-summary PDF.
    """
    check_key(x_api_key)

    buffer = BytesIO()
    pdf = canvas.Canvas(buffer, pagesize=LETTER)
    width, height = LETTER
    LEFT = 50
    RIGHT = width - 50
    USABLE_WIDTH = RIGHT - LEFT

    conn = get_conn()
    cur = conn.cursor()
    verify_patient_household(cur, str(patient_id), household_id)

    cur.execute("""
        SELECT first_name, last_name, dob
        FROM patients WHERE patient_id = %s AND household_id = %s;
    """, (str(patient_id), household_id))
    p = cur.fetchone()
    patient_name = f"{p[0]} {p[1]}" if p else "Unknown Patient"
    patient_dob = p[2].strftime("%m/%d/%Y") if p and p[2] else "Unknown DOB"

    cur.execute("""
        SELECT name, dosage, time_of_day, purpose, rxotc
        FROM medications
        WHERE patient_id = %s AND household_id = %s AND discontinued = false
        ORDER BY name;
    """, (str(patient_id), household_id))
    meds = cur.fetchall()
    cur.close()
    conn.close()

    # =====================================================
    # PDF HELPERS (same conventions as export_medications_pdf)
    # =====================================================
    def check_page_break(y, needed=80):
        if y < needed:
            pdf.showPage()
            return height - 50
        return y

    def wrap_text(text, col_width, fontsize=9):
        char_width = fontsize * 0.55
        max_chars = max(1, int((col_width - 8) / char_width))
        words = str(text or "").split()
        lines = []
        line = ""
        for word in words:
            test = (line + " " + word).strip()
            if len(test) <= max_chars:
                line = test
            else:
                if line:
                    lines.append(line)
                line = word
        if line:
            lines.append(line)
        return lines if lines else [""]

    def draw_table_row(y, cols, widths, fontsize=9, bold=False, fill_bg=False):
        line_height = 12
        pad = 4
        wrapped = [wrap_text(col, w, fontsize) for col, w in zip(cols, widths)]
        num_lines = max(len(lines) for lines in wrapped)
        row_height = num_lines * line_height + pad * 2
        x = LEFT
        if fill_bg:
            pdf.setFillColorRGB(0.85, 0.85, 0.85)
            pdf.rect(x, y - row_height + pad, USABLE_WIDTH, row_height, fill=1, stroke=0)
            pdf.setFillColorRGB(0, 0, 0)
        pdf.setFont("Helvetica-Bold" if bold else "Helvetica", fontsize)
        for lines, w in zip(wrapped, widths):
            pdf.rect(x, y - row_height + pad, w, row_height, fill=0, stroke=1)
            text_y = y - line_height + 2
            for line in lines:
                pdf.drawString(x + pad, text_y, line)
                text_y -= line_height
            x += w
        return y - row_height

    # =====================================================
    # HEADER
    # =====================================================
    y = height - 50
    pdf.setFont("Helvetica-Bold", 18)
    pdf.drawString(LEFT, y, "Medication Schedule")
    y -= 24
    pdf.setFont("Helvetica", 12)
    pdf.drawString(LEFT, y, f"{patient_name}  |  DOB: {patient_dob}")
    y -= 16
    pdf.setFont("Helvetica", 9)
    pdf.setFillColorRGB(0.4, 0.4, 0.4)
    pdf.drawString(LEFT, y, f"Generated {datetime.now().strftime('%m/%d/%Y')}")
    pdf.setFillColorRGB(0, 0, 0)
    y -= 26

    # =====================================================
    # GROUP BY TIME OF DAY — a med with multiple times appears in
    # every relevant section, matching how the app's own Medications
    # screen groups them (MedicationsViewModel.ApplyFilterAndGroup).
    # =====================================================
    sections = [
        ("Morning", "morning"),
        ("Midday", "midday"),
        ("Evening", "evening"),
        ("Night", "night"),
    ]
    col_widths = [140, 100, 150, 60]
    headers = ["Name", "Dosage", "Purpose", "Rx/OTC"]

    any_section_printed = False

    for label, key in sections:
        matches = [m for m in meds if m[2] and key in m[2]]
        if not matches:
            continue

        any_section_printed = True
        y = check_page_break(y, needed=100)
        pdf.setFont("Helvetica-Bold", 13)
        pdf.drawString(LEFT, y, label)
        y -= 18
        y = draw_table_row(y, headers, col_widths, bold=True, fill_bg=True)

        for name, dosage, time_of_day, purpose, rxotc in matches:
            y = check_page_break(y, needed=80)
            y = draw_table_row(
                y,
                [name, dosage or "", purpose or "", (rxotc or "").upper()],
                col_widths
            )
        y -= 16

    # Anything with no time_of_day at all, or a value outside the four
    # standard slots — shown rather than silently dropped, so nothing a
    # patient actually takes goes missing from the reference sheet.
    other = [m for m in meds if not m[2] or not any(k in m[2] for _, k in sections)]
    if other:
        any_section_printed = True
        y = check_page_break(y, needed=100)
        pdf.setFont("Helvetica-Bold", 13)
        pdf.drawString(LEFT, y, "Other / As Needed")
        y -= 18
        y = draw_table_row(y, headers, col_widths, bold=True, fill_bg=True)
        for name, dosage, time_of_day, purpose, rxotc in other:
            y = check_page_break(y, needed=80)
            y = draw_table_row(
                y,
                [name, dosage or "", purpose or "", (rxotc or "").upper()],
                col_widths
            )

    if not any_section_printed:
        pdf.setFont("Helvetica", 10)
        pdf.drawString(LEFT, y, "No active medications on record.")

    pdf.save()
    buffer.seek(0)

    return StreamingResponse(
        buffer,
        media_type="application/pdf",
        headers={"Content-Disposition": "attachment; filename=medication-schedule.pdf"}
    )


# --------------------
# DOCTORS ENDPOINTS
# --------------------
@app.get("/api/doctors")
def get_doctors(
    patient_id: str,
    active_only: bool = True,
    x_api_key: str = Header(...),
    household_id: str = Depends(get_household_id)
):
    check_key(x_api_key)
    if patient_id in (None, "", "unknown", "00000000-0000-0000-0000-000000000000"):
        return {"doctors": []}
    try:
        UUID(patient_id)
    except Exception:
        return {"doctors": []}

    conn = get_conn()
    cur = conn.cursor()
    verify_patient_household(cur, patient_id, household_id)
    active_filter = "AND d.is_active = true" if active_only else ""
    cur.execute(f"""
        SELECT d.doctor_id, d.name, d.specialty, d.phone, d.fax,
               d.email, d.address, d.notes, d.is_active, pd.is_primary
        FROM patient_doctors pd
        JOIN doctors d ON d.doctor_id = pd.doctor_id
        WHERE pd.patient_id = %s AND d.household_id = %s {active_filter}
        ORDER BY pd.is_primary DESC, d.name;
    """, (str(patient_id), household_id))
    rows = cur.fetchall()
    cur.close()
    conn.close()
    return {
        "doctors": [
            {
                "doctor_id": str(r[0]), "name": r[1], "specialty": r[2],
                "phone": r[3], "fax": r[4], "email": r[5], "address": r[6],
                "notes": r[7], "is_active": r[8], "is_primary": r[9]
            }
            for r in rows
        ]
    }

@app.get("/api/doctors/household")
def get_household_doctors(
    x_api_key: str = Header(...),
    household_id: str = Depends(get_household_id)
):
    check_key(x_api_key)
    conn = get_conn()
    cur = conn.cursor()
    cur.execute("""
        SELECT doctor_id, name, specialty, phone, fax,
               email, address, notes, is_active, created_at
        FROM doctors WHERE household_id = %s AND is_active = true ORDER BY name;
    """, (household_id,))
    rows = cur.fetchall()
    cur.close()
    conn.close()
    return {
        "doctors": [
            {
                "doctor_id": str(r[0]), "name": r[1], "specialty": r[2],
                "phone": r[3], "fax": r[4], "email": r[5], "address": r[6],
                "notes": r[7], "is_active": r[8], "created_at": r[9]
            }
            for r in rows
        ]
    }

@app.post("/api/doctors")
def create_doctor(
    payload: DoctorCreate,
    x_api_key: str = Header(...),
    household_id: str = Depends(get_household_id)
):
    check_key(x_api_key)
    conn = get_conn()
    cur = conn.cursor()
    try:
        cur.execute("SELECT 1 FROM patients WHERE patient_id = %s AND household_id = %s",
                    (str(payload.patient_id), household_id))
        if not cur.fetchone():
            raise HTTPException(status_code=404, detail="Patient not found.")

        cur.execute("SELECT doctor_id FROM doctors WHERE household_id = %s AND lower(name) = lower(%s)",
                    (household_id, payload.name))
        existing = cur.fetchone()

        if existing:
            doctor_id = existing[0]
        else:
            cur.execute("""
                INSERT INTO doctors (household_id, name, specialty, phone, fax,
                                     email, address, notes, created_at, updated_at)
                VALUES (%s,%s,%s,%s,%s,%s,%s,%s,now(),now()) RETURNING doctor_id;
            """, (household_id, payload.name, payload.specialty, payload.phone,
                  payload.fax, payload.email, payload.address, payload.notes))
            doctor_id = cur.fetchone()[0]

        if payload.is_primary:
            cur.execute("UPDATE patient_doctors SET is_primary = false WHERE patient_id = %s",
                        (str(payload.patient_id),))

        cur.execute("""
            INSERT INTO patient_doctors (patient_id, doctor_id, is_primary, relationship_notes, created_at)
            VALUES (%s,%s,%s,%s,now())
            ON CONFLICT (patient_id, doctor_id) DO UPDATE
                SET is_primary = EXCLUDED.is_primary,
                    relationship_notes = EXCLUDED.relationship_notes
        """, (str(payload.patient_id), str(doctor_id), payload.is_primary, payload.relationship_notes))
        conn.commit()
        return {"doctor_id": str(doctor_id), "message": "Doctor created and linked successfully."}
    except Exception:
        conn.rollback()
        raise
    finally:
        cur.close()
        conn.close()

@app.patch("/api/doctors/{doctor_id}")
def update_doctor(
    doctor_id: UUID,
    doctor: DoctorUpdate,
    x_api_key: str = Header(...),
    household_id: str = Depends(get_household_id)
):
    check_key(x_api_key)
    fields = doctor.dict(exclude_unset=True)
    if not fields:
        return {"message": "No fields to update."}
    conn = get_conn()
    cur = conn.cursor()
    try:
        doctor_fields = {k: v for k, v in fields.items()
                         if k in ["name", "specialty", "phone", "fax", "email", "address", "notes", "is_active"]}
        if doctor_fields:
            set_clause = ", ".join([f"{k} = %s" for k in doctor_fields])
            values = list(doctor_fields.values()) + [str(doctor_id), household_id]
            cur.execute(f"UPDATE doctors SET {set_clause}, updated_at = now() WHERE doctor_id = %s AND household_id = %s",
                        tuple(values))

        relationship_fields = {k: v for k, v in fields.items() if k in ["is_primary", "relationship_notes"]}
        if relationship_fields:
            patient_id = str(fields["patient_id"])
            verify_patient_household(cur, patient_id, household_id)
            if relationship_fields.get("is_primary") is True:
                cur.execute("UPDATE patient_doctors SET is_primary = false WHERE patient_id = %s", (patient_id,))
            set_clause = ", ".join([f"{k} = %s" for k in relationship_fields])
            values = list(relationship_fields.values()) + [patient_id, str(doctor_id)]
            cur.execute(f"UPDATE patient_doctors SET {set_clause} WHERE patient_id = %s AND doctor_id = %s",
                        tuple(values))

        conn.commit()
        return {"message": "Doctor updated successfully.", "doctor_id": str(doctor_id)}
    except Exception:
        conn.rollback()
        raise
    finally:
        cur.close()
        conn.close()

@app.post("/api/patient_doctors")
def link_doctor_to_patient(
    link: PatientDoctorCreate,
    x_api_key: str = Header(...),
    household_id: str = Depends(get_household_id)
):
    check_key(x_api_key)
    conn = get_conn()
    cur = conn.cursor()
    try:
        verify_patient_household(cur, str(link.patient_id), household_id)
        cur.execute("SELECT 1 FROM doctors WHERE doctor_id = %s AND household_id = %s",
                    (str(link.doctor_id), household_id))
        if not cur.fetchone():
            raise HTTPException(status_code=404, detail="Doctor not found in your household")

        cur.execute("""
            INSERT INTO patient_doctors (patient_id, doctor_id, is_primary, relationship_notes)
            VALUES (%s,%s,%s,%s)
            ON CONFLICT (patient_id, doctor_id) DO UPDATE
                SET is_primary = EXCLUDED.is_primary,
                    relationship_notes = EXCLUDED.relationship_notes;
        """, (link.patient_id, link.doctor_id, link.is_primary, link.relationship_notes))
        conn.commit()
    except HTTPException:
        conn.rollback()
        raise
    except Exception as e:
        conn.rollback()
        raise HTTPException(status_code=400, detail=str(e))
    finally:
        cur.close()
        conn.close()
    return {"status": "success"}

@app.delete("/api/patient_doctors")
def unlink_doctor_from_patient(
    patient_id: str,
    doctor_id: str,
    x_api_key: str = Header(...),
    household_id: str = Depends(get_household_id)
):
    check_key(x_api_key)
    conn = get_conn()
    cur = conn.cursor()
    verify_patient_household(cur, patient_id, household_id)
    cur.execute("DELETE FROM patient_doctors WHERE patient_id = %s AND doctor_id = %s",
                (patient_id, doctor_id))
    conn.commit()
    cur.close()
    conn.close()
    return {"status": "success"}

@app.post("/api/set-doctor")
def set_selected_doctor(name: str, token: str = Query(...)):
    if token != "ha":
        raise HTTPException(status_code=401, detail="Invalid token")
    import requests
    try:
        requests.post(
            "http://localhost:8123/api/services/input_select/select_option",
            headers={"Authorization": f"Bearer {HA_LONG_LIVED_TOKEN}", "Content-Type": "application/json"},
            json={"entity_id": "input_select.edit_doctor", "option": name},
            timeout=3
        )
    except Exception as e:
        print(f"set-doctor error: {e}")
    return {"status": "ok"}

@app.get("/doctor-list", response_class=HTMLResponse)
def doctor_list_page(patient_id: str, token: str = Query(...)):
    if token != "ha":
        raise HTTPException(status_code=401, detail="Invalid token")

    html = f"""<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>Doctors</title>
  <style>
    * {{ box-sizing: border-box; margin: 0; padding: 0; }}
    body {{ font-family: 'Roboto', sans-serif; background: transparent; padding: 4px; }}
    .doctor-btn {{
      display: block; width: 100%; padding: 10px 14px; margin-bottom: 4px;
      border-radius: 6px; border: 1px solid #e0e0e0; background: white;
      text-align: left; font-size: 14px; cursor: pointer;
      transition: background 0.15s, color 0.15s; color: #212121;
    }}
    .doctor-btn:hover {{ background: #e8f0fe; }}
    .doctor-btn.selected {{ background: #1976d2; color: white; border-color: #1976d2; }}
    .doctor-btn.primary {{ font-weight: bold; }}
    .status {{ font-size: 12px; color: #888; padding: 4px; }}
  </style>
</head>
<body>
  <div id="list"><div class="status">Loading...</div></div>
  <script>
    const PATIENT_ID = "{patient_id}";
    const API_BASE = "http://192.168.68.116:8000";
    const API_KEY = "kris_jessica_vitals_2026_secret";
    let selectedName = "";
    async function loadDoctors() {{
      try {{
        const res = await fetch(`${{API_BASE}}/api/doctors?patient_id=${{PATIENT_ID}}&active_only=true`,
          {{ headers: {{ "X-API-KEY": API_KEY }} }});
        const data = await res.json();
        render(data.doctors || []);
      }} catch(e) {{
        document.getElementById('list').innerHTML = '<div class="status">Error loading doctors.</div>';
      }}
    }}
    function render(doctors) {{
      const list = document.getElementById('list');
      list.innerHTML = doctors.map(doc => `
        <button class="doctor-btn ${{doc.is_primary ? 'primary' : ''}} ${{doc.name === selectedName ? 'selected' : ''}}"
          data-name="${{doc.name}}">${{doc.is_primary ? '🩺 ' : ''}}${{doc.name}}</button>
      `).join('');
      list.querySelectorAll('.doctor-btn').forEach(btn => {{
        btn.addEventListener('click', async () => {{
          selectedName = btn.dataset.name;
          list.querySelectorAll('.doctor-btn').forEach(b =>
            b.classList.toggle('selected', b.dataset.name === selectedName));
          await fetch(`${{API_BASE}}/api/set-doctor?name=${{encodeURIComponent(selectedName)}}&token=ha`,
            {{ method: 'POST' }});
        }});
      }});
    }}
    loadDoctors();
  </script>
</body>
</html>"""
    return HTMLResponse(content=html)

# =====================================================
# ALLERGY ENDPOINTS
# =====================================================
@app.get("/api/allergies")
def get_allergies(
    patient_id: str,
    active_only: bool = True,
    x_api_key: str = Header(..., alias="X-API-KEY"),
    household_id: str = Depends(get_household_id)
):
    check_key(x_api_key)
    try:
        UUID(patient_id)
    except:
        return {"allergies": []}
    conn = get_conn()
    cur = conn.cursor()
    verify_patient_household(cur, patient_id, household_id)
    query = """
        SELECT allergy_id, patient_id, allergen, allergy_type,
               reaction, severity, notes, is_active, created_at
        FROM allergies WHERE patient_id = %s AND household_id = %s
    """
    params = [patient_id, household_id]
    if active_only:
        query += " AND is_active = true"
    query += " ORDER BY allergy_type, allergen;"
    cur.execute(query, params)
    rows = cur.fetchall()
    cur.close()
    conn.close()
    return {
        "allergies": [
            {
                "allergy_id": str(r[0]), "patient_id": str(r[1]), "allergen": r[2],
                "allergy_type": r[3], "reaction": r[4], "severity": r[5],
                "notes": r[6], "is_active": r[7], "created_at": r[8].isoformat()
            }
            for r in rows
        ]
    }

@app.post("/api/allergies")
def create_allergy(
    allergy: AllergyCreate,
    x_api_key: str = Header(..., alias="X-API-KEY"),
    household_id: str = Depends(get_household_id)
):
    check_key(x_api_key)
    conn = get_conn()
    cur = conn.cursor()
    verify_patient_household(cur, str(allergy.patient_id), household_id)
    cur.execute("""
        INSERT INTO allergies (patient_id, household_id, allergen, allergy_type,
                               reaction, severity, notes, is_active)
        VALUES (%s,%s,%s,%s,%s,%s,%s,%s) RETURNING allergy_id;
    """, (str(allergy.patient_id), household_id, allergy.allergen, allergy.allergy_type,
          allergy.reaction, allergy.severity, allergy.notes, allergy.is_active))
    allergy_id = cur.fetchone()[0]
    conn.commit()
    cur.close()
    conn.close()
    return {"status": "success", "allergy_id": str(allergy_id)}

@app.patch("/api/allergies/{allergy_id}")
def update_allergy(
    allergy_id: UUID,
    updates: AllergyUpdate,
    x_api_key: str = Header(..., alias="X-API-KEY"),
    household_id: str = Depends(get_household_id)
):
    check_key(x_api_key)
    fields = {k: v for k, v in updates.dict().items() if v is not None}
    if not fields:
        return {"status": "no changes"}
    conn = get_conn()
    cur = conn.cursor()
    set_clause = ", ".join(f"{k} = %s" for k in fields)
    values = list(fields.values()) + [str(allergy_id), household_id]
    cur.execute(f"UPDATE allergies SET {set_clause} WHERE allergy_id = %s AND household_id = %s;", values)
    conn.commit()
    cur.close()
    conn.close()
    return {"status": "success"}

# =====================================================
# VISIT LOG ENDPOINTS
# =====================================================
@app.get("/api/visits")
def get_visits(
    patient_id: str,
    x_api_key: str = Header(...),
    household_id: str = Depends(get_household_id)
):
    check_key(x_api_key)
    conn = get_conn()
    cur = conn.cursor()
    verify_patient_household(cur, patient_id, household_id)
    cur.execute("""
        SELECT v.visit_id, v.patient_id, v.doctor_id, d.name AS doctor_name,
               v.visit_date, v.reason, v.notes, v.follow_up_date, v.created_at
        FROM visit_logs v
        LEFT JOIN doctors d ON v.doctor_id = d.doctor_id
        WHERE v.patient_id = %s AND v.household_id = %s AND v.is_active = true
        ORDER BY v.visit_date DESC;
    """, (patient_id, household_id))
    rows = cur.fetchall()
    cur.close()
    conn.close()
    return {
        "visits": [
            {
                "visit_id": str(r[0]), "patient_id": str(r[1]),
                "doctor_id": str(r[2]) if r[2] else None, "doctor_name": r[3],
                "visit_date": r[4].isoformat() if r[4] else None,
                "reason": r[5], "notes": r[6],
                "follow_up_date": r[7].isoformat() if r[7] else None,
                "created_at": r[8].isoformat() if r[8] else None
            }
            for r in rows
        ]
    }

@app.post("/api/visits")
def create_visit(
    visit: VisitCreate,
    x_api_key: str = Header(...),
    household_id: str = Depends(get_household_id)
):
    check_key(x_api_key)
    conn = get_conn()
    cur = conn.cursor()
    try:
        verify_patient_household(cur, str(visit.patient_id), household_id)
        cur.execute("""
            INSERT INTO visit_logs (patient_id, doctor_id, household_id,
                                    visit_date, reason, notes, follow_up_date)
            VALUES (%s,%s,%s,COALESCE(%s, now()),%s,%s,%s) RETURNING visit_id;
        """, (visit.patient_id, visit.doctor_id, household_id,
              visit.visit_date, visit.reason, visit.notes, visit.follow_up_date))
        visit_id = cur.fetchone()[0]

        has_vitals = any([visit.systolic, visit.diastolic, visit.oxygen_saturation,
                          visit.heart_rate, visit.temperature, visit.blood_glucose, visit.weight])
        if has_vitals:
            cur.execute("""
                INSERT INTO vitals (household_id, patient_id, recorded_at,
                                    systolic, diastolic, oxygen_saturation,
                                    heart_rate, temperature, blood_glucose,
                                    weight, source, notes)
                VALUES (%s,%s,COALESCE(%s, now()),%s,%s,%s,%s,%s,%s,%s,%s,%s)
            """, (household_id, visit.patient_id, visit.visit_date,
                  visit.systolic, visit.diastolic, visit.oxygen_saturation,
                  visit.heart_rate, visit.temperature, visit.blood_glucose,
                  visit.weight, "doctor_visit", visit.notes))
        conn.commit()
    except HTTPException:
        conn.rollback()
        raise
    except Exception as e:
        conn.rollback()
        raise HTTPException(status_code=400, detail=str(e))
    finally:
        cur.close()
        conn.close()
    return {"status": "success", "visit_id": str(visit_id)}

@app.patch("/api/visits/{visit_id}")
def update_visit(
    visit_id: str,
    update: VisitUpdate,
    x_api_key: str = Header(...),
    household_id: str = Depends(get_household_id)
):
    check_key(x_api_key)
    conn = get_conn()
    cur = conn.cursor()
    fields = []
    values = []
    for field, value in update.model_dump(exclude_none=True).items():
        fields.append(f"{field} = %s")
        values.append(value)
    if not fields:
        return {"status": "no changes"}
    fields.append("updated_at = now()")
    values.extend([visit_id, household_id])
    cur.execute(f"UPDATE visit_logs SET {', '.join(fields)} WHERE visit_id = %s AND household_id = %s", values)
    conn.commit()
    cur.close()
    conn.close()
    return {"status": "success"}

@app.get("/api/visits/latest")
def get_latest_visit(
    patient_id: str,
    doctor_id: str,
    x_api_key: str = Header(...),
    household_id: str = Depends(get_household_id)
):
    check_key(x_api_key)
    conn = get_conn()
    cur = conn.cursor()
    verify_patient_household(cur, patient_id, household_id)
    cur.execute("""
        SELECT visit_date, reason FROM visit_logs
        WHERE patient_id = %s AND doctor_id = %s AND household_id = %s AND is_active = true
        ORDER BY visit_date DESC LIMIT 1;
    """, (patient_id, doctor_id, household_id))
    latest = cur.fetchone()
    if not latest:
        cur.close()
        conn.close()
        return {}
    cur.execute("""
        SELECT follow_up_date FROM visit_logs
        WHERE patient_id = %s AND doctor_id = %s AND household_id = %s AND is_active = true
          AND follow_up_date IS NOT NULL AND follow_up_date >= current_date
        ORDER BY follow_up_date ASC LIMIT 1;
    """, (patient_id, doctor_id, household_id))
    followup = cur.fetchone()
    cur.close()
    conn.close()
    return {
        "visit_date": latest[0].isoformat() if latest[0] else None,
        "reason": latest[1],
        "follow_up_date": followup[0].isoformat() if followup else None
    }

# =====================================================
# INCIDENT LOG ENDPOINTS
# =====================================================
@app.get("/api/incidents")
def get_incidents(
    patient_id: str,
    x_api_key: str = Header(...),
    household_id: str = Depends(get_household_id)
):
    check_key(x_api_key)
    conn = get_conn()
    cur = conn.cursor()
    verify_patient_household(cur, patient_id, household_id)
    cur.execute("""
        SELECT incident_id, patient_id, incident_date, severity, incident_type,
               location, description, outcome, follow_up_needed, follow_up_notes, created_at
        FROM incident_logs
        WHERE patient_id = %s AND household_id = %s AND is_active = true
        ORDER BY incident_date DESC;
    """, (patient_id, household_id))
    rows = cur.fetchall()
    cur.close()
    conn.close()
    return {
        "incidents": [
            {
                "incident_id": str(r[0]), "patient_id": str(r[1]),
                "incident_date": r[2].isoformat() if r[2] else None,
                "severity": r[3], "incident_type": r[4], "location": r[5],
                "description": r[6], "outcome": r[7], "follow_up_needed": r[8],
                "follow_up_notes": r[9],
                "created_at": r[10].isoformat() if r[10] else None
            }
            for r in rows
        ]
    }

@app.post("/api/incidents")
def create_incident(
    incident: IncidentCreate,
    x_api_key: str = Header(...),
    household_id: str = Depends(get_household_id)
):
    check_key(x_api_key)
    conn = get_conn()
    cur = conn.cursor()
    try:
        verify_patient_household(cur, str(incident.patient_id), household_id)
        cur.execute("""
            INSERT INTO incident_logs (patient_id, household_id, incident_date,
                                       severity, incident_type, location, description,
                                       outcome, follow_up_needed, follow_up_notes)
            VALUES (%s,%s,COALESCE(%s, now()),%s,%s,%s,%s,%s,%s,%s) RETURNING incident_id;
        """, (incident.patient_id, household_id, incident.incident_date,
              incident.severity, incident.incident_type, incident.location,
              incident.description, incident.outcome, incident.follow_up_needed,
              incident.follow_up_notes))
        incident_id = cur.fetchone()[0]
        conn.commit()
    except HTTPException:
        conn.rollback()
        raise
    except Exception as e:
        conn.rollback()
        raise HTTPException(status_code=400, detail=str(e))
    finally:
        cur.close()
        conn.close()
    return {"status": "success", "incident_id": str(incident_id)}

@app.patch("/api/incidents/{incident_id}")
def update_incident(
    incident_id: str,
    update: IncidentUpdate,
    x_api_key: str = Header(...),
    household_id: str = Depends(get_household_id)
):
    check_key(x_api_key)
    conn = get_conn()
    cur = conn.cursor()
    fields = []
    values = []
    for field, value in update.model_dump(exclude_none=True).items():
        fields.append(f"{field} = %s")
        values.append(value)
    if not fields:
        return {"status": "no changes"}
    fields.append("updated_at = now()")
    values.extend([incident_id, household_id])
    cur.execute(f"UPDATE incident_logs SET {', '.join(fields)} WHERE incident_id = %s AND household_id = %s", values)
    conn.commit()
    cur.close()
    conn.close()
    return {"status": "success"}

# =====================================================
# NOTES ENDPOINTS
# =====================================================
@app.get("/api/notes")
def get_notes(
    patient_id: str,
    x_api_key: str = Header(...),
    household_id: str = Depends(get_household_id)
):
    check_key(x_api_key)
    conn = get_conn()
    cur = conn.cursor()
    verify_patient_household(cur, patient_id, household_id)
    cur.execute("""
        SELECT note_id, patient_id, note_type, title, body, created_at, updated_at
        FROM patient_notes
        WHERE patient_id = %s AND household_id = %s AND is_active = true
        ORDER BY created_at DESC;
    """, (patient_id, household_id))
    rows = cur.fetchall()
    cur.close()
    conn.close()
    return {
        "notes": [
            {
                "note_id": str(r[0]), "patient_id": str(r[1]), "note_type": r[2],
                "title": r[3], "body": r[4],
                "created_at": r[5].isoformat() if r[5] else None,
                "updated_at": r[6].isoformat() if r[6] else None
            }
            for r in rows
        ]
    }

@app.post("/api/notes")
def create_note(
    note: NoteCreate,
    x_api_key: str = Header(...),
    household_id: str = Depends(get_household_id)
):
    check_key(x_api_key)
    conn = get_conn()
    cur = conn.cursor()
    try:
        verify_patient_household(cur, str(note.patient_id), household_id)
        cur.execute("""
            INSERT INTO patient_notes (patient_id, household_id, note_type, title, body)
            VALUES (%s,%s,%s,%s,%s) RETURNING note_id;
        """, (note.patient_id, household_id, note.note_type, note.title, note.body))
        note_id = cur.fetchone()[0]
        conn.commit()
    except HTTPException:
        conn.rollback()
        raise
    except Exception as e:
        conn.rollback()
        raise HTTPException(status_code=400, detail=str(e))
    finally:
        cur.close()
        conn.close()
    return {"status": "success", "note_id": str(note_id)}

@app.patch("/api/notes/{note_id}")
def update_note(
    note_id: str,
    update: NoteUpdate,
    x_api_key: str = Header(...),
    household_id: str = Depends(get_household_id)
):
    check_key(x_api_key)
    conn = get_conn()
    cur = conn.cursor()
    fields = []
    values = []
    for field, value in update.model_dump(exclude_none=True).items():
        fields.append(f"{field} = %s")
        values.append(value)
    if not fields:
        return {"status": "no changes"}
    fields.append("updated_at = now()")
    values.extend([note_id, household_id])
    cur.execute(f"UPDATE patient_notes SET {', '.join(fields)} WHERE note_id = %s AND household_id = %s", values)
    conn.commit()
    cur.close()
    conn.close()
    return {"status": "success"}

@app.api_route("/api/health", methods=["GET", "HEAD"])
def health_check():
    return {"status": "ok"}

# =====================================================
# USER PREFERENCES
# =====================================================
@app.get("/api/user/preferences")
def get_user_preferences(
    user_id: str = Query(...),
    household_id: str = Depends(get_household_id),
    caller_user_id: str = Depends(get_own_user_id)
):
    if caller_user_id is not None and caller_user_id != user_id:
        raise HTTPException(status_code=403, detail="Cannot access another user's preferences")
    conn = get_conn()
    cur = conn.cursor()
    cur.execute("""
        SELECT user_id, display_name, theme,
               show_heart_rate, show_spo2, show_temperature,
               show_weight, show_glucose
        FROM users WHERE user_id = %s AND household_id = %s;
    """, (user_id, household_id))
    row = cur.fetchone()
    cur.close()
    conn.close()
    if not row:
        raise HTTPException(status_code=404, detail="User not found")
    return {
        "user_id":          str(row[0]),
        "display_name":     row[1],
        "theme":            row[2],
        "show_heart_rate":  row[3],
        "show_spo2":        row[4],
        "show_temperature": row[5],
        "show_weight":      row[6],
        "show_glucose":     row[7],
    }

@app.patch("/api/user/preferences")
def update_user_preferences(
    user_id: str = Query(...),
    payload: dict = Body(...),
    household_id: str = Depends(get_household_id),
    caller_user_id: str = Depends(get_own_user_id)
):
    if caller_user_id is not None and caller_user_id != user_id:
        raise HTTPException(status_code=403, detail="Cannot modify another user's preferences")

    allowed = {"theme", "show_heart_rate", "show_spo2",
               "show_temperature", "show_weight", "show_glucose"}
    updates = {k: v for k, v in payload.items() if k in allowed}
    if not updates:
        raise HTTPException(status_code=400, detail="No valid fields to update")

    fields = ", ".join(f"{k} = %s" for k in updates)
    values = list(updates.values()) + [user_id, household_id]

    conn = get_conn()
    cur = conn.cursor()
    cur.execute(f"""
        UPDATE users SET {fields}
        WHERE user_id = %s AND household_id = %s;
    """, values)
    conn.commit()
    cur.close()
    conn.close()
    return {"status": "updated"}

# =====================================================
# AUTH ENDPOINTS
# =====================================================
@app.post("/api/auth/google")
def auth_google(body: GoogleAuthRequest):
    try:
        request = google.auth.transport.requests.Request()
        decoded = google_id_token.verify_oauth2_token(
            body.id_token,
            request,
            audience=None
        )
    except Exception as e:
        raise HTTPException(status_code=401, detail=f"Invalid Google token: {e}")

    firebase_uid  = decoded["sub"]
    email         = decoded.get("email", "")
    display_name  = decoded.get("name", email.split("@")[0])
    provider      = "google.com"

    conn = get_conn()
    cur  = conn.cursor()

    try:
        cur.execute("""
            SELECT user_id, household_id, auth_provider FROM users
            WHERE firebase_uid = %s OR email = %s
            LIMIT 1;
        """, (firebase_uid, email))
        existing = cur.fetchone()

        if existing:
            user_id = str(existing[0])
            household_id = str(existing[1]) if existing[1] else None
            auth_provider_out = existing[2]
            cur.execute("""
                UPDATE users SET firebase_uid = %s, last_seen_at = now()
                WHERE user_id = %s;
            """, (firebase_uid, user_id))
        else:
            # Household is NOT created here anymore — the user picks a tier
            # (Individual/Family/Free) or joins an existing household via
            # invite code on the new CTA screen, and THAT is what actually
            # creates/attaches the household. A freshly-registered user has
            # household_id = NULL until then.
            cur.execute("""
                INSERT INTO users (
                    household_id, email, display_name,
                    firebase_uid, auth_provider, provider_user_id,
                    subscription_status, last_seen_at, has_logged_in
                )
                VALUES (NULL, %s, %s, %s, %s, %s, 'trial', now(), true)
                RETURNING user_id;
            """, (email, display_name, firebase_uid, provider, firebase_uid))
            user_id = str(cur.fetchone()[0])
            household_id = None
            auth_provider_out = provider

        conn.commit()

        # is_new_user now means "needs onboarding" — household_id being
        # null is exactly that signal, whether this is a brand-new row or
        # an existing account that registered but never finished tier
        # selection/joining before closing the app.
        is_new_user = household_id is None

        token = create_jwt(user_id, household_id, email)
        return {
            "token":         token,
            "user_id":       user_id,
            "household_id":  household_id,
            "display_name":  display_name,
            "email":         email,
            "is_new_user":   is_new_user,
            "auth_provider": auth_provider_out,
        }

    except Exception as e:
        conn.rollback()
        raise HTTPException(status_code=500, detail=f"Auth error: {e}")
    finally:
        cur.close()
        conn.close()


@app.post("/api/auth/register")
def register(body: RegisterRequest):
    email = body.email.strip().lower()

    if not body.password or len(body.password) < 8:
        raise HTTPException(status_code=400, detail="Password must be at least 8 characters")
    if not body.display_name.strip():
        raise HTTPException(status_code=400, detail="Display name is required")

    conn = get_conn()
    cur = conn.cursor()
    try:
        cur.execute("SELECT 1 FROM users WHERE email = %s", (email,))
        if cur.fetchone():
            raise HTTPException(status_code=409, detail="An account with this email already exists")

        # Household is NOT created here anymore — see auth_google for the
        # same change and reasoning. household_id stays NULL until the user
        # picks a tier or joins an existing household via invite code.
        password_hash = hash_password(body.password)
        verification_token = secrets.token_urlsafe(32)
        expires_at = datetime.now(timezone.utc) + timedelta(hours=24)

        cur.execute("""
            INSERT INTO users (
                household_id, email, display_name, auth_provider,
                password_hash, email_verified, verification_token,
                verification_token_expires_at, subscription_status,
                last_seen_at, has_logged_in
            )
            VALUES (NULL, %s, %s, 'password', %s, false, %s, %s, 'trial', now(), false)
            RETURNING user_id;
        """, (email, body.display_name.strip(), password_hash,
              verification_token, expires_at))
        user_id = str(cur.fetchone()[0])
        conn.commit()

        send_verification_email(email, verification_token)

        return {
            "status":  "verification_sent",
            "email":   email,
            "message": "Check your email to verify your account, then sign in.",
        }
    except HTTPException:
        conn.rollback()
        raise
    except Exception as e:
        conn.rollback()
        raise HTTPException(status_code=500, detail=f"Registration error: {e}")
    finally:
        cur.close()
        conn.close()


@app.get("/api/auth/verify-email", response_class=HTMLResponse)
def verify_email(token: str = Query(...)):
    conn = get_conn()
    cur = conn.cursor()
    try:
        cur.execute("""
            SELECT user_id, verification_token_expires_at, email_verified
            FROM users WHERE verification_token = %s
        """, (token,))
        row = cur.fetchone()

        if not row:
            return HTMLResponse(
                verification_page(
                    "Link not valid",
                    "This verification link is invalid or has already been used.",
                    is_error=True),
                status_code=400)

        user_id, expires_at, already_verified = row

        if already_verified:
            return HTMLResponse(
                verification_page(
                    "Already verified",
                    "Your email is already verified — you can sign in to Vitals now."))

        if expires_at and expires_at < datetime.now(timezone.utc):
            return HTMLResponse(
                verification_page(
                    "Link expired",
                    "This verification link has expired. Please request a new one from the app.",
                    is_error=True),
                status_code=400)

        cur.execute("""
            UPDATE users SET email_verified = true, verification_token = NULL,
                             verification_token_expires_at = NULL
            WHERE user_id = %s
        """, (str(user_id),))
        conn.commit()
        return HTMLResponse(
            verification_page(
                "Email verified!",
                "Your account is ready. Head back to Vitals and sign in."))
    finally:
        cur.close()
        conn.close()


@app.post("/api/auth/resend-verification")
def resend_verification(body: ResendVerificationRequest):
    email = body.email.strip().lower()
    # Same message whether or not the email exists / is already verified —
    # avoids letting this endpoint be used to enumerate registered emails.
    generic_response = {
        "status": "ok",
        "message": "If that email has a pending verification, a new link has been sent.",
    }

    conn = get_conn()
    cur = conn.cursor()
    try:
        cur.execute("""
            SELECT user_id, email_verified FROM users
            WHERE email = %s AND auth_provider = 'password'
        """, (email,))
        row = cur.fetchone()
        if not row:
            return generic_response

        user_id, email_verified = row
        if email_verified:
            return generic_response

        verification_token = secrets.token_urlsafe(32)
        expires_at = datetime.now(timezone.utc) + timedelta(hours=24)
        cur.execute("""
            UPDATE users SET verification_token = %s, verification_token_expires_at = %s
            WHERE user_id = %s
        """, (verification_token, expires_at, str(user_id)))
        conn.commit()
        send_verification_email(email, verification_token)
        return generic_response
    finally:
        cur.close()
        conn.close()


@app.post("/api/household/invite")
def create_household_invite(
    body: HouseholdInviteRequest,
    household_id: str = Depends(get_household_id),
    auth: dict = Depends(get_auth),
):
    if auth.get("type") == "api_key":
        raise HTTPException(status_code=401, detail="Household invites require a signed-in account")

    inviter_user_id = auth.get("sub")
    invitee_email = body.invitee_email.strip().lower()

    conn = get_conn()
    cur = conn.cursor()
    try:
        cur.execute("SELECT email, display_name FROM users WHERE user_id = %s", (inviter_user_id,))
        row = cur.fetchone()
        inviter_email = row[0] if row else None
        inviter_name = row[1] if row and row[1] else "A Vitals user"

        if inviter_email and invitee_email == inviter_email.strip().lower():
            raise HTTPException(status_code=400, detail="You can't invite yourself.")

        # If there's already a pending (unused, unexpired) invite to this
        # same email, cancel it and issue a fresh one rather than creating
        # a second reservation for what's really the same intended person —
        # e.g. their first email landed in spam and they need it resent.
        # Also gives them a full new 24-hour window instead of whatever
        # time was left on the old code.
        cur.execute("""
            SELECT invite_id FROM household_invites
            WHERE household_id = %s AND invited_email = %s
              AND used_at IS NULL AND expires_at > now()
        """, (household_id, invitee_email))
        existing_pending = cur.fetchone()
        if existing_pending:
            mark_invite_used(cur, str(existing_pending[0]))

        patient_limit, patient_count, pending_invite_count = count_reserved_slots(cur, household_id)
        if patient_count + pending_invite_count >= patient_limit:
            raise HTTPException(
                status_code=403,
                detail="You've used all your available patient slots. Cancel a pending invite, "
                       "or wait for one to expire, before sending another."
            )

        code = generate_invite_code()
        expires_at = datetime.now(timezone.utc) + timedelta(hours=24)

        cur.execute("""
            INSERT INTO household_invites (household_id, code, invited_email, created_by, expires_at)
            VALUES (%s, %s, %s, %s, %s)
            RETURNING invite_id;
        """, (household_id, code, invitee_email, inviter_user_id, expires_at))
        conn.commit()

        send_household_invite_email(invitee_email, code, inviter_name)

        return {"status": "sent", "invitee_email": invitee_email, "expires_at": expires_at.isoformat()}
    except HTTPException:
        conn.rollback()
        raise
    except Exception as e:
        conn.rollback()
        raise HTTPException(status_code=500, detail=f"Invite error: {e}")
    finally:
        cur.close()
        conn.close()


@app.get("/api/household/status")
def get_household_status(
    household_id: str = Depends(get_household_id),
    auth: dict = Depends(get_auth),
):
    """
    Lets the client (Settings' invite UI) proactively disable the "Invite"
    button using the exact same math the server enforces
    (count_reserved_slots), instead of only finding out after tapping it
    and getting a 403 back.
    """
    if auth.get("type") == "api_key":
        raise HTTPException(status_code=401, detail="This requires a signed-in account")

    conn = get_conn()
    cur = conn.cursor()
    patient_limit, patient_count, pending_invite_count = count_reserved_slots(cur, household_id)
    cur.close()
    conn.close()

    available_slots = max(0, patient_limit - patient_count - pending_invite_count)

    return {
        "patient_limit": patient_limit,
        "patient_count": patient_count,
        "pending_invite_count": pending_invite_count,
        "available_slots": available_slots,
        "can_invite": available_slots > 0,
    }


@app.get("/api/household/invites")
def list_household_invites(
    household_id: str = Depends(get_household_id),
    auth: dict = Depends(get_auth),
):
    """
    Lists this household's pending (unused, unexpired) invites — what the
    primary account holder sees to decide whether to cancel one and free
    up a reserved slot, per count_reserved_slots().
    """
    if auth.get("type") == "api_key":
        raise HTTPException(status_code=401, detail="Household invites require a signed-in account")

    conn = get_conn()
    cur = conn.cursor()
    cur.execute("""
        SELECT invite_id, invited_email, created_at, expires_at
        FROM household_invites
        WHERE household_id = %s AND used_at IS NULL AND expires_at > now()
        ORDER BY created_at DESC;
    """, (household_id,))
    rows = cur.fetchall()
    cur.close()
    conn.close()
    return {
        "invites": [
            {
                "invite_id": str(r[0]),
                "invited_email": r[1],
                "created_at": r[2].isoformat(),
                "expires_at": r[3].isoformat(),
            }
            for r in rows
        ]
    }


@app.delete("/api/household/invite/{invite_id}")
def cancel_household_invite(
    invite_id: str,
    household_id: str = Depends(get_household_id),
    auth: dict = Depends(get_auth),
):
    """
    Cancels a pending invite before it's redeemed, freeing up the slot it
    was reserving. Marks the same used_at column an actual redemption
    would — either way, the invite becomes permanently unredeemable and
    stops counting toward count_reserved_slots(). Scoped to the caller's
    own household so one household can't cancel another's invite by ID.
    """
    if auth.get("type") == "api_key":
        raise HTTPException(status_code=401, detail="Household invites require a signed-in account")

    conn = get_conn()
    cur = conn.cursor()
    try:
        cur.execute("""
            SELECT used_at FROM household_invites
            WHERE invite_id = %s AND household_id = %s
        """, (invite_id, household_id))
        row = cur.fetchone()

        if not row:
            raise HTTPException(status_code=404, detail="Invite not found")
        if row[0] is not None:
            raise HTTPException(status_code=400, detail="This invite is no longer pending")

        mark_invite_used(cur, invite_id)
        conn.commit()
        return {"status": "cancelled"}
    except HTTPException:
        conn.rollback()
        raise
    except Exception as e:
        conn.rollback()
        raise HTTPException(status_code=500, detail=f"Cancel error: {e}")
    finally:
        cur.close()
        conn.close()


@app.post("/api/household/select-tier")
def select_household_tier(body: HouseholdTierRequest, auth: dict = Depends(get_auth)):
    """
    Creates the household and attaches it to the caller — this is what
    actually creates a household now, not registration. Called from the
    plan-selection CTA (Individual / Family / Free) that runs before
    Personalization. Tier is recorded as intent only; no billing happens
    here (Stripe integration is Phase 7) — this just makes sure Phase 7
    has a real tier value to work from instead of having to backfill one.
    """
    if auth.get("type") == "api_key":
        raise HTTPException(status_code=401, detail="This requires a signed-in account")

    if body.tier not in ("individual", "family", "free"):
        raise HTTPException(status_code=400, detail="Invalid tier")

    user_id = auth.get("sub")
    if auth.get("household_id"):
        raise HTTPException(status_code=400, detail="You're already part of a household.")

    conn = get_conn()
    cur = conn.cursor()
    try:
        cur.execute("SELECT display_name, email FROM users WHERE user_id = %s", (user_id,))
        row = cur.fetchone()
        if not row:
            raise HTTPException(status_code=404, detail="Account not found")
        display_name, email = row

        # Family gets its real capacity immediately — no billing has
        # happened yet regardless of tier (Phase 7), so there's no reason
        # to artificially withhold Family's 5-patient allowance just
        # because payment collection isn't wired up yet.
        patient_limit = 5 if body.tier == "family" else 2

        household_name = f"{display_name}'s Household" if display_name else "New Household"
        cur.execute("""
            INSERT INTO households (name, tier, subscription_status, trial_started_at, patient_limit, created_at)
            VALUES (%s, %s, 'trial', now(), %s, now())
            RETURNING household_id;
        """, (household_name, body.tier, patient_limit))
        household_id = str(cur.fetchone()[0])

        cur.execute("UPDATE users SET household_id = %s WHERE user_id = %s", (household_id, user_id))
        conn.commit()

        token = create_jwt(user_id, household_id, email)
        return {"status": "created", "household_id": household_id, "tier": body.tier, "token": token}
    except HTTPException:
        conn.rollback()
        raise
    except Exception as e:
        conn.rollback()
        raise HTTPException(status_code=500, detail=f"Tier selection error: {e}")
    finally:
        cur.close()
        conn.close()


@app.post("/api/household/join")
def join_household(
    body: HouseholdJoinRequest,
    auth: dict = Depends(get_auth),
):
    """
    Attaches the caller to an existing household (identified by a valid
    invite code) instead of creating a new one. Called from the plan
    CTA's "Join an existing household" option. Since household_id is now
    null until tier selection/join actually happens, there's no orphaned
    household to clean up here — this just sets it, once, on a user who
    doesn't have one yet.
    """
    if auth.get("type") == "api_key":
        raise HTTPException(status_code=401, detail="Joining a household requires a signed-in account")

    user_id = auth.get("sub")
    if auth.get("household_id"):
        raise HTTPException(status_code=400, detail="You're already part of a household.")

    conn = get_conn()
    cur = conn.cursor()
    try:
        household_id, invite_id = resolve_invite_household(cur, body.invite_code)

        cur.execute("UPDATE users SET household_id = %s WHERE user_id = %s", (household_id, user_id))
        mark_invite_used(cur, invite_id)
        conn.commit()

        cur.execute("SELECT email FROM users WHERE user_id = %s", (user_id,))
        email = cur.fetchone()[0]
        token = create_jwt(user_id, household_id, email)

        # Tells the client whether "create a new patient for myself" should
        # be offered on the next screen, or whether joining should only
        # offer attaching to one of the household's existing patients.
        # Joining itself is never blocked by the patient limit — only
        # creating an ADDITIONAL patient is, since attaching to an existing
        # one doesn't consume a slot.
        cur.execute("SELECT patient_limit FROM households WHERE household_id = %s", (household_id,))
        row = cur.fetchone()
        patient_limit = row[0] if row and row[0] is not None else 2
        cur.execute("SELECT COUNT(*) FROM patients WHERE household_id = %s", (household_id,))
        current_count = cur.fetchone()[0]
        can_create_new_patient = current_count < patient_limit

        return {
            "status": "joined",
            "household_id": household_id,
            "token": token,
            "can_create_new_patient": can_create_new_patient,
        }
    except HTTPException:
        conn.rollback()
        raise
    except Exception as e:
        conn.rollback()
        raise HTTPException(status_code=500, detail=f"Join error: {e}")
    finally:
        cur.close()
        conn.close()


@app.post("/api/auth/login")
def login(body: LoginRequest):
    email = body.email.strip().lower()

    conn = get_conn()
    cur = conn.cursor()
    try:
        cur.execute("""
            SELECT user_id, household_id, display_name, password_hash,
                   auth_provider, email_verified
            FROM users WHERE email = %s
        """, (email,))
        row = cur.fetchone()

        if not row:
            raise HTTPException(status_code=401, detail="Invalid email or password")

        (user_id, household_id_raw, display_name, password_hash,
         auth_provider, email_verified) = row
        household_id = str(household_id_raw) if household_id_raw else None

        if auth_provider != "password" or not password_hash:
            other = "Google" if auth_provider == "google.com" else "a different sign-in method"
            raise HTTPException(
                status_code=401,
                detail=f"This email is registered with {other}. Please use that to sign in instead."
            )

        if not verify_password(body.password, password_hash):
            raise HTTPException(status_code=401, detail="Invalid email or password")

        if not email_verified:
            raise HTTPException(
                status_code=403,
                detail="Please verify your email before signing in. Check your inbox for the verification link."
            )

        # is_new_user means "needs onboarding" — household_id being null is
        # exactly that signal, whether this is this account's first login
        # ever, or a returning account that verified but never finished
        # tier selection/joining before closing the app. Simpler and more
        # correct than tracking a separate has_logged_in flag.
        is_new_user = household_id is None

        cur.execute("UPDATE users SET last_seen_at = now(), has_logged_in = true WHERE user_id = %s", (str(user_id),))
        conn.commit()

        token = create_jwt(str(user_id), household_id, email)
        return {
            "token":         token,
            "user_id":       str(user_id),
            "household_id":  household_id,
            "display_name":  display_name,
            "email":         email,
            "is_new_user":   is_new_user,
            "auth_provider": auth_provider,
        }
    except HTTPException:
        conn.rollback()
        raise
    finally:
        cur.close()
        conn.close()


@app.get("/api/auth/verify")
def verify_session(auth: dict = Depends(get_auth)):
    """
    Confirms the JWT's user_id still has a real row in the database — a
    valid, unexpired JWT alone doesn't mean that; the account (or its
    household) could have been deleted server-side after the token was
    issued, and the token itself has no way to reflect that until it
    naturally expires (up to 7 days). The mobile app calls this on launch
    before trusting a locally-cached session, instead of just checking
    whether a JWT is present.
    """
    if auth.get("type") == "api_key":
        return {"valid": True}

    user_id = auth.get("sub")
    if not user_id:
        raise HTTPException(status_code=401, detail="Token missing user id")

    conn = get_conn()
    cur = conn.cursor()
    cur.execute("SELECT 1 FROM users WHERE user_id = %s", (user_id,))
    exists = cur.fetchone() is not None
    cur.close()
    conn.close()

    if not exists:
        raise HTTPException(status_code=401, detail="Account no longer exists")

    return {"valid": True}