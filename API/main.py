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
from google.oauth2 import service_account
from jose import jwt as jose_jwt, JWTError
from datetime import timezone
import matplotlib
import numpy as np
from scipy import stats
matplotlib.use('Agg')
import matplotlib.pyplot as plt
import matplotlib.dates as mdates
import base64
import json
import os
import psycopg2
import re
import bcrypt
import secrets
import requests
from zoneinfo import ZoneInfo
import statistics
import hashlib
import hmac
from jose import jwk


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
    "/api/auth/google", "/api/auth/apple", "/api/health",
    "/api/auth/register", "/api/auth/login",
    "/api/auth/verify-email", "/api/auth/resend-verification",
}

APPLE_CLIENT_ID = os.getenv("APPLE_CLIENT_ID", "com.vitalswellness.vitals")
APPLE_KEYS_URL = "https://appleid.apple.com/auth/keys"
APPLE_ISSUER = "https://appleid.apple.com"

# Native subscription verification configuration. Secrets stay server-side.
APPLE_BUNDLE_ID = os.getenv("APPLE_BUNDLE_ID", APPLE_CLIENT_ID)
APPLE_IAP_KEY_ID = os.getenv("APPLE_IAP_KEY_ID")
APPLE_IAP_ISSUER_ID = os.getenv("APPLE_IAP_ISSUER_ID")
APPLE_IAP_PRIVATE_KEY = os.getenv("APPLE_IAP_PRIVATE_KEY")
APPLE_IAP_PRIVATE_KEY_PATH = os.getenv("APPLE_IAP_PRIVATE_KEY_PATH")
GOOGLE_PLAY_PACKAGE_NAME = os.getenv(
    "GOOGLE_PLAY_PACKAGE_NAME",
    "com.vitalswellness.vitals",
)
GOOGLE_PLAY_SERVICE_ACCOUNT_JSON = os.getenv("GOOGLE_PLAY_SERVICE_ACCOUNT_JSON")
GOOGLE_PLAY_SERVICE_ACCOUNT_PATH = os.getenv("GOOGLE_PLAY_SERVICE_ACCOUNT_PATH")
GOOGLE_PLAY_SCOPE = "https://www.googleapis.com/auth/androidpublisher"
VITALS_BILLING_GRACE_DAYS = 5

RESEND_API_KEY = os.getenv("RESEND_API_KEY")
EMAIL_FROM = "Vitals <noreply@vitals-wellness.com>"

# Phase 7 / 0.7.2 native-store product catalog.
#
# Product identifiers are configuration, not entitlement logic. Keeping them
# in environment variables lets App Store Connect / Play Console identifiers
# be finalized without another mobile build or backend code change.
#
# Google Play subscriptions use a subscription product plus a base plan;
# Apple auto-renewable subscription durations use separate product IDs.
BILLING_PRODUCT_ENV = {
    "apple": {
        ("standard", "monthly"): ("APPLE_STANDARD_MONTHLY_PRODUCT_ID", None),
        ("standard", "annual"): ("APPLE_STANDARD_ANNUAL_PRODUCT_ID", None),
        ("family", "monthly"): ("APPLE_FAMILY_MONTHLY_PRODUCT_ID", None),
        ("family", "annual"): ("APPLE_FAMILY_ANNUAL_PRODUCT_ID", None),
    },
    "google": {
        ("standard", "monthly"): (
            "GOOGLE_STANDARD_PRODUCT_ID",
            "GOOGLE_STANDARD_MONTHLY_BASE_PLAN_ID",
        ),
        ("standard", "annual"): (
            "GOOGLE_STANDARD_PRODUCT_ID",
            "GOOGLE_STANDARD_ANNUAL_BASE_PLAN_ID",
        ),
        ("family", "monthly"): (
            "GOOGLE_FAMILY_PRODUCT_ID",
            "GOOGLE_FAMILY_MONTHLY_BASE_PLAN_ID",
        ),
        ("family", "annual"): (
            "GOOGLE_FAMILY_PRODUCT_ID",
            "GOOGLE_FAMILY_ANNUAL_BASE_PLAN_ID",
        ),
    },
}

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
def _raise_if_patient_entitlement_locked(
    cur,
    household_id: str,
    stored_entitlement_locked: bool,
):
    """
    Enforces the over-capacity transition before any patient-scoped read or
    write. While a household still needs to choose which profiles remain
    active, no patient is silently chosen on its behalf. After selection,
    only the persisted locked profiles are denied.

    Stored locks apply only while the household is above its CURRENT effective
    plan capacity, so upgrading immediately restores every profile without
    deleting or rewriting clinical data.
    """
    entitlement = get_household_entitlement(cur, household_id)

    if entitlement["requires_basic_patient_selection"]:
        raise HTTPException(
            status_code=409,
            detail="Choose which patients to keep active before continuing."
        )

    patient_limit = entitlement["patient_limit"]
    if (
        stored_entitlement_locked
        and patient_limit is not None
        and entitlement["patient_count"] > patient_limit
    ):
        raise HTTPException(
            status_code=403,
            detail="This patient is locked by your current plan. Choose an active patient or upgrade to restore access."
        )


def verify_patient_household(cur, patient_id: str, household_id: str):
    """
    Confirms patient_id belongs to household_id and is available under the
    household's current entitlement before a patient-scoped read/write.
    """
    cur.execute(
        """
        SELECT entitlement_locked
        FROM patients
        WHERE patient_id = %s AND household_id = %s
        """,
        (patient_id, household_id)
    )
    row = cur.fetchone()
    if row is None:
        raise HTTPException(status_code=403, detail="Patient not found in your household")

    _raise_if_patient_entitlement_locked(cur, household_id, bool(row[0]))


def verify_child_record_household(cur, table: str, id_column: str, record_id: str, household_id: str):
    """
    Same check for endpoints addressed by a child record id rather than
    patient_id directly.
    """
    cur.execute(f"""
        SELECT p.entitlement_locked
        FROM {table} t
        JOIN patients p ON p.patient_id = t.patient_id
        WHERE t.{id_column} = %s AND p.household_id = %s
    """, (record_id, household_id))
    row = cur.fetchone()
    if row is None:
        raise HTTPException(status_code=403, detail="Record not found in your household")

    _raise_if_patient_entitlement_locked(cur, household_id, bool(row[0]))

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
    temperature_site: Optional[Literal[
        "oral", "rectal", "axillary", "tympanic", "temporal", "other", "unknown"
    ]] = None
    blood_glucose: Optional[int] = Field(None, ge=30, le=600)
    # Patient-recorded glucose context. The numeric glucose value remains on
    # vitals.blood_glucose (normalized mg/dL); this metadata lives in the
    # one-to-one glucose_context row keyed by vital_id.
    glucose_context: Optional[Literal[
        "fasting", "pre_meal", "post_meal", "bedtime", "random", "other", "unknown"
    ]] = None
    glucose_meal_type: Optional[Literal[
        "breakfast", "lunch", "dinner", "snack", "other"
    ]] = None
    glucose_minutes_after_meal: Optional[int] = Field(None, ge=0, le=720)
    glucose_meal_event_id: Optional[UUID] = None
    glucose_source_type: Optional[Literal[
        "manual_bgm", "cgm", "import", "other"
    ]] = None
    glucose_original_value: Optional[float] = Field(None, ge=0)
    glucose_original_unit: Optional[Literal["mg/dL", "mmol/L"]] = None
    glucose_source_device: Optional[str] = None
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
    # Canonical storage is total inches even though the mobile UI captures
    # feet + inches. Nullable because a caregiver may not know height during
    # onboarding and can add it later from Settings.
    height_inches: Optional[int] = Field(None, ge=12, le=107)
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
    height_inches: Optional[int]
    # True only while this household is above its current effective patient
    # capacity and this profile was not selected to remain active.
    is_entitlement_locked: bool = False

class PatientDemographicsUpdate(BaseModel):
    gender: Optional[str] = None
    # Kept for backward compatibility with older app builds. New builds use
    # the dated /height endpoint for explicit height measurements/corrections.
    height_inches: Optional[int] = Field(None, ge=12, le=107)

class PatientHeightCreate(BaseModel):
    height_inches: int = Field(..., ge=12, le=107)
    effective_date: Optional[date] = None
    update_type: Literal["measurement", "correction"] = "measurement"

class PatientHeightOut(BaseModel):
    height_id: str
    patient_id: str
    height_inches: int
    effective_date: date
    entry_type: str
    source: str
    supersedes_height_id: Optional[str]
    is_active: bool
    created_at: datetime

class PatientHeightSaveResponse(BaseModel):
    record: PatientHeightOut
    current_height_inches: Optional[int]

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
    temperature_site: Optional[Literal[
        "oral", "rectal", "axillary", "tympanic", "temporal", "other", "unknown"
    ]] = None
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

class AppleAuthRequest(BaseModel):
    id_token: str
    raw_nonce: str
    # Apple only returns fullName on the FIRST authorization. The client
    # forwards it when present so a brand-new Vitals account can keep a
    # friendly display name; identity itself comes only from the verified JWT.
    display_name: Optional[str] = None

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
    # Phase 7 onboarding records plan intent only. Access remains a full
    # 30-day household trial until billing/entitlement enforcement decides
    # otherwise. "individual"/"free" are accepted temporarily by the
    # endpoint for backward compatibility and normalized server-side.
    tier: str  # "standard" | "family" | "trial"

class PatientAccessSelectionRequest(BaseModel):
    # Patient profiles that should remain active while this household is
    # above its current effective plan capacity.
    patient_ids: list[str]

class BillingCatalogProduct(BaseModel):
    plan: Literal["standard", "family"]
    billing_period: Literal["monthly", "annual"]
    product_id: Optional[str] = None
    base_plan_id: Optional[str] = None
    configured: bool

class BillingCatalogResponse(BaseModel):
    provider: Literal["apple", "google"]
    configured: bool
    account_binding_token: str
    products: List[BillingCatalogProduct]

class BillingVerifyRequest(BaseModel):
    provider: Literal["apple", "google"]
    # Apple: StoreKit transactionId. The server resolves current subscription
    # status from the App Store Server API; client-supplied JWS is never trusted.
    transaction_id: Optional[str] = None
    # Google: purchaseToken returned by Google Play Billing.
    purchase_token: Optional[str] = None

class BillingVerifyResponse(BaseModel):
    verified: bool
    entitlement_changed: bool
    provider: Literal["apple", "google"]
    plan: Literal["standard", "family"]
    billing_period: Literal["monthly", "annual"]
    product_id: str
    base_plan_id: Optional[str] = None
    store_status: str
    auto_renew_enabled: Optional[bool] = None
    expires_at: Optional[datetime] = None
    billing_subscription_id: str
    entitlement: dict

# --------------------
# Utility functions
# --------------------
def _billing_env_value(name: Optional[str]) -> Optional[str]:
    if not name:
        return None
    value = os.getenv(name)
    if value is None:
        return None
    value = value.strip()
    return value or None


def get_billing_product_catalog(provider: str) -> dict:
    """
    Returns the provider-specific Standard/Family monthly/annual product map.

    Prices are intentionally NOT stored here. Apple and Google remain the
    authority for localized price/currency presentation; the mobile client
    fetches those details from the store using these configured identifiers.
    """
    specs = BILLING_PRODUCT_ENV.get(provider)
    if specs is None:
        raise HTTPException(status_code=400, detail="Unsupported billing provider")

    products = []
    for (plan, billing_period), (product_env, base_plan_env) in specs.items():
        product_id = _billing_env_value(product_env)
        base_plan_id = _billing_env_value(base_plan_env)

        configured = bool(
            product_id
            and (provider != "google" or base_plan_id)
        )
        products.append({
            "plan": plan,
            "billing_period": billing_period,
            "product_id": product_id,
            "base_plan_id": base_plan_id,
            "configured": configured,
        })

    return {
        "provider": provider,
        "configured": all(product["configured"] for product in products),
        "products": products,
    }


def _billing_account_binding_token(provider: str, user_id: str) -> str:
    """
    Returns the account identifier the native purchase client must attach to
    the store transaction.

    Apple requires appAccountToken to be a UUID, so the Vitals user UUID is
    used directly. Google expects an obfuscated external account identifier;
    a one-way SHA-256 value keeps the raw Vitals user id out of Play billing.
    """
    if provider == "apple":
        try:
            return str(UUID(str(user_id)))
        except (TypeError, ValueError):
            raise HTTPException(status_code=400, detail="Invalid Vitals user id")

    if provider == "google":
        return hashlib.sha256(
            f"vitals:{user_id}".encode("utf-8")
        ).hexdigest()

    raise HTTPException(status_code=400, detail="Unsupported billing provider")


def _catalog_product_for_verified_purchase(
    provider: str,
    product_id: Optional[str],
    base_plan_id: Optional[str] = None,
) -> dict:
    """
    Maps a STORE-VERIFIED product back to the Vitals commercial plan.

    Client-supplied plan/period values are intentionally absent from the
    verification request; a verified store product is the only source of truth.
    """
    if not product_id:
        raise HTTPException(status_code=409, detail="Store purchase has no product id")

    catalog = get_billing_product_catalog(provider)
    for product in catalog["products"]:
        if not product["configured"]:
            continue
        if product["product_id"] != product_id:
            continue
        if provider == "google" and product["base_plan_id"] != base_plan_id:
            continue
        return product

    raise HTTPException(
        status_code=409,
        detail="The verified store product is not configured for Vitals."
    )


def _require_any_configured_store_product(provider: str):
    catalog = get_billing_product_catalog(provider)
    if not any(product["configured"] for product in catalog["products"]):
        raise HTTPException(
            status_code=503,
            detail=f"{provider.title()} billing products are not configured yet."
        )


def _parse_store_datetime(value: Optional[str]) -> Optional[datetime]:
    if not value:
        return None
    try:
        normalized = value.strip()
        if normalized.endswith("Z"):
            normalized = normalized[:-1] + "+00:00"
        parsed = datetime.fromisoformat(normalized)
        if parsed.tzinfo is None:
            parsed = parsed.replace(tzinfo=timezone.utc)
        return parsed.astimezone(timezone.utc)
    except (TypeError, ValueError):
        raise HTTPException(
            status_code=502,
            detail="Store returned an invalid subscription timestamp."
        )


def _apple_datetime_from_millis(value) -> Optional[datetime]:
    if value is None:
        return None
    try:
        return datetime.fromtimestamp(float(value) / 1000.0, tz=timezone.utc)
    except (TypeError, ValueError, OSError):
        raise HTTPException(
            status_code=502,
            detail="Apple returned an invalid subscription timestamp."
        )


def _decode_apple_server_jws_payload(signed_value: Optional[str]) -> dict:
    """
    Decode a JWS returned directly by Apple's authenticated Server API.

    Vitals never accepts this signed payload from the mobile client. The JWS is
    obtained only through a server-to-server HTTPS request authenticated with
    our App Store Connect in-app-purchase key; the transport/API response is
    therefore the trust boundary for this slice. App Store Server
    Notifications will use full JWS signature validation in the notification
    slice.
    """
    if not signed_value:
        raise HTTPException(status_code=502, detail="Apple response is missing signed data")

    parts = signed_value.split(".")
    if len(parts) != 3:
        raise HTTPException(status_code=502, detail="Apple returned malformed signed data")

    try:
        payload = parts[1] + ("=" * (-len(parts[1]) % 4))
        decoded = base64.urlsafe_b64decode(payload.encode("ascii"))
        result = json.loads(decoded.decode("utf-8"))
        if not isinstance(result, dict):
            raise ValueError("JWS payload is not an object")
        return result
    except Exception:
        raise HTTPException(status_code=502, detail="Apple returned unreadable signed data")


def _apple_private_key_text() -> str:
    if APPLE_IAP_PRIVATE_KEY:
        return APPLE_IAP_PRIVATE_KEY.replace("\\n", "\n")

    if APPLE_IAP_PRIVATE_KEY_PATH:
        try:
            with open(APPLE_IAP_PRIVATE_KEY_PATH, "r", encoding="utf-8") as handle:
                value = handle.read().strip()
            if value:
                return value
        except OSError:
            pass

    raise HTTPException(
        status_code=503,
        detail="Apple billing verification credentials are not configured."
    )


def _apple_server_authorization_token() -> str:
    if not APPLE_IAP_KEY_ID or not APPLE_IAP_ISSUER_ID or not APPLE_BUNDLE_ID:
        raise HTTPException(
            status_code=503,
            detail="Apple billing verification credentials are not configured."
        )

    now_seconds = int(datetime.now(timezone.utc).timestamp())
    payload = {
        "iss": APPLE_IAP_ISSUER_ID,
        "iat": now_seconds,
        "exp": now_seconds + (15 * 60),
        "aud": "appstoreconnect-v1",
        "bid": APPLE_BUNDLE_ID,
    }
    headers = {
        "alg": "ES256",
        "kid": APPLE_IAP_KEY_ID,
        "typ": "JWT",
    }

    try:
        return jose_jwt.encode(
            payload,
            _apple_private_key_text(),
            algorithm="ES256",
            headers=headers,
        )
    except HTTPException:
        raise
    except Exception:
        raise HTTPException(
            status_code=503,
            detail="Apple billing verification credentials are not usable."
        )


def _apple_server_get(path: str, environment: str):
    base_url = (
        "https://api.storekit-sandbox.apple.com"
        if environment == "sandbox"
        else "https://api.storekit.apple.com"
    )
    try:
        return requests.get(
            f"{base_url}{path}",
            headers={
                "Authorization": f"Bearer {_apple_server_authorization_token()}",
                "Accept": "application/json",
            },
            timeout=15,
        )
    except requests.RequestException:
        raise HTTPException(
            status_code=503,
            detail="Apple billing verification is temporarily unavailable."
        )


def _apple_response_or_error(response, not_found_detail: str) -> dict:
    if response.status_code == 200:
        try:
            payload = response.json()
        except ValueError:
            raise HTTPException(status_code=502, detail="Apple returned invalid JSON")
        if not isinstance(payload, dict):
            raise HTTPException(status_code=502, detail="Apple returned an invalid response")
        return payload

    if response.status_code == 404:
        raise HTTPException(status_code=404, detail=not_found_detail)
    if response.status_code in (401, 403):
        raise HTTPException(
            status_code=503,
            detail="Apple billing verification credentials were rejected."
        )
    if response.status_code == 429 or response.status_code >= 500:
        raise HTTPException(
            status_code=503,
            detail="Apple billing verification is temporarily unavailable."
        )

    raise HTTPException(status_code=400, detail="Apple rejected the transaction identifier.")


def _fetch_apple_transaction(transaction_id: str) -> tuple[str, dict]:
    safe_id = requests.utils.quote(transaction_id, safe="")
    path = f"/inApps/v1/transactions/{safe_id}"

    # StoreKit sandbox purchases can reach the production Vitals API. Resolve
    # production first and then sandbox rather than trusting an environment
    # value supplied by the phone.
    for environment in ("production", "sandbox"):
        response = _apple_server_get(path, environment)
        if response.status_code == 404:
            continue

        body = _apple_response_or_error(response, "Apple transaction was not found.")
        transaction = _decode_apple_server_jws_payload(
            body.get("signedTransactionInfo")
        )
        if transaction.get("bundleId") != APPLE_BUNDLE_ID:
            raise HTTPException(status_code=403, detail="Apple transaction belongs to another app")
        return environment, transaction

    raise HTTPException(status_code=400, detail="Apple transaction was not found.")


def _fetch_apple_subscription_status(
    original_transaction_id: str,
    environment: str,
) -> tuple[int, dict, dict]:
    safe_id = requests.utils.quote(original_transaction_id, safe="")
    response = _apple_server_get(
        f"/inApps/v1/subscriptions/{safe_id}",
        environment,
    )
    body = _apple_response_or_error(
        response,
        "Apple subscription was not found."
    )

    if body.get("bundleId") != APPLE_BUNDLE_ID:
        raise HTTPException(status_code=403, detail="Apple subscription belongs to another app")

    for group in body.get("data") or []:
        for item in group.get("lastTransactions") or []:
            if str(item.get("originalTransactionId") or "") != original_transaction_id:
                continue

            transaction = _decode_apple_server_jws_payload(
                item.get("signedTransactionInfo")
            )
            renewal = (
                _decode_apple_server_jws_payload(item.get("signedRenewalInfo"))
                if item.get("signedRenewalInfo")
                else {}
            )
            try:
                status = int(item.get("status"))
            except (TypeError, ValueError):
                raise HTTPException(
                    status_code=502,
                    detail="Apple returned an invalid subscription status."
                )
            return status, transaction, renewal

    raise HTTPException(
        status_code=502,
        detail="Apple did not return current status for the subscription."
    )


def _verify_apple_purchase(transaction_id: Optional[str], user_id: str) -> dict:
    _require_any_configured_store_product("apple")
    transaction_id = (transaction_id or "").strip()
    if not transaction_id:
        raise HTTPException(status_code=400, detail="transaction_id is required for Apple")

    environment, submitted_transaction = _fetch_apple_transaction(transaction_id)
    expected_binding = _billing_account_binding_token("apple", user_id)

    submitted_binding = str(submitted_transaction.get("appAccountToken") or "").lower()
    if submitted_binding != expected_binding.lower():
        raise HTTPException(
            status_code=403,
            detail="Apple purchase is not bound to this Vitals account."
        )

    original_transaction_id = str(
        submitted_transaction.get("originalTransactionId") or ""
    ).strip()
    if not original_transaction_id:
        raise HTTPException(
            status_code=502,
            detail="Apple transaction is missing its original transaction id."
        )

    status_code, latest_transaction, renewal = _fetch_apple_subscription_status(
        original_transaction_id,
        environment,
    )

    if latest_transaction.get("bundleId") != APPLE_BUNDLE_ID:
        raise HTTPException(status_code=403, detail="Apple subscription belongs to another app")

    latest_binding = str(latest_transaction.get("appAccountToken") or "").lower()
    if latest_binding != expected_binding.lower():
        raise HTTPException(
            status_code=403,
            detail="Apple subscription is not bound to this Vitals account."
        )

    if str(latest_transaction.get("originalTransactionId") or "") != original_transaction_id:
        raise HTTPException(
            status_code=502,
            detail="Apple returned a mismatched subscription."
        )

    product = _catalog_product_for_verified_purchase(
        "apple",
        latest_transaction.get("productId"),
    )

    store_status = {
        1: "active",
        2: "expired",
        3: "billing_retry",
        4: "grace",
        5: "revoked",
    }.get(status_code)
    if not store_status:
        raise HTTPException(status_code=502, detail="Apple returned an unknown subscription state")

    auto_renew_raw = renewal.get("autoRenewStatus")
    auto_renew_enabled = (
        None if auto_renew_raw is None else int(auto_renew_raw) == 1
    )

    expires_at = _apple_datetime_from_millis(latest_transaction.get("expiresDate"))
    if expires_at is None:
        raise HTTPException(status_code=502, detail="Apple subscription has no expiration date")

    return {
        "provider": "apple",
        "plan": product["plan"],
        "billing_period": product["billing_period"],
        "product_id": product["product_id"],
        "base_plan_id": None,
        "external_subscription_ref": original_transaction_id,
        "linked_external_subscription_ref": None,
        "latest_transaction_id": str(latest_transaction.get("transactionId") or "") or None,
        "store_environment": environment,
        "store_status": store_status,
        "auto_renew_enabled": auto_renew_enabled,
        "purchased_at": _apple_datetime_from_millis(
            latest_transaction.get("originalPurchaseDate")
            or latest_transaction.get("purchaseDate")
        ),
        "expires_at": expires_at,
    }


def _google_play_credentials():
    try:
        if GOOGLE_PLAY_SERVICE_ACCOUNT_JSON:
            info = json.loads(GOOGLE_PLAY_SERVICE_ACCOUNT_JSON)
            return service_account.Credentials.from_service_account_info(
                info,
                scopes=[GOOGLE_PLAY_SCOPE],
            )

        if GOOGLE_PLAY_SERVICE_ACCOUNT_PATH:
            return service_account.Credentials.from_service_account_file(
                GOOGLE_PLAY_SERVICE_ACCOUNT_PATH,
                scopes=[GOOGLE_PLAY_SCOPE],
            )
    except Exception:
        raise HTTPException(
            status_code=503,
            detail="Google Play billing verification credentials are not usable."
        )

    raise HTTPException(
        status_code=503,
        detail="Google Play billing verification credentials are not configured."
    )


def _verify_google_purchase(purchase_token: Optional[str], user_id: str) -> dict:
    _require_any_configured_store_product("google")
    purchase_token = (purchase_token or "").strip()
    if not purchase_token:
        raise HTTPException(status_code=400, detail="purchase_token is required for Google")

    credentials = _google_play_credentials()
    session = google.auth.transport.requests.AuthorizedSession(credentials)
    safe_package = requests.utils.quote(GOOGLE_PLAY_PACKAGE_NAME, safe="")
    safe_token = requests.utils.quote(purchase_token, safe="")
    url = (
        "https://androidpublisher.googleapis.com/androidpublisher/v3/"
        f"applications/{safe_package}/purchases/subscriptionsv2/tokens/{safe_token}"
    )

    try:
        response = session.get(url, timeout=15)
    except requests.RequestException:
        raise HTTPException(
            status_code=503,
            detail="Google Play billing verification is temporarily unavailable."
        )
    finally:
        try:
            session.close()
        except Exception:
            pass

    if response.status_code == 404:
        raise HTTPException(status_code=400, detail="Google Play purchase token was not found.")
    if response.status_code in (401, 403):
        raise HTTPException(
            status_code=503,
            detail="Google Play billing verification credentials were rejected."
        )
    if response.status_code == 429 or response.status_code >= 500:
        raise HTTPException(
            status_code=503,
            detail="Google Play billing verification is temporarily unavailable."
        )
    if response.status_code != 200:
        raise HTTPException(status_code=400, detail="Google Play rejected the purchase token.")

    try:
        body = response.json()
    except ValueError:
        raise HTTPException(status_code=502, detail="Google Play returned invalid JSON")

    expected_binding = _billing_account_binding_token("google", user_id)
    identifiers = body.get("externalAccountIdentifiers") or {}
    actual_binding = str(identifiers.get("obfuscatedExternalAccountId") or "")
    if actual_binding != expected_binding:
        raise HTTPException(
            status_code=403,
            detail="Google Play purchase is not bound to this Vitals account."
        )

    candidates = []
    for item in body.get("lineItems") or []:
        product_id = item.get("productId")
        offer_details = item.get("offerDetails") or {}
        base_plan_id = offer_details.get("basePlanId")
        try:
            product = _catalog_product_for_verified_purchase(
                "google",
                product_id,
                base_plan_id,
            )
        except HTTPException as exc:
            if exc.status_code == 409:
                continue
            raise

        expires_at = _parse_store_datetime(item.get("expiryTime"))
        if expires_at is None:
            continue
        candidates.append((expires_at, item, product))

    if not candidates:
        raise HTTPException(
            status_code=409,
            detail="Google Play purchase does not contain a configured Vitals subscription."
        )

    expires_at, item, product = max(candidates, key=lambda candidate: candidate[0])
    state = str(body.get("subscriptionState") or "")
    store_status = {
        "SUBSCRIPTION_STATE_ACTIVE": "active",
        "SUBSCRIPTION_STATE_CANCELED": "canceled",
        "SUBSCRIPTION_STATE_IN_GRACE_PERIOD": "grace",
        "SUBSCRIPTION_STATE_ON_HOLD": "on_hold",
        "SUBSCRIPTION_STATE_PAUSED": "paused",
        "SUBSCRIPTION_STATE_EXPIRED": "expired",
        "SUBSCRIPTION_STATE_PENDING": "pending",
        "SUBSCRIPTION_STATE_PENDING_PURCHASE_CANCELED": "expired",
    }.get(state)
    if not store_status:
        raise HTTPException(
            status_code=502,
            detail="Google Play returned an unknown subscription state."
        )

    auto_renewing = item.get("autoRenewingPlan") or {}
    auto_renew_enabled = auto_renewing.get("autoRenewEnabled")
    if auto_renew_enabled is not None:
        auto_renew_enabled = bool(auto_renew_enabled)

    return {
        "provider": "google",
        "plan": product["plan"],
        "billing_period": product["billing_period"],
        "product_id": product["product_id"],
        "base_plan_id": product["base_plan_id"],
        "external_subscription_ref": purchase_token,
        "linked_external_subscription_ref": body.get("linkedPurchaseToken"),
        "latest_transaction_id": item.get("latestSuccessfulOrderId"),
        "store_environment": "test" if body.get("testPurchase") is not None else "production",
        "store_status": store_status,
        "auto_renew_enabled": auto_renew_enabled,
        "purchased_at": _parse_store_datetime(body.get("startTime")),
        "expires_at": expires_at,
    }


def _verify_store_purchase(body: BillingVerifyRequest, user_id: str) -> dict:
    if body.provider == "apple":
        return _verify_apple_purchase(body.transaction_id, user_id)
    if body.provider == "google":
        return _verify_google_purchase(body.purchase_token, user_id)
    raise HTTPException(status_code=400, detail="Unsupported billing provider")


def _require_billing_claim_permission(
    cur,
    household_id: str,
    user_id: str,
    provider: str,
) -> dict:
    entitlement = get_household_entitlement(cur, household_id, user_id)

    if entitlement["is_founder"] or entitlement["is_beta"]:
        raise HTTPException(
            status_code=403,
            detail="Complimentary Founder/Beta households do not use store billing."
        )

    cur.execute("""
        SELECT billing_owner_user_id, billing_provider, subscription_status
        FROM households
        WHERE household_id = %s
    """, (household_id,))
    row = cur.fetchone()
    if not row:
        raise HTTPException(status_code=404, detail="Household not found")

    billing_owner_user_id, current_provider, subscription_status = row
    if billing_owner_user_id and str(billing_owner_user_id) != str(user_id):
        raise HTTPException(
            status_code=403,
            detail="This household's store subscription belongs to a different billing owner."
        )

    if not (
        entitlement["can_start_purchase"]
        or entitlement["can_manage_billing"]
    ):
        raise HTTPException(
            status_code=403,
            detail="You are not authorized to verify purchases for this household."
        )

    if (
        current_provider
        and current_provider != provider
        and subscription_status in ("active", "grace")
    ):
        raise HTTPException(
            status_code=409,
            detail="This household already has an active subscription from another store."
        )

    return entitlement


def _prepare_billing_subscription_upsert(
    cur,
    household_id: str,
    verified: dict,
) -> tuple[bool, Optional[datetime]]:
    references = [
        ref for ref in (
            verified["external_subscription_ref"],
            verified.get("linked_external_subscription_ref"),
        )
        if ref
    ]

    cur.execute("""
        SELECT DISTINCT household_id
        FROM billing_subscriptions
        WHERE provider = %s
          AND (
              external_subscription_ref = ANY(%s)
              OR linked_external_subscription_ref = ANY(%s)
          )
    """, (verified["provider"], references, references))
    for row in cur.fetchall():
        if str(row[0]) != str(household_id):
            raise HTTPException(
                status_code=409,
                detail="This store subscription is already linked to another Vitals household."
            )

    cur.execute("""
        SELECT store_status, expires_at
        FROM billing_subscriptions
        WHERE provider = %s
          AND household_id = %s
          AND external_subscription_ref = ANY(%s)
        ORDER BY last_verified_at DESC NULLS LAST, updated_at DESC
        LIMIT 1
    """, (verified["provider"], household_id, references))
    prior = cur.fetchone()

    prior_paid = bool(
        prior
        and prior[0] in ("active", "canceled", "grace", "billing_retry")
    )
    prior_expires_at = prior[1] if prior else None
    return prior_paid, prior_expires_at


def _upsert_billing_subscription(
    cur,
    household_id: str,
    user_id: str,
    verified: dict,
) -> str:
    cur.execute("""
        INSERT INTO billing_subscriptions (
            household_id,
            billing_owner_user_id,
            provider,
            plan,
            billing_period,
            product_id,
            base_plan_id,
            external_subscription_ref,
            linked_external_subscription_ref,
            latest_transaction_id,
            store_environment,
            store_status,
            auto_renew_enabled,
            purchased_at,
            expires_at,
            last_verified_at,
            updated_at
        )
        VALUES (
            %s, %s, %s, %s, %s, %s, %s, %s, %s,
            %s, %s, %s, %s, %s, %s, now(), now()
        )
        ON CONFLICT (provider, external_subscription_ref)
        DO UPDATE SET
            household_id = EXCLUDED.household_id,
            billing_owner_user_id = EXCLUDED.billing_owner_user_id,
            plan = EXCLUDED.plan,
            billing_period = EXCLUDED.billing_period,
            product_id = EXCLUDED.product_id,
            base_plan_id = EXCLUDED.base_plan_id,
            linked_external_subscription_ref = EXCLUDED.linked_external_subscription_ref,
            latest_transaction_id = EXCLUDED.latest_transaction_id,
            store_environment = EXCLUDED.store_environment,
            store_status = EXCLUDED.store_status,
            auto_renew_enabled = EXCLUDED.auto_renew_enabled,
            purchased_at = EXCLUDED.purchased_at,
            expires_at = EXCLUDED.expires_at,
            last_verified_at = now(),
            updated_at = now()
        RETURNING billing_subscription_id
    """, (
        household_id,
        user_id,
        verified["provider"],
        verified["plan"],
        verified["billing_period"],
        verified["product_id"],
        verified.get("base_plan_id"),
        verified["external_subscription_ref"],
        verified.get("linked_external_subscription_ref"),
        verified.get("latest_transaction_id"),
        verified["store_environment"],
        verified["store_status"],
        verified.get("auto_renew_enabled"),
        verified.get("purchased_at"),
        verified.get("expires_at"),
    ))
    return str(cur.fetchone()[0])


def _apply_verified_billing_entitlement(
    cur,
    household_id: str,
    user_id: str,
    verified: dict,
    prior_paid: bool,
    prior_expires_at: Optional[datetime],
) -> bool:
    """
    Apply only STORE-VERIFIED access states.

    A failed initial purchase never receives Vitals grace. Grace is available
    only when this household already had a paid verification for the same
    subscription lineage. Re-verifying cannot keep extending the five-day
    courtesy window.
    """
    now = datetime.now(timezone.utc)
    store_status = verified["store_status"]
    expires_at = verified.get("expires_at")
    plan = verified["plan"]
    plan_limit = EFFECTIVE_PLAN_PATIENT_LIMITS[plan]

    paid_through_active = bool(
        expires_at
        and expires_at > now
        and store_status in ("active", "canceled")
    )

    if paid_through_active:
        cancel_at_period_end = bool(
            store_status == "canceled"
            or verified.get("auto_renew_enabled") is False
        )
        cur.execute("""
            UPDATE households
            SET tier = %s,
                subscription_status = 'active',
                patient_limit = %s,
                billing_owner_user_id = %s,
                billing_provider = %s,
                billing_product_id = %s,
                subscription_started_at = COALESCE(
                    subscription_started_at,
                    %s,
                    now()
                ),
                subscription_ends_at = %s,
                grace_ends_at = NULL,
                cancel_at_period_end = %s,
                entitlement_updated_at = now()
            WHERE household_id = %s
        """, (
            plan,
            plan_limit,
            user_id,
            verified["provider"],
            verified["product_id"],
            verified.get("purchased_at"),
            expires_at,
            cancel_at_period_end,
            household_id,
        ))
        return cur.rowcount == 1

    if store_status in ("grace", "billing_retry") and prior_paid:
        cur.execute("""
            SELECT subscription_status, grace_ends_at,
                   billing_owner_user_id, billing_provider
            FROM households
            WHERE household_id = %s
        """, (household_id,))
        current = cur.fetchone()
        if not current:
            raise HTTPException(status_code=404, detail="Household not found")

        current_status, existing_grace_end, billing_owner_id, current_provider = current
        same_existing_grace = bool(
            current_status == "grace"
            and existing_grace_end is not None
            and billing_owner_id is not None
            and str(billing_owner_id) == str(user_id)
            and current_provider == verified["provider"]
        )

        if same_existing_grace:
            grace_end = existing_grace_end
        else:
            grace_anchor = prior_expires_at or expires_at or now
            grace_end = grace_anchor + timedelta(days=VITALS_BILLING_GRACE_DAYS)

        if grace_end > now:
            cur.execute("""
                UPDATE households
                SET tier = %s,
                    subscription_status = 'grace',
                    patient_limit = %s,
                    billing_owner_user_id = %s,
                    billing_provider = %s,
                    billing_product_id = %s,
                    subscription_started_at = COALESCE(
                        subscription_started_at,
                        %s,
                        now()
                    ),
                    subscription_ends_at = %s,
                    grace_ends_at = %s,
                    cancel_at_period_end = false,
                    entitlement_updated_at = now()
                WHERE household_id = %s
            """, (
                plan,
                plan_limit,
                user_id,
                verified["provider"],
                verified["product_id"],
                verified.get("purchased_at"),
                prior_expires_at or expires_at,
                grace_end,
                household_id,
            ))
            return cur.rowcount == 1

    # Pending/failed INITIAL purchases do not mutate entitlement. For a
    # previously paid subscription, terminal or inaccessible store states end
    # paid access and release billing ownership so a new purchase can begin.
    terminal_state = (
        store_status in ("expired", "revoked", "on_hold", "paused")
        or (store_status in ("active", "canceled") and not paid_through_active)
        or (
            store_status in ("grace", "billing_retry")
            and prior_paid
        )
    )

    if terminal_state and prior_paid:
        cur.execute("""
            UPDATE households
            SET subscription_status = 'basic',
                billing_owner_user_id = NULL,
                billing_provider = NULL,
                billing_product_id = NULL,
                subscription_ends_at = %s,
                grace_ends_at = NULL,
                cancel_at_period_end = false,
                entitlement_updated_at = now()
            WHERE household_id = %s
              AND billing_owner_user_id = %s
              AND billing_provider = %s
        """, (
            expires_at or prior_expires_at,
            household_id,
            user_id,
            verified["provider"],
        ))
        return cur.rowcount == 1

    return False


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

def _spo2_local_datetime(row: dict) -> datetime:
    """
    Convert recorded_at to the reading's local wall-clock time. Prefer the
    per-reading UTC offset captured by the client; fall back to the same
    America/Chicago approximation used by the Heart Rate engine for legacy
    rows that predate local_offset_minutes.
    """
    if row["local_offset_minutes"] is not None:
        return row["recorded_at"] + timedelta(minutes=row["local_offset_minutes"])
    return row["recorded_at"].astimezone(ZoneInfo("America/Chicago"))


def run_spo2_analysis(rows: list, baseline_rows: list | None = None) -> dict | None:
    """
    SpO2 Analysis Engine.

    This intentionally treats manually-entered pulse-oximetry values as
    discrete observations, not a continuous signal. Counts and percentages
    below are therefore percentages of LOGGED READINGS only. We do not use
    Rosendaal/trapezoidal interpolation to claim "time below" a threshold.

    Current database limitation:
      - no spo2_context table yet, so resting/activity state, symptom tags,
        supplemental-oxygen state/flow, device/source metadata, and explicit
        measurement-quality flags are not available.
      - analyses that require those fields are returned as unavailable in
        data_support rather than inferred.

    baseline_rows is a fixed 37-day lookback supplied by the registry:
      recent   = last 7 days
      baseline = days 8-37 ago
    """

    if not rows:
        return None

    def _row_dict(r):
        return {
            "recorded_at":          r[0],
            "local_offset_minutes": r[1],
            "spo2":                 int(r[2]),
            "heart_rate":           r[3],
            "systolic":             r[4],
            "diastolic":            r[5],
            "temperature":          float(r[6]) if r[6] is not None else None,
            "weight":               float(r[7]) if r[7] is not None else None,
            "blood_glucose":        r[8],
        }

    rows_d = [_row_dict(r) for r in rows]
    latest = rows_d[-1]
    values = [r["spo2"] for r in rows_d]

    # Default descriptive reference thresholds only. These are deliberately
    # surfaced as metadata so a future patient/clinician-specific target can
    # replace them without rewriting the analysis engine.
    LOW_THRESHOLD = 95
    MODERATE_LOW_THRESHOLD = 92
    MARKED_LOW_THRESHOLD = 88

    # Spec's own default (§6.8, W_confirm) is 10 minutes — NOT Heart Rate's
    # 15-minute episode window. Defined once here so the grouping logic and
    # the value reported back in the result can never drift apart again.
    CONFIRMATION_WINDOW_MINUTES = 10

    linked_context = {}
    if latest["heart_rate"] is not None:
        linked_context["heart_rate"] = latest["heart_rate"]
    if latest["systolic"] is not None and latest["diastolic"] is not None:
        linked_context["blood_pressure"] = {
            "systolic": latest["systolic"],
            "diastolic": latest["diastolic"],
        }
    if latest["temperature"] is not None:
        linked_context["temperature"] = latest["temperature"]
    if latest["weight"] is not None:
        linked_context["weight"] = latest["weight"]
    if latest["blood_glucose"] is not None:
        linked_context["blood_glucose"] = latest["blood_glucose"]

    result = {
        "spo2":              latest["spo2"],
        "recorded_at":       latest["recorded_at"].isoformat(),
        "measurement_context": "unknown",
        "source_type":       "unknown",
        "linked_context":    linked_context,
        "reading_count":     len(rows_d),
        "target_profile": {
            "low_threshold":        LOW_THRESHOLD,
            "moderate_threshold":   MODERATE_LOW_THRESHOLD,
            "marked_low_threshold": MARKED_LOW_THRESHOLD,
            "source":               "reference_default",
            "patient_specific":     False,
        },
    }

    distinct_days = len({_spo2_local_datetime(r).date() for r in rows_d})
    span_days = (
        (rows_d[-1]["recorded_at"] - rows_d[0]["recorded_at"]).total_seconds() / 86400
        if len(rows_d) > 1 else 0.0
    )

    # ---------------------------------------------------------
    # Central tendency and observed range
    # ---------------------------------------------------------
    if len(values) >= 3 and distinct_days >= 3:
        result["spot_summary"] = {
            "n":             len(values),
            "distinct_days": distinct_days,
            "mean":          round(statistics.mean(values), 1),
            "median":        round(statistics.median(values), 1),
            "min":           min(values),
            "max":           max(values),
            "range":         max(values) - min(values),
        }
    else:
        result["spot_summary"] = None

    # ---------------------------------------------------------
    # Between-reading dispersion (NOT continuous variability)
    # ---------------------------------------------------------
    if len(values) >= 5:
        q1 = float(np.percentile(values, 25))
        q3 = float(np.percentile(values, 75))
        result["dispersion"] = {
            "n":   len(values),
            "sd":  round(statistics.stdev(values), 1),
            "iqr": round(q3 - q1, 1),
            "q1":  round(q1, 1),
            "q3":  round(q3, 1),
        }
    else:
        result["dispersion"] = None

    # ---------------------------------------------------------
    # OLS trend
    # ---------------------------------------------------------
    t = v = None
    if rows_d:
        origin = rows_d[0]["recorded_at"]
        t = np.array([
            (r["recorded_at"] - origin).total_seconds() / 86400
            for r in rows_d
        ])
        v = np.array([float(r["spo2"]) for r in rows_d])

    def _spo2_trend_label(slope: float, significant: bool) -> str:
        # Vitals presentation gate, not a clinical threshold. A tiny
        # non-zero regression coefficient should not create an alarming
        # "rising/falling" label from rounding noise.
        epsilon = 0.05
        if slope > epsilon and significant:
            return "rising_significant"
        if slope > epsilon:
            return "rising"
        if slope < -epsilon and significant:
            return "falling_significant"
        if slope < -epsilon:
            return "falling"
        return "stable"

    if len(values) >= 5 and distinct_days >= 5 and span_days >= 7:
        if np.std(v) == 0:
            result["trend"] = {
                "slope_pct_points_per_day": 0.0,
                "modeled_change":           0.0,
                "span_days":                round(span_days, 1),
                "r2":                       None,
                "p_value":                  None,
                "trend_label":              "stable",
                "consistency":              None,
            }
        else:
            slope, _, r_val, p_val, _ = stats.linregress(t, v)
            r2 = float(r_val ** 2)
            show_p = len(values) >= 8 and span_days >= 14
            significant = bool(show_p and p_val < 0.05)

            result["trend"] = {
                "slope_pct_points_per_day": round(float(slope), 3),
                "modeled_change":           round(float(slope) * (t.max() - t.min()), 1),
                "span_days":                round(span_days, 1),
                "r2":                       round(r2, 2),
                "p_value":                  round(float(p_val), 3) if show_p else None,
                "trend_label":              _spo2_trend_label(float(slope), significant),
                "consistency":              _consistency_label(r2),
            }
    else:
        result["trend"] = None

    # LOESS is visualization support only. It does not create a clinical
    # interpretation independently of the OLS/data-support gates above.
    if len(values) >= 7 and span_days >= 7:
        smoothed = loess_smooth(t, v, frac=0.6)
        result["loess"] = [
            {
                "recorded_at": r["recorded_at"].isoformat(),
                "smoothed_spo2": round(float(s), 1),
            }
            for r, s in zip(rows_d, smoothed)
        ]
    else:
        result["loess"] = None

    # ---------------------------------------------------------
    # Personal baseline deviation
    # ---------------------------------------------------------
    result["baseline_deviation"] = None
    if baseline_rows:
        baseline_d = [_row_dict(r) for r in baseline_rows]
        now_utc = datetime.now(timezone.utc)

        recent_set = [
            r for r in baseline_d
            if now_utc - timedelta(days=7) <= r["recorded_at"] <= now_utc
        ]
        baseline_set = [
            r for r in baseline_d
            if now_utc - timedelta(days=37) <= r["recorded_at"] <= now_utc - timedelta(days=8)
        ]

        baseline_vals = [r["spo2"] for r in baseline_set]
        recent_vals = [r["spo2"] for r in recent_set]
        baseline_days = len({_spo2_local_datetime(r).date() for r in baseline_set})
        recent_days = len({_spo2_local_datetime(r).date() for r in recent_set})

        if (
            len(baseline_vals) >= 7 and baseline_days >= 7
            and len(recent_vals) >= 3 and recent_days >= 3
        ):
            baseline_median = statistics.median(baseline_vals)
            recent_median = statistics.median(recent_vals)

            result["baseline_deviation"] = {
                "baseline_median":  round(baseline_median, 1),
                "recent_median":    round(recent_median, 1),
                "baseline_n":       len(baseline_vals),
                "recent_n":         len(recent_vals),
                "delta_pct_points": round(recent_median - baseline_median, 1),
            }

    # ---------------------------------------------------------
    # Low-observation analysis
    # ---------------------------------------------------------
    low_rows = [r for r in rows_d if r["spo2"] < LOW_THRESHOLD]
    moderate_rows = [r for r in rows_d if r["spo2"] < MODERATE_LOW_THRESHOLD]
    marked_rows = [r for r in rows_d if r["spo2"] < MARKED_LOW_THRESHOLD]
    show_pct = len(rows_d) >= 5

    def _reading_payload(r):
        return {
            "recorded_at": r["recorded_at"].isoformat(),
            "spo2":        r["spo2"],
        }

    result["low_observations"] = {
        "threshold": LOW_THRESHOLD,
        "count": len(low_rows),
        "pct_of_logged_readings": (
            round(100 * len(low_rows) / len(rows_d), 1) if show_pct else None
        ),
        "readings": [_reading_payload(r) for r in low_rows],
    }

    # Reference bands are descriptive counts of logged readings only.
    # They intentionally avoid an overall "hypoxemia classification"
    # based on the average.
    band_defs = [
        ("at_or_above_95", lambda x: x >= 95),
        ("92_to_94",       lambda x: 92 <= x < 95),
        ("88_to_91",       lambda x: 88 <= x < 92),
        ("below_88",       lambda x: x < 88),
    ]
    result["reference_bands"] = {
        name: {
            "count": sum(1 for x in values if predicate(x)),
            "pct_of_logged_readings": (
                round(
                    100 * sum(1 for x in values if predicate(x)) / len(values),
                    1,
                )
                if show_pct else None
            ),
        }
        for name, predicate in band_defs
    }

    # "Confirmed" here is explicitly a Vitals engineering grouping rule:
    # at least two below-target observations no more than
    # CONFIRMATION_WINDOW_MINUTES apart. It is not a diagnosis and does
    # not imply continuous desaturation.
    confirmed_groups = []
    if low_rows:
        ordered = sorted(low_rows, key=lambda r: r["recorded_at"])
        group = [ordered[0]]

        def _flush_confirmed(g):
            if len(g) < 2:
                return
            confirmed_groups.append({
                "first_recorded_at": g[0]["recorded_at"].isoformat(),
                "last_recorded_at":  g[-1]["recorded_at"].isoformat(),
                "reading_count":     len(g),
                "nadir":             min(r["spo2"] for r in g),
                "readings":          [_reading_payload(r) for r in g],
            })

        for r in ordered[1:]:
            gap_minutes = (
                r["recorded_at"] - group[-1]["recorded_at"]
            ).total_seconds() / 60
            if gap_minutes <= CONFIRMATION_WINDOW_MINUTES:
                group.append(r)
            else:
                _flush_confirmed(group)
                group = [r]
        _flush_confirmed(group)

    result["confirmed_low_observations"] = {
        "confirmation_rule_minutes": CONFIRMATION_WINDOW_MINUTES,
        "episode_count": len(confirmed_groups),
        "episodes": confirmed_groups,
    }

    result["marked_low_observations"] = {
        "threshold": MARKED_LOW_THRESHOLD,
        "count": len(marked_rows),
        "pct_of_logged_readings": (
            round(100 * len(marked_rows) / len(rows_d), 1) if show_pct else None
        ),
        "readings": [_reading_payload(r) for r in marked_rows],
    }

    # ---------------------------------------------------------
    # Time-of-day pattern
    # ---------------------------------------------------------
    bucket_rows = {
        "morning": [],
        "afternoon": [],
        "evening": [],
        "overnight": [],
    }

    for r in rows_d:
        hour = _spo2_local_datetime(r).hour
        if 5 <= hour < 12:
            bucket_rows["morning"].append(r)
        elif 12 <= hour < 17:
            bucket_rows["afternoon"].append(r)
        elif 17 <= hour < 22:
            bucket_rows["evening"].append(r)
        else:
            bucket_rows["overnight"].append(r)

    def _bucket_stats(items):
        if len(items) < 2:
            return None
        vals = [r["spo2"] for r in items]
        return {
            "n":             len(vals),
            "distinct_days": len({_spo2_local_datetime(r).date() for r in items}),
            "mean":          round(statistics.mean(vals), 1),
            "median":        round(statistics.median(vals), 1),
            "min":           min(vals),
            "max":           max(vals),
        }

    buckets = {name: _bucket_stats(items) for name, items in bucket_rows.items()}
    usable_buckets = {
        name: stats_block for name, stats_block in buckets.items()
        if stats_block is not None
    }

    pattern_summary = None
    if len(usable_buckets) >= 2:
        highest = max(usable_buckets.items(), key=lambda x: x[1]["median"])
        lowest = min(usable_buckets.items(), key=lambda x: x[1]["median"])
        median_delta = highest[1]["median"] - lowest[1]["median"]

        # 2 percentage points is a Vitals presentation gate, not a
        # diagnostic threshold.
        if median_delta >= 2:
            pattern_summary = {
                "highest_period": highest[0],
                "lowest_period":  lowest[0],
                "median_delta":   round(median_delta, 1),
            }

    result["time_of_day"] = {
        "buckets": buckets,
        "pattern_summary": pattern_summary,
    }

    # ---------------------------------------------------------
    # Same-event context for low observations
    # ---------------------------------------------------------
    low_context = []
    for r in low_rows:
        context = {}
        if r["heart_rate"] is not None:
            context["heart_rate"] = r["heart_rate"]
        if r["systolic"] is not None and r["diastolic"] is not None:
            context["blood_pressure"] = {
                "systolic": r["systolic"],
                "diastolic": r["diastolic"],
            }
        if r["temperature"] is not None:
            context["temperature"] = r["temperature"]

        low_context.append({
            "recorded_at": r["recorded_at"].isoformat(),
            "spo2":        r["spo2"],
            "context":     context,
        })

    result["cross_vital_context"] = {
        "low_observations": low_context,
    }

    # Optional Pearson correlations for physician-facing context only.
    # n>=10 paired values across >=7 days and non-zero variance.
    correlations = {}
    pair_specs = [
        ("heart_rate", lambda r: r["heart_rate"]),
        ("temperature", lambda r: r["temperature"]),
        ("systolic", lambda r: r["systolic"]),
        ("diastolic", lambda r: r["diastolic"]),
    ]

    for name, value_fn in pair_specs:
        pairs = [r for r in rows_d if value_fn(r) is not None]
        if len(pairs) < 10:
            continue
        pair_span = (
            pairs[-1]["recorded_at"] - pairs[0]["recorded_at"]
        ).total_seconds() / 86400
        if pair_span < 7:
            continue

        spo2_vals = np.array([float(r["spo2"]) for r in pairs])
        other_vals = np.array([float(value_fn(r)) for r in pairs])

        if np.std(spo2_vals) == 0 or np.std(other_vals) == 0:
            continue

        correlations[name] = round(
            float(np.corrcoef(spo2_vals, other_vals)[0, 1]),
            2,
        )

    result["cross_vital_correlations"] = correlations if correlations else None

    # These are intentionally unavailable until the schema captures them.
    result["symptom_association"] = None
    result["oxygen_context"] = None

    # ---------------------------------------------------------
    # Data support / confidence
    # ---------------------------------------------------------
    n = len(values)

    if n == 0:
        support_state = "none"
    elif n >= 8 and span_days >= 14:
        support_state = "significance"
    elif n >= 7 and span_days >= 7:
        support_state = "loess"
    elif n >= 5 and distinct_days >= 5 and span_days >= 7:
        support_state = "trend"
    elif n >= 3 and distinct_days >= 3:
        support_state = "descriptive"
    else:
        support_state = "snapshot"

    calendar_days_approx = max(int(span_days) + 1, 1)
    coverage_pct = round(100 * distinct_days / calendar_days_approx, 1)

    unavailable = []

    if result["spot_summary"] is None:
        unavailable.append({
            "analysis": "spot_summary",
            "reason": (
                f"needs >=3 readings across >=3 distinct days "
                f"(have n={n}, days={distinct_days})"
            ),
        })

    if result["dispersion"] is None:
        unavailable.append({
            "analysis": "dispersion",
            "reason": f"needs >=5 readings (have n={n})",
        })

    if result["trend"] is None:
        unavailable.append({
            "analysis": "trend",
            "reason": (
                f"needs >=5 readings across >=5 distinct days and >=7 day span "
                f"(have n={n}, days={distinct_days}, span={round(span_days, 1)})"
            ),
        })
    elif result["trend"].get("p_value") is None:
        unavailable.append({
            "analysis": "trend.p_value",
            "reason": (
                f"needs >=8 readings and >=14 day span "
                f"(have n={n}, span={round(span_days, 1)})"
            ),
        })

    if result["loess"] is None:
        unavailable.append({
            "analysis": "loess",
            "reason": (
                f"needs >=7 readings and >=7 day span "
                f"(have n={n}, span={round(span_days, 1)})"
            ),
        })

    if result["baseline_deviation"] is None:
        unavailable.append({
            "analysis": "baseline_deviation",
            "reason": (
                "needs its own separate baseline (>=7 readings/>=7 days, "
                "8-37 days ago) and recent (>=3 readings/>=3 days, last 7 days) "
                "data — independent of this window"
            ),
        })

    if result["time_of_day"]["pattern_summary"] is None:
        unavailable.append({
            "analysis": "time_of_day",
            "reason": (
                "needs at least two time-of-day buckets with >=2 readings each "
                "and a >=2 percentage-point median difference"
            ),
        })

    unavailable.append({
        "analysis": "symptom_association",
        "reason": "SpO2 symptom/context fields are not collected yet",
    })
    unavailable.append({
        "analysis": "oxygen_context",
        "reason": "supplemental-oxygen state/flow is not collected yet",
    })

    result["data_support"] = {
        "support_state":             support_state,
        "n":                         n,
        "distinct_days":             distinct_days,
        "span_days":                 round(span_days, 1),
        "distinct_day_coverage_pct": coverage_pct,
        "unavailable_analyses":      unavailable,
    }

    return result


def _temperature_local_datetime(row: dict) -> datetime:
    """
    Convert recorded_at to the reading's local wall-clock time. Prefer the
    per-reading UTC offset captured by the client; use the same
    America/Chicago fallback as the other analysis engines for legacy rows.
    """
    if row["local_offset_minutes"] is not None:
        return row["recorded_at"] + timedelta(minutes=row["local_offset_minutes"])
    return row["recorded_at"].astimezone(ZoneInfo("America/Chicago"))


def run_temperature_analysis(rows: list, baseline_rows: list | None = None) -> dict | None:
    """
    Dedicated Temperature Analysis Engine.

    Temperature is treated as an episode-centric, site-aware vital. Sparse
    manual readings support observed counts, peaks, distinct febrile days,
    episode grouping, and same-site baseline comparisons. They DO NOT support
    inferred continuous fever duration or interpolated "time in fever."

    Current product storage is Fahrenheit. The engine normalizes to Celsius
    internally/output-side for deterministic dual-unit representation while
    preserving the original Fahrenheit value. temperature_site may be
    'unknown' for pre-migration rows.

    baseline_rows is a fixed 60-day implementation lookback used only to give
    the same-site baseline enough history. The clinical/data-sufficiency gate
    remains >=7 eligible readings on >=7 distinct days spanning >=14 days.
    The 60-day fetch window is a Vitals engineering implementation choice, not
    a clinical threshold.
    """
    if not rows:
        return None

    FEVER_F = 100.4
    HYPOTHERMIA_F = 95.0
    EPISODE_GAP_HOURS = 24.0

    def _to_c(value_f: float) -> float:
        return (value_f - 32.0) * 5.0 / 9.0

    def _site(value) -> str:
        value = (value or "unknown").strip().lower()
        return value if value in {
            "oral", "rectal", "axillary", "tympanic",
            "temporal", "other", "unknown"
        } else "unknown"

    def _row_dict(r):
        return {
            "recorded_at":          r[0],
            "local_offset_minutes": r[1],
            "temperature_f":        float(r[2]),
            "temperature_site":     _site(r[3]),
            # vitals.source is the ingestion/application source (for example
            # "maui_app"), not thermometer/device provenance. Keep it
            # separate from the future temperature source_type field defined
            # by the engineering spec.
            "source":               r[4] or "unknown",
            "heart_rate":           r[5],
            "oxygen_saturation":    r[6],
            "systolic":             r[7],
            "diastolic":            r[8],
            "blood_glucose":        r[9],
            "weight":               float(r[10]) if r[10] is not None else None,
        }

    rows_d = [_row_dict(r) for r in rows]
    latest = rows_d[-1]

    latest_context = {}
    if latest["heart_rate"] is not None:
        latest_context["heart_rate"] = latest["heart_rate"]
    if latest["oxygen_saturation"] is not None:
        latest_context["oxygen_saturation"] = latest["oxygen_saturation"]
    if latest["systolic"] is not None and latest["diastolic"] is not None:
        latest_context["blood_pressure"] = {
            "systolic": latest["systolic"],
            "diastolic": latest["diastolic"],
        }
    if latest["blood_glucose"] is not None:
        latest_context["blood_glucose"] = latest["blood_glucose"]
    if latest["weight"] is not None:
        latest_context["weight"] = latest["weight"]

    distinct_days = len({_temperature_local_datetime(r).date() for r in rows_d})
    span_days = (
        (rows_d[-1]["recorded_at"] - rows_d[0]["recorded_at"]).total_seconds() / 86400
        if len(rows_d) > 1 else 0.0
    )

    # Measurement-site consistency. Unknown-site rows remain valid history,
    # but do not participate in same-site baseline/trend claims.
    known_sites = [
        r["temperature_site"]
        for r in rows_d
        if r["temperature_site"] != "unknown"
    ]
    unique_known_sites = sorted(set(known_sites))
    modal_site = None
    site_consistency_pct = None
    if known_sites:
        modal_site = statistics.multimode(known_sites)[0]
        site_consistency_pct = round(
            100.0 * sum(1 for s in known_sites if s == modal_site) / len(known_sites),
            1,
        )

    known_site_pct = round(100.0 * len(known_sites) / len(rows_d), 1)

    # Range observations. These are logged-reading counts only.
    fever_rows = [r for r in rows_d if r["temperature_f"] >= FEVER_F]
    hypothermia_rows = [r for r in rows_d if r["temperature_f"] < HYPOTHERMIA_F]
    febrile_days = len({_temperature_local_datetime(r).date() for r in fever_rows})

    lowest = min(rows_d, key=lambda r: r["temperature_f"])

    # Fever episode grouping: a new episode begins when the gap from the
    # prior fever-range observation exceeds 24 hours. This is a Vitals
    # grouping rule, not a medical definition.
    episode_groups = []
    if fever_rows:
        ordered_fever = sorted(fever_rows, key=lambda r: r["recorded_at"])
        group = [ordered_fever[0]]
        for r in ordered_fever[1:]:
            gap_hours = (
                r["recorded_at"] - group[-1]["recorded_at"]
            ).total_seconds() / 3600.0
            if gap_hours <= EPISODE_GAP_HOURS:
                group.append(r)
            else:
                episode_groups.append(group)
                group = [r]
        episode_groups.append(group)

    episodes = []
    for idx, group in enumerate(episode_groups, start=1):
        peak = max(group, key=lambda r: r["temperature_f"])
        minimum = min(group, key=lambda r: r["temperature_f"])
        sites = sorted(set(r["temperature_site"] for r in group))
        span_hours = (
            (group[-1]["recorded_at"] - group[0]["recorded_at"]).total_seconds() / 3600.0
            if len(group) > 1 else 0.0
        )
        delta_latest = None
        hours_since_peak = None

        # Only compare latest vs peak when the measurement site matches;
        # fixed cross-site offsets are intentionally not used.
        if (
            idx == len(episode_groups)
            and latest["recorded_at"] >= peak["recorded_at"]
            and latest["temperature_site"] != "unknown"
            and latest["temperature_site"] == peak["temperature_site"]
        ):
            delta_latest = round(
                latest["temperature_f"] - peak["temperature_f"], 1
            )
            hours_since_peak = round(
                (latest["recorded_at"] - peak["recorded_at"]).total_seconds() / 3600.0,
                1,
            )

        episodes.append({
            "episode_id":                 idx,
            "first_fever_at":             group[0]["recorded_at"].isoformat(),
            "last_fever_at":              group[-1]["recorded_at"].isoformat(),
            "observed_span_hours":        round(span_hours, 1),
            "peak_f":                     round(peak["temperature_f"], 1),
            "peak_c":                     round(_to_c(peak["temperature_f"]), 1),
            "peak_at":                    peak["recorded_at"].isoformat(),
            "peak_site":                  peak["temperature_site"],
            "minimum_f":                  round(minimum["temperature_f"], 1),
            "minimum_c":                  round(_to_c(minimum["temperature_f"]), 1),
            "minimum_at":                 minimum["recorded_at"].isoformat(),
            "minimum_site":               minimum["temperature_site"],
            "n_fever_readings":           len(group),
            "febrile_days":               len({_temperature_local_datetime(r).date() for r in group}),
            "sites":                      sites,
            "mixed_sites":                len([s for s in sites if s != "unknown"]) > 1,
            "delta_latest_from_peak_f":   delta_latest,
            "hours_since_peak":           hours_since_peak,
        })

    latest_episode_group = episode_groups[-1] if episode_groups else None
    latest_episode = episodes[-1] if episodes else None

    # Same-site personal baseline. Eligible readings are non-fever,
    # non-hypothermia-range values from the latest reading's known site.
    # When the selected data contain a fever episode, baseline candidates
    # on/after the latest episode start are excluded so the acute episode
    # does not define the patient's "usual" temperature.
    baseline = None
    baseline_failure_reason = None
    latest_site = latest["temperature_site"]

    if latest_site == "unknown":
        baseline_failure_reason = "measurement_site_unknown"
    else:
        source_rows = [_row_dict(r) for r in baseline_rows] if baseline_rows else rows_d
        cutoff = (
            latest_episode_group[0]["recorded_at"]
            if latest_episode_group else latest["recorded_at"]
        )

        candidates = [
            r for r in source_rows
            if r["temperature_site"] == latest_site
            and HYPOTHERMIA_F <= r["temperature_f"] < FEVER_F
            and r["recorded_at"] < cutoff
        ]

        baseline_days = len({_temperature_local_datetime(r).date() for r in candidates})
        baseline_span_days = (
            (candidates[-1]["recorded_at"] - candidates[0]["recorded_at"]).total_seconds() / 86400.0
            if len(candidates) > 1 else 0.0
        )

        if len(candidates) >= 7 and baseline_days >= 7 and baseline_span_days >= 14:
            baseline_median_f = float(statistics.median(
                [r["temperature_f"] for r in candidates]
            ))
            baseline = {
                "site":            latest_site,
                "median_f":        round(baseline_median_f, 1),
                "median_c":        round(_to_c(baseline_median_f), 1),
                "n":               len(candidates),
                "distinct_days":   baseline_days,
                "span_days":       round(baseline_span_days, 1),
                "delta_current_f": round(latest["temperature_f"] - baseline_median_f, 1),
                "delta_current_c": round(
                    _to_c(latest["temperature_f"]) - _to_c(baseline_median_f), 1
                ),
            }
        else:
            baseline_failure_reason = "insufficient_same_site_baseline"

    # Acute OLS trajectory for the latest recorded fever episode.
    #
    # Important: this is NOT restricted to fever-range points. Once an
    # episode has started, same-site temperatures below the fever reference
    # can be the most informative evidence of a downward/recovery pattern.
    # We therefore include same-site readings from the first fever
    # observation through at most 24 hours after the last fever observation.
    # The 24-hour association window mirrors the episode-gap engineering rule
    # and is not a medical definition of episode duration.
    acute_trend = None
    acute_trend_failure_reason = None
    acute_trend_rows = []

    if latest_episode_group:
        episode_known_sites = {
            r["temperature_site"]
            for r in latest_episode_group
            if r["temperature_site"] != "unknown"
        }

        if len(episode_known_sites) == 0:
            acute_trend_failure_reason = "measurement_site_unknown"
        elif len(episode_known_sites) > 1:
            acute_trend_failure_reason = "mixed_measurement_sites"
        else:
            acute_site = next(iter(episode_known_sites))
            first_fever_at = latest_episode_group[0]["recorded_at"]
            last_fever_at = latest_episode_group[-1]["recorded_at"]
            association_end = last_fever_at + timedelta(hours=EPISODE_GAP_HOURS)

            acute_trend_rows = [
                r for r in rows_d
                if r["temperature_site"] == acute_site
                and first_fever_at <= r["recorded_at"] <= association_end
            ]

            acute_span_hours = (
                (acute_trend_rows[-1]["recorded_at"] - acute_trend_rows[0]["recorded_at"]).total_seconds() / 3600.0
                if len(acute_trend_rows) > 1 else 0.0
            )

            if len(acute_trend_rows) >= 3 and acute_span_hours >= 6:
                origin = acute_trend_rows[0]["recorded_at"]
                x_hours = np.array([
                    (r["recorded_at"] - origin).total_seconds() / 3600.0
                    for r in acute_trend_rows
                ])
                y_f = np.array([r["temperature_f"] for r in acute_trend_rows])

                if np.std(y_f) == 0:
                    slope = 0.0
                    r2 = None
                    p_value = None
                else:
                    slope, _, r_value, p_value_raw, _ = stats.linregress(x_hours, y_f)
                    slope = float(slope)
                    r2 = float(r_value ** 2)
                    p_value = float(p_value_raw)

                modeled_change = slope * acute_span_hours

                # 0.5°F total modeled change is a Vitals presentation gate
                # only, not a clinical threshold.
                if abs(modeled_change) < 0.5:
                    label = "stable"
                elif slope > 0:
                    label = "rising"
                else:
                    label = "falling"

                acute_trend = {
                    "site":                  acute_site,
                    "n":                     len(acute_trend_rows),
                    "span_hours":            round(acute_span_hours, 1),
                    "slope_f_per_hour":      round(slope, 3),
                    "slope_f_per_12_hours":  round(slope * 12.0, 2),
                    "modeled_change_f":      round(modeled_change, 1),
                    "r2":                    round(r2, 2) if r2 is not None else None,
                    "p_value":               round(p_value, 3) if p_value is not None else None,
                    "trend_label":           label,
                    "first_reading_at":      acute_trend_rows[0]["recorded_at"].isoformat(),
                    "last_reading_at":       acute_trend_rows[-1]["recorded_at"].isoformat(),
                    "includes_post_fever_readings": any(
                        r["temperature_f"] < FEVER_F for r in acute_trend_rows
                    ),
                }
            else:
                acute_trend_failure_reason = "insufficient_episode_density"
    else:
        acute_trend_failure_reason = "no_fever_range_readings"

    # Same-event paired-vital context for fever-range observations. No
    # causation is inferred and no temperature-specific threshold is
    # invented for the paired vital.
    fever_context = []
    paired_counts = {
        "heart_rate": 0,
        "oxygen_saturation": 0,
        "blood_pressure": 0,
    }

    for r in fever_rows:
        context = {}
        if r["heart_rate"] is not None:
            context["heart_rate"] = r["heart_rate"]
            paired_counts["heart_rate"] += 1
        if r["oxygen_saturation"] is not None:
            context["oxygen_saturation"] = r["oxygen_saturation"]
            paired_counts["oxygen_saturation"] += 1
        if r["systolic"] is not None and r["diastolic"] is not None:
            context["blood_pressure"] = {
                "systolic": r["systolic"],
                "diastolic": r["diastolic"],
            }
            paired_counts["blood_pressure"] += 1

        fever_context.append({
            "recorded_at":   r["recorded_at"].isoformat(),
            "temperature_f": round(r["temperature_f"], 1),
            "site":          r["temperature_site"],
            "context":       context,
        })

    cross_vital_context = {
        "fever_observations": fever_context,
        "paired_counts":      paired_counts,
    }

    unavailable = []

    if latest_site == "unknown":
        unavailable.append({
            "analysis": "personal_baseline",
            "reason_code": "measurement_site_unknown",
            "reason": "measurement site is unknown; same-site baseline comparison is unavailable",
        })
    elif baseline is None:
        unavailable.append({
            "analysis": "personal_baseline",
            "reason_code": baseline_failure_reason or "insufficient_same_site_baseline",
            "reason": "needs >=7 eligible same-site readings on >=7 distinct days spanning >=14 days",
        })

    if not fever_rows:
        unavailable.append({
            "analysis": "fever_episode",
            "reason_code": "no_fever_range_readings",
            "reason": "no logged readings met the configured fever-range reference",
        })

    if acute_trend is None:
        unavailable.append({
            "analysis": "acute_trend",
            "reason_code": acute_trend_failure_reason or "insufficient_episode_density",
            "reason": (
                "needs >=3 same-site temperature readings spanning >=6 hours "
                "from the start of the latest fever episode through its 24-hour association window"
            ),
        })

    # Broader "low temperature" analysis remains capability-gated until
    # Vitals defines a configurable product threshold. Do not invent a
    # universal cutoff. The recognized <95°F hypothermia-range safety
    # context remains implemented independently.
    unavailable.append({
        "analysis": "low_temperature",
        "reason_code": "low_temperature_threshold_not_configured",
        "reason": "a broader low-temperature reference has not been configured; hypothermia-range observations below 95°F are still evaluated separately",
    })

    if len(unique_known_sites) > 1:
        unavailable.append({
            "analysis": "site_sensitive_comparison",
            "reason_code": "mixed_measurement_sites",
            "reason": "mixed measurement sites limit direct comparison; same-method readings are more comparable",
        })

    if not any(paired_counts.values()):
        unavailable.append({
            "analysis": "cross_vital_context",
            "reason_code": "no_paired_vitals",
            "reason": "no fever-range readings had paired HR, SpO2, or blood-pressure data",
        })

    # P1 capabilities are intentionally not inferred from fields Vitals does
    # not collect yet.
    unavailable.extend([
        {
            "analysis": "symptom_association",
            "reason_code": "no_symptom_data",
            "reason": "structured temperature symptom tags are not collected yet",
        },
        {
            "analysis": "antipyretic_association",
            "reason_code": "no_medication_administration_events",
            "reason": "actual antipyretic dose-administration events are not collected yet",
        },
    ])

    if acute_trend is not None:
        support_state = "acute_trend"
    elif fever_rows:
        support_state = "episode"
    elif baseline is not None:
        support_state = "baseline"
    elif len(rows_d) >= 3 and distinct_days >= 3:
        support_state = "descriptive"
    else:
        support_state = "snapshot"

    result = {
        "latest": {
            "value_f":        round(latest["temperature_f"], 1),
            "value_c":        round(_to_c(latest["temperature_f"]), 1),
            "recorded_at":    latest["recorded_at"].isoformat(),
            "site":           latest_site,
            "source":         latest["source"],
            # Reserved for true measurement provenance (manual,
            # thermometer, HealthKit/HealthConnect, etc.). The current
            # vitals table does not collect this separately yet.
            "source_type":    None,
            "linked_context": latest_context,
        },
        "reading_count": len(rows_d),
        "target_profile": {
            "fever_threshold_f":          FEVER_F,
            "fever_threshold_c":          38.0,
            "low_temperature_threshold_f": None,
            "low_temperature_threshold_c": None,
            "hypothermia_threshold_f":    HYPOTHERMIA_F,
            "hypothermia_threshold_c": 35.0,
            "source":                  "reference_default",
            "patient_specific":        False,
        },
        "baseline": baseline,
        "range_events": {
            "fever_threshold_f":       FEVER_F,
            "fever_count":             len(fever_rows),
            "fever_logged_pct":        round(100.0 * len(fever_rows) / len(rows_d), 1),
            "febrile_days":               febrile_days,
            "low_temperature_threshold_f": None,
            "low_temperature_count":       None,
            "low_temperature_readings":    None,
            "hypothermia_threshold_f":    HYPOTHERMIA_F,
            "hypothermia_range_count":    len(hypothermia_rows),
            "hypothermia_readings": [
                {
                    "recorded_at": r["recorded_at"].isoformat(),
                    "value_f":     round(r["temperature_f"], 1),
                    "site":        r["temperature_site"],
                }
                for r in hypothermia_rows
            ],
            "lowest_f":                round(lowest["temperature_f"], 1),
            "lowest_c":                round(_to_c(lowest["temperature_f"]), 1),
            "lowest_at":               lowest["recorded_at"].isoformat(),
            "lowest_site":             lowest["temperature_site"],
        },
        "episodes":            episodes,
        "latest_episode":      latest_episode,
        "acute_trend":         acute_trend,
        "cross_vital_context": cross_vital_context,
        "time_of_day_baseline": None,
        "symptom_associations": None,
        "antipyretic_associations": None,
        "pediatric_flags": None,
        "data_support": {
            "n":                         len(rows_d),
            "distinct_days":             distinct_days,
            "span_days":                 round(span_days, 1),
            "known_site_pct":            known_site_pct,
            "modal_site":                modal_site,
            "site_consistency_pct":      site_consistency_pct,
            "mixed_measurement_sites":   len(unique_known_sites) > 1,
            "support_state":             support_state,
            "unavailable_analyses":      unavailable,
        },
        "generated_at": datetime.now(timezone.utc).isoformat(),
    }

    return result


# --------------------
# Weight / glucose descriptive analysis
# --------------------
def _run_descriptive_scalar_analysis(
    rows: list,
    *,
    vital_type: str,
    unit: str,
    allow_longitudinal_trend: bool,
) -> dict | None:
    """
    Conservative P0 engine for scalar vitals that do not yet have enough
    structured context for a clinically targeted interpretation.

    Weight supports a descriptive longitudinal model because repeated body
    weight measurements are comparable as the same physical quantity.
    Blood glucose deliberately does NOT expose a clinical target band or
    trend yet: fasting/post-meal/random context is not collected, so mixing
    those states into a target or trajectory would overstate what the data
    can support.
    """
    if not rows:
        return None

    points = [
        {
            "recorded_at": r[0],
            "local_offset_minutes": r[1],
            "value": float(r[2]),
        }
        for r in rows
        if r[2] is not None
    ]
    if not points:
        return None

    values = np.array([p["value"] for p in points], dtype=float)
    first = points[0]
    latest = points[-1]
    span_days = max(
        0.0,
        (latest["recorded_at"] - first["recorded_at"]).total_seconds() / 86400.0,
    )
    distinct_days = len({_hr_local_datetime(p).date() for p in points})

    summary = {
        "mean": round(float(np.mean(values)), 1),
        "median": round(float(np.median(values)), 1),
        "min": round(float(np.min(values)), 1),
        "max": round(float(np.max(values)), 1),
    }

    change = None
    # First-to-latest change is meaningful for longitudinal Weight tracking.
    # Do not expose it for glucose while fasting/meal context is unknown:
    # two values from different measurement states are not directly
    # comparable as one continuous trajectory.
    if len(points) >= 2 and allow_longitudinal_trend:
        absolute_change = float(latest["value"] - first["value"])
        pct_change = (
            absolute_change / float(first["value"]) * 100.0
            if float(first["value"]) != 0
            else None
        )
        change = {
            "first_value": round(float(first["value"]), 1),
            "first_at": first["recorded_at"].isoformat(),
            "latest_value": round(float(latest["value"]), 1),
            "latest_at": latest["recorded_at"].isoformat(),
            "absolute_change": round(absolute_change, 1),
            "pct_change": round(pct_change, 1) if pct_change is not None else None,
        }

    trend = None
    unavailable = []

    if allow_longitudinal_trend:
        if len(points) >= 3 and distinct_days >= 3 and span_days >= 7.0:
            origin = first["recorded_at"]
            t = np.array([
                (p["recorded_at"] - origin).total_seconds() / 86400.0
                for p in points
            ], dtype=float)
            slope, _, r_val, p_val, _ = stats.linregress(t, values)
            trend = {
                "n": len(points),
                "span_days": round(span_days, 1),
                "slope_per_day": round(float(slope), 3),
                "slope_per_week": round(float(slope) * 7.0, 2),
                "r2": round(float(r_val ** 2), 2),
                "p_value": round(float(p_val), 3),
            }
        else:
            unavailable.append({
                "analysis": "longitudinal_trend",
                "reason_code": "insufficient_longitudinal_support",
                "reason": "needs >=3 readings on >=3 distinct days spanning >=7 days",
            })
    else:
        unavailable.append({
            "analysis": "longitudinal_trend",
            "reason_code": "measurement_context_not_collected",
            "reason": (
                "fasting/post-meal/random measurement context is not collected, "
                "so Vitals does not model a single glucose trajectory across mixed contexts"
            ),
        })
        unavailable.append({
            "analysis": "target_range",
            "reason_code": "measurement_context_not_collected",
            "reason": (
                "glucose target interpretation requires measurement context and may also "
                "depend on an individualized care plan"
            ),
        })

    support_state = (
        "trend"
        if trend is not None
        else "descriptive"
        if len(points) >= 2
        else "snapshot"
    )

    return {
        "vital_type": vital_type,
        "unit": unit,
        "latest": {
            "value": round(float(latest["value"]), 1),
            "recorded_at": latest["recorded_at"].isoformat(),
        },
        "reading_count": len(points),
        "summary": summary,
        "change_from_first": change,
        "trend": trend,
        "data_support": {
            "n": len(points),
            "distinct_days": distinct_days,
            "span_days": round(span_days, 1),
            "support_state": support_state,
            "unavailable_analyses": unavailable,
        },
    }


def run_weight_analysis(
    rows: list,
    patient_demographics: dict | None = None,
) -> dict | None:
    """
    Dedicated Weight analysis engine.

    Weight is different from the other spot vitals in two important ways:
    repeated measurements are directly comparable over time, but same-day
    repeats and ordinary short-term fluctuation can easily distort a naive
    regression. The engine therefore keeps the raw latest/summary values for
    transparency while using one median value per local calendar day for
    baseline change, recent-period comparison, variability, and trend.

    The trend uses a Theil-Sen slope over daily medians. That estimator is
    deliberately more resistant to an isolated bad scale reading or entry
    typo than ordinary least squares. OLS R²/p are retained only as secondary
    fit diagnostics for backwards-compatible report rendering; the trend
    direction itself is determined from the Theil-Sen 95% slope interval.

    Adult current BMI is calculated only when the patient's date of birth
    establishes age >=20 on the latest weight date and a current profile
    height is available. BMI is presented as screening context, never as a
    diagnosis or body-composition estimate.

    Historical BMI is calculated separately using one median Weight per local
    calendar day. Each Weight day is paired only with the newest ACTIVE height
    observation whose effective_date is on or before that Weight date. A newer
    height is never backfilled into older Weight dates, and profile-backfilled
    heights therefore do not fabricate BMI values for pre-migration Weight.
    """
    if not rows:
        return None

    points = [
        {
            "recorded_at": r[0],
            "local_offset_minutes": r[1],
            "value": float(r[2]),
        }
        for r in rows
        if r[2] is not None
    ]
    if not points:
        return None

    raw_values = np.array([p["value"] for p in points], dtype=float)
    first = points[0]
    latest = points[-1]

    # ------------------------------------------------------------------
    # Adult CURRENT BMI (P0 §7.1 / §7.7)
    # ------------------------------------------------------------------
    # patients.height_inches remains the current profile-height cache, while
    # patient_height_history preserves effective-dated observations.
    # Current BMI uses the current profile value plus its dated metadata;
    # historical BMI is calculated separately below from the dated timeline.
    anthropometrics = {
        "bmi_available": False,
        "reason_unavailable": None,
        "height_inches": None,
        "height_cm": None,
        "height_source": None,
        "height_measured_at": None,
        "age_years": None,
        "bmi": None,
        "adult_category": None,
    }

    demographics = patient_demographics or {}
    dob = demographics.get("dob")
    height_inches = demographics.get("height_inches")
    latest_local_date = _hr_local_datetime(latest).date()

    if height_inches is not None:
        height_inches = int(height_inches)
        anthropometrics["height_inches"] = height_inches
        anthropometrics["height_cm"] = round(height_inches * 2.54, 1)
        anthropometrics["height_source"] = (
            demographics.get("height_source") or "current_profile"
        )
        height_effective_date = demographics.get("height_effective_date")
        if height_effective_date is not None:
            anthropometrics["height_measured_at"] = (
                height_effective_date.isoformat()
                if hasattr(height_effective_date, "isoformat")
                else str(height_effective_date)
            )

    if dob is None:
        anthropometrics["reason_unavailable"] = "missing_date_of_birth"
    else:
        if isinstance(dob, str):
            dob = date.fromisoformat(dob)

        age_years = (
            latest_local_date.year
            - dob.year
            - (
                (latest_local_date.month, latest_local_date.day)
                < (dob.month, dob.day)
            )
        )
        anthropometrics["age_years"] = age_years

        if age_years < 20:
            # Adult categories must never be applied to pediatric patients.
            # Pediatric BMI-for-age is a separate gated strategy in the spec.
            anthropometrics["reason_unavailable"] = "pediatric_strategy_required"
        elif height_inches is None:
            anthropometrics["reason_unavailable"] = "missing_height"
        elif height_inches <= 0:
            anthropometrics["reason_unavailable"] = "invalid_height"
        else:
            # Normalize to metric internally per the Weight engineering spec.
            # The familiar 703*lb/in² form is mathematically equivalent, but
            # keeping one internal unit convention avoids future mixed-unit
            # drift when metric entry is added.
            weight_kg = float(latest["value"]) / 2.2046226218
            height_m = (float(height_inches) * 2.54) / 100.0
            bmi = weight_kg / (height_m ** 2)

            if bmi < 18.5:
                category = "underweight"
            elif bmi < 25.0:
                category = "healthy_weight"
            elif bmi < 30.0:
                category = "overweight"
            elif bmi < 35.0:
                category = "obesity_class_1"
            elif bmi < 40.0:
                category = "obesity_class_2"
            else:
                category = "obesity_class_3"

            anthropometrics.update({
                "bmi_available": True,
                "reason_unavailable": None,
                "bmi": round(float(bmi), 1),
                "adult_category": category,
            })

    raw_span_days = max(
        0.0,
        (latest["recorded_at"] - first["recorded_at"]).total_seconds() / 86400.0,
    )

    # Collapse repeated measurements on the same LOCAL calendar day to one
    # daily median. This prevents a day with several checks from carrying more
    # statistical weight than a day with one check.
    by_day: dict = {}
    for p in points:
        local_day = _hr_local_datetime(p).date()
        by_day.setdefault(local_day, []).append(float(p["value"]))

    daily_points = [
        {
            "date": day,
            "value": float(np.median(values)),
            "reading_count": len(values),
        }
        for day, values in sorted(by_day.items())
    ]
    daily_values = np.array([p["value"] for p in daily_points], dtype=float)
    distinct_days = len(daily_points)
    daily_span_days = (
        float((daily_points[-1]["date"] - daily_points[0]["date"]).days)
        if distinct_days >= 2
        else 0.0
    )

    # ------------------------------------------------------------------
    # HISTORICAL ADULT BMI TRAJECTORY
    # ------------------------------------------------------------------
    # Use one daily-median Weight point, matching the rest of Weight's
    # longitudinal analysis. For each Weight date, select ONLY the newest
    # active height observation effective on/before that date. Never apply a
    # later height backward in time.
    height_history = demographics.get("height_history") or []
    normalized_height_history = []
    for h in height_history:
        effective = h.get("effective_date")
        if effective is None:
            continue
        if isinstance(effective, str):
            effective = date.fromisoformat(effective)
        normalized_height_history.append({
            "height_inches": int(h["height_inches"]),
            "effective_date": effective,
            "source": h.get("source"),
            "entry_type": h.get("entry_type"),
            "created_at": h.get("created_at"),
        })

    # SQL already orders same-date observations by created_at; Python's sort
    # is stable, so sorting by effective date keeps the newest same-day active
    # record last and therefore selected below.
    normalized_height_history.sort(key=lambda h: h["effective_date"])

    historical_points = []
    skipped_missing_height = 0
    skipped_pediatric = 0
    skipped_missing_dob = 0
    adult_candidate_days = 0

    for daily in daily_points:
        weight_date = daily["date"]

        if dob is None:
            skipped_missing_dob += 1
            continue

        age_years = (
            weight_date.year
            - dob.year
            - (
                (weight_date.month, weight_date.day)
                < (dob.month, dob.day)
            )
        )
        if age_years < 20:
            skipped_pediatric += 1
            continue

        adult_candidate_days += 1

        effective_height = None
        for h in normalized_height_history:
            if h["effective_date"] <= weight_date:
                effective_height = h
            else:
                break

        if effective_height is None:
            skipped_missing_height += 1
            continue

        hist_height_inches = int(effective_height["height_inches"])
        if hist_height_inches <= 0:
            skipped_missing_height += 1
            continue

        weight_kg = float(daily["value"]) / 2.2046226218
        height_m = (float(hist_height_inches) * 2.54) / 100.0
        hist_bmi = weight_kg / (height_m ** 2)

        if hist_bmi < 18.5:
            hist_category = "underweight"
        elif hist_bmi < 25.0:
            hist_category = "healthy_weight"
        elif hist_bmi < 30.0:
            hist_category = "overweight"
        elif hist_bmi < 35.0:
            hist_category = "obesity_class_1"
        elif hist_bmi < 40.0:
            hist_category = "obesity_class_2"
        else:
            hist_category = "obesity_class_3"

        historical_points.append({
            "local_date": weight_date.isoformat(),
            "weight_lb": round(float(daily["value"]), 1),
            "source_reading_count": int(daily["reading_count"]),
            "height_inches": hist_height_inches,
            "height_effective_date": effective_height["effective_date"].isoformat(),
            "height_source": effective_height.get("source"),
            "height_entry_type": effective_height.get("entry_type"),
            "age_years": age_years,
            "bmi": round(float(hist_bmi), 1),
            "adult_category": hist_category,
        })

    historical_reason_code = None
    historical_reason = None
    if not historical_points:
        if dob is None:
            historical_reason_code = "missing_date_of_birth"
            historical_reason = (
                "A date of birth is required before historical adult BMI can be age-gated."
            )
        elif adult_candidate_days == 0:
            historical_reason_code = "pediatric_strategy_required"
            historical_reason = (
                "No Weight day in this period occurred at age 20 or older; "
                "pediatric BMI-for-age requires a separate strategy."
            )
        else:
            historical_reason_code = "no_historically_valid_height"
            historical_reason = (
                "No active height observation was effective on or before the eligible "
                "Weight date(s). Newer heights are not applied backward in time."
            )

    historical_bmi = {
        "available": len(historical_points) > 0,
        "reason_unavailable": historical_reason_code,
        "reason": historical_reason,
        "point_count": len(historical_points),
        "candidate_weight_days": distinct_days,
        "adult_candidate_days": adult_candidate_days,
        "skipped_missing_historical_height": skipped_missing_height,
        "skipped_pediatric": skipped_pediatric,
        "skipped_missing_date_of_birth": skipped_missing_dob,
        "points": historical_points,
        "first_bmi": None,
        "latest_bmi": None,
        "absolute_change": None,
        "span_days": 0.0,
    }
    if historical_points:
        historical_bmi["first_bmi"] = historical_points[0]["bmi"]
        historical_bmi["latest_bmi"] = historical_points[-1]["bmi"]
        historical_bmi["span_days"] = float(
            (
                date.fromisoformat(historical_points[-1]["local_date"])
                - date.fromisoformat(historical_points[0]["local_date"])
            ).days
        )
        if len(historical_points) >= 2:
            historical_bmi["absolute_change"] = round(
                float(historical_points[-1]["bmi"] - historical_points[0]["bmi"]),
                1,
            )

    summary = {
        "mean": round(float(np.mean(raw_values)), 1),
        "median": round(float(np.median(raw_values)), 1),
        "min": round(float(np.min(raw_values)), 1),
        "max": round(float(np.max(raw_values)), 1),
    }
    daily_summary = {
        "mean": round(float(np.mean(daily_values)), 1),
        "median": round(float(np.median(daily_values)), 1),
        "min": round(float(np.min(daily_values)), 1),
        "max": round(float(np.max(daily_values)), 1),
    }

    # Kept for compatibility with the first Weight/PDF pass. New UI should
    # prefer baseline_change below because a single first reading is a weak
    # reference point.
    change_from_first = None
    if len(points) >= 2:
        absolute_change = float(latest["value"] - first["value"])
        pct_change = (
            absolute_change / float(first["value"]) * 100.0
            if float(first["value"]) != 0
            else None
        )
        change_from_first = {
            "first_value": round(float(first["value"]), 1),
            "first_at": first["recorded_at"].isoformat(),
            "latest_value": round(float(latest["value"]), 1),
            "latest_at": latest["recorded_at"].isoformat(),
            "absolute_change": round(absolute_change, 1),
            "pct_change": round(pct_change, 1) if pct_change is not None else None,
        }

    # Baseline = median of the first up-to-three DISTINCT days, never
    # including the latest day itself. This is much less sensitive to an
    # unusually high/low first measurement.
    baseline_change = None
    if distinct_days >= 2:
        baseline_days_used = min(3, distinct_days - 1)
        baseline_values = np.array(
            [p["value"] for p in daily_points[:baseline_days_used]], dtype=float
        )
        baseline_value = float(np.median(baseline_values))
        latest_daily_value = float(daily_points[-1]["value"])
        absolute_change = latest_daily_value - baseline_value
        pct_change = (
            absolute_change / baseline_value * 100.0
            if baseline_value != 0
            else None
        )
        baseline_change = {
            "baseline_value": round(baseline_value, 1),
            "baseline_days_used": baseline_days_used,
            "baseline_start_date": daily_points[0]["date"].isoformat(),
            "baseline_end_date": daily_points[baseline_days_used - 1]["date"].isoformat(),
            "latest_daily_value": round(latest_daily_value, 1),
            "latest_date": daily_points[-1]["date"].isoformat(),
            "absolute_change": round(float(absolute_change), 1),
            "pct_change": round(float(pct_change), 1) if pct_change is not None else None,
        }

    # Recent seven-day median versus the seven days immediately before it.
    # Two distinct measurement days are required in each block so a single
    # isolated reading is not presented as a period-to-period shift.
    recent_change = None
    if distinct_days >= 4:
        latest_day = daily_points[-1]["date"]
        recent_start = latest_day - timedelta(days=6)
        prior_start = latest_day - timedelta(days=13)
        prior_end = latest_day - timedelta(days=7)

        recent_vals = [
            p["value"] for p in daily_points
            if recent_start <= p["date"] <= latest_day
        ]
        prior_vals = [
            p["value"] for p in daily_points
            if prior_start <= p["date"] <= prior_end
        ]

        if len(recent_vals) >= 2 and len(prior_vals) >= 2:
            recent_median = float(np.median(np.array(recent_vals, dtype=float)))
            prior_median = float(np.median(np.array(prior_vals, dtype=float)))
            absolute_change = recent_median - prior_median
            pct_change = (
                absolute_change / prior_median * 100.0
                if prior_median != 0
                else None
            )
            recent_change = {
                "recent_median": round(recent_median, 1),
                "prior_median": round(prior_median, 1),
                "recent_days_with_readings": len(recent_vals),
                "prior_days_with_readings": len(prior_vals),
                "absolute_change": round(float(absolute_change), 1),
                "pct_change": round(float(pct_change), 1) if pct_change is not None else None,
            }

    variation = None
    if distinct_days >= 3:
        q1, q3 = np.percentile(daily_values, [25, 75])
        median_daily = float(np.median(daily_values))
        mad = float(np.median(np.abs(daily_values - median_daily)))
        variation = {
            "sd": round(float(np.std(daily_values, ddof=1)), 1),
            "iqr": round(float(q3 - q1), 1),
            "q1": round(float(q1), 1),
            "q3": round(float(q3), 1),
            "mad": round(mad, 1),
        }

    trend = None
    unavailable = []

    if not historical_bmi["available"]:
        unavailable.append({
            "analysis": "historical_bmi_trajectory",
            "reason_code": historical_bmi["reason_unavailable"],
            "reason": historical_bmi["reason"],
        })

    # A robust trend needs enough independent DAYS and enough calendar span
    # to represent more than a few adjacent measurements.
    if distinct_days >= 5 and daily_span_days >= 14.0:
        origin = daily_points[0]["date"]
        t = np.array(
            [(p["date"] - origin).days for p in daily_points],
            dtype=float,
        )

        ts_slope, _, ts_low, ts_high = stats.theilslopes(
            daily_values, t, 0.95
        )
        ols = stats.linregress(t, daily_values)

        low_week = float(ts_low) * 7.0
        high_week = float(ts_high) * 7.0
        if low_week > 0:
            direction = "increasing"
        elif high_week < 0:
            direction = "decreasing"
        else:
            direction = "no_clear_trend"

        trend = {
            "method": "theil_sen_daily_medians",
            "n": distinct_days,
            "span_days": round(daily_span_days, 1),
            "slope_per_day": round(float(ts_slope), 3),
            "slope_per_week": round(float(ts_slope) * 7.0, 2),
            "ci95_low_per_week": round(low_week, 2),
            "ci95_high_per_week": round(high_week, 2),
            "direction": direction,
            # Secondary OLS diagnostics retained for the existing PDF helper.
            "r2": round(float(ols.rvalue ** 2), 2),
            "p_value": round(float(ols.pvalue), 3),
        }
    else:
        unavailable.append({
            "analysis": "robust_longitudinal_trend",
            "reason_code": "insufficient_longitudinal_support",
            "reason": "needs >=5 distinct measurement days spanning >=14 days",
        })

    if recent_change is None:
        unavailable.append({
            "analysis": "recent_7_day_comparison",
            "reason_code": "insufficient_period_coverage",
            "reason": (
                "needs >=2 measurement days in the most recent 7 days and "
                ">=2 measurement days in the 7 days before that"
            ),
        })

    support_state = (
        "trend"
        if trend is not None
        else "descriptive"
        if distinct_days >= 2
        else "snapshot"
    )

    return {
        "analysis_version": 6,
        "vital_type": "weight",
        "unit": "lb",
        "latest": {
            "value": round(float(latest["value"]), 1),
            "recorded_at": latest["recorded_at"].isoformat(),
        },
        "reading_count": len(points),
        "anthropometrics": anthropometrics,
        "historical_bmi": historical_bmi,
        "summary": summary,
        "daily_summary": daily_summary,
        "change_from_first": change_from_first,
        "baseline_change": baseline_change,
        "recent_change": recent_change,
        "variation": variation,
        "trend": trend,
        "data_support": {
            "n": len(points),
            "distinct_days": distinct_days,
            "span_days": round(raw_span_days, 1),
            "daily_span_days": round(daily_span_days, 1),
            "support_state": support_state,
            "unavailable_analyses": unavailable,
        },
        "limitations": [
            (
                "BMI is a screening measure and should be interpreted with other health information."
                if anthropometrics["bmi_available"]
                else {
                    "missing_height": "Add a current height in Patient Profile to calculate adult BMI.",
                    "missing_date_of_birth": "A date of birth is required before adult BMI can be age-gated.",
                    "pediatric_strategy_required": "Adult BMI screening categories are not shown for patients under age 20; pediatric BMI-for-age requires a separate age- and reference-sex-specific strategy.",
                    "invalid_height": "The stored height is not usable for BMI calculation.",
                }.get(
                    anthropometrics["reason_unavailable"],
                    "Adult BMI is unavailable for the current patient profile.",
                )
            ),
            (
                "Historical BMI uses only active height observations effective on or before each Weight date; newer heights are never backfilled into older dates."
                if historical_bmi["available"]
                else historical_bmi["reason"]
            ),
            "Vitals does not judge whether weight gain or loss is desirable without an individualized goal.",
            "Disease-specific rapid-weight-change alerts require diagnosis or clinician-configured thresholds and are not applied automatically.",
        ],
    }


def run_glucose_analysis(
    rows: list,
    medication_changes: list | None = None,
) -> dict | None:
    """
    Dedicated patient-recorded Glucose analysis engine (manual/BGM first).

    Contract principles:
    - Never model one clinical trajectory across mixed fasting/pre-meal/
      post-meal/bedtime/random observations.
    - Preserve legacy readings with no glucose_context row as "unknown"
      rather than dropping them.
    - Manual/BGM spot readings can support contextual summaries, trends,
      low-event review, meal excursions, descriptive variability, time-of-day
      patterns, and medication-timeline comparisons.
    - "Logged Readings in Target" is intentionally capability-gated until a
      patient glucose target profile exists. It must never be mislabeled as
      CGM Time in Range.
    - GMI remains present in the contract but unavailable until qualified,
      sufficiently dense CGM data AND coverage metadata exist. Sparse manual
      readings are never used to calculate GMI.

    Expected row shape (see VITAL_ANALYSIS_REGISTRY['glucose']):
      0 recorded_at
      1 local_offset_minutes
      2 blood_glucose (normalized mg/dL)
      3 measurement_context
      4 meal_type
      5 minutes_after_meal
      6 meal_event_id
      7 source_type
      8 original_value
      9 original_unit
     10 source_device
    """
    if not rows:
        return None

    def _row_dict(r):
        return {
            "recorded_at": r[0],
            "local_offset_minutes": r[1],
            "value": float(r[2]),
            "measurement_context": r[3] or "unknown",
            "meal_type": r[4],
            "minutes_after_meal": r[5],
            "meal_event_id": str(r[6]) if r[6] is not None else None,
            "source_type": r[7] or "unknown",
            "original_value": float(r[8]) if r[8] is not None else None,
            "original_unit": r[9],
            "source_device": r[10],
        }

    points = [_row_dict(r) for r in rows if r[2] is not None]
    if not points:
        return None

    known_contexts = ("fasting", "pre_meal", "post_meal", "bedtime", "random")
    all_context_names = (
        "fasting", "pre_meal", "post_meal", "bedtime",
        "random", "other", "unknown",
    )

    values = np.array([p["value"] for p in points], dtype=float)
    first = points[0]
    latest = points[-1]
    span_days = max(
        0.0,
        (latest["recorded_at"] - first["recorded_at"]).total_seconds() / 86400.0,
    )
    distinct_days = len({_hr_local_datetime(p).date() for p in points})

    summary = {
        "mean": round(float(np.mean(values)), 1),
        "median": round(float(np.median(values)), 1),
        "min": round(float(np.min(values)), 1),
        "max": round(float(np.max(values)), 1),
    }

    def _coverage_for(group):
        if not group:
            return 0, 0.0
        days = len({_hr_local_datetime(p).date() for p in group})
        group_span = max(
            0.0,
            (group[-1]["recorded_at"] - group[0]["recorded_at"]).total_seconds() / 86400.0,
        )
        return days, group_span

    def _ols(group):
        if len(group) < 2:
            return None
        origin = group[0]["recorded_at"]
        x = np.array([
            (p["recorded_at"] - origin).total_seconds() / 86400.0
            for p in group
        ], dtype=float)
        y = np.array([p["value"] for p in group], dtype=float)
        if float(np.max(x) - np.min(x)) <= 0:
            return None
        if float(np.std(y)) == 0:
            return {
                "slope_mg_dl_per_day": 0.0,
                "modeled_change_mg_dl": 0.0,
                "r2": None,
                "p_value": None,
            }
        slope, _, r_val, p_val, _ = stats.linregress(x, y)
        return {
            "slope_mg_dl_per_day": round(float(slope), 3),
            "modeled_change_mg_dl": round(float(slope) * float(np.max(x) - np.min(x)), 1),
            "r2": round(float(r_val ** 2), 2),
            "p_value": round(float(p_val), 3),
        }

    # --------------------------------------------------------------
    # Snapshot + source/context accounting
    # --------------------------------------------------------------
    source_counts = {}
    for p in points:
        source_counts[p["source_type"]] = source_counts.get(p["source_type"], 0) + 1

    context_counts = {name: 0 for name in all_context_names}
    for p in points:
        context = p["measurement_context"]
        if context not in context_counts:
            context = "unknown"
        context_counts[context] += 1

    context_recorded_count = sum(
        1 for p in points
        if p["measurement_context"] not in ("unknown", None)
    )
    context_completeness_pct = round(
        100.0 * context_recorded_count / len(points), 1
    )

    latest_block = {
        "value": round(float(latest["value"]), 1),
        "recorded_at": latest["recorded_at"].isoformat(),
        "measurement_context": latest["measurement_context"],
        "meal_type": latest["meal_type"],
        "minutes_after_meal": latest["minutes_after_meal"],
        "source_type": latest["source_type"],
        "original_value": latest["original_value"],
        "original_unit": latest["original_unit"],
        "source_device": latest["source_device"],
    }

    # --------------------------------------------------------------
    # Context-specific summaries (>=3 readings in that context)
    # --------------------------------------------------------------
    context_summaries = {}
    context_groups = {}
    for context in all_context_names:
        group = [p for p in points if p["measurement_context"] == context]
        context_groups[context] = group
        if not group:
            continue

        group_values = np.array([p["value"] for p in group], dtype=float)
        group_days, group_span = _coverage_for(group)
        available = context in known_contexts and len(group) >= 3

        block = {
            "is_available": available,
            "reason_code": None,
            "sample_count": len(group),
            "distinct_days": group_days,
            "span_days": round(group_span, 1),
            "mean": None,
            "median": None,
            "min": None,
            "max": None,
        }
        if available:
            block.update({
                "mean": round(float(np.mean(group_values)), 1),
                "median": round(float(np.median(group_values)), 1),
                "min": round(float(np.min(group_values)), 1),
                "max": round(float(np.max(group_values)), 1),
            })
        elif context not in known_contexts:
            block["reason_code"] = "noncomparable_context"
        else:
            block["reason_code"] = "insufficient_count"

        context_summaries[context] = block

    # --------------------------------------------------------------
    # Context-specific trend + LOESS
    # Directional trend: >=5 readings across >=7 days.
    # Regression significance is shown only at >=10 readings / >=14 days.
    # --------------------------------------------------------------
    context_trends = {}
    any_context_trend = False
    for context in known_contexts:
        group = context_groups.get(context) or []
        group_days, group_span = _coverage_for(group)
        available = len(group) >= 5 and group_days >= 2 and group_span >= 7.0

        block = {
            "is_available": available,
            "reason_code": None if available else "insufficient_longitudinal_support",
            "sample_count": len(group),
            "distinct_days": group_days,
            "span_days": round(group_span, 1),
            "direction": None,
            "slope_mg_dl_per_day": None,
            "modeled_change_mg_dl": None,
            "r2": None,
            "p_value": None,
            "significance_available": False,
            "loess": None,
        }

        if available:
            fit = _ols(group)
            if fit is not None:
                slope = fit["slope_mg_dl_per_day"]
                block.update(fit)
                block["direction"] = (
                    "rising" if slope > 0
                    else "falling" if slope < 0
                    else "stable"
                )

                # Significance is deliberately gated more strictly than the
                # descriptive direction/slope.
                significance_available = len(group) >= 10 and group_span >= 14.0
                block["significance_available"] = significance_available
                if not significance_available:
                    block["p_value"] = None

                origin = group[0]["recorded_at"]
                x = np.array([
                    (p["recorded_at"] - origin).total_seconds() / 86400.0
                    for p in group
                ], dtype=float)
                y = np.array([p["value"] for p in group], dtype=float)
                smoothed = loess_smooth(x, y, frac=0.6)
                block["loess"] = [
                    {
                        "recorded_at": p["recorded_at"].isoformat(),
                        "smoothed_mg_dl": round(float(v), 1),
                    }
                    for p, v in zip(group, smoothed)
                ]
                any_context_trend = True
            else:
                block["is_available"] = False
                block["reason_code"] = "insufficient_time_geometry"

        context_trends[context] = block

    # --------------------------------------------------------------
    # Low-glucose event analysis (numeric thresholds do not require a
    # patient target profile). Level 3 cannot be inferred from a number.
    # --------------------------------------------------------------
    level_1 = []
    level_2 = []
    for p in points:
        event = {
            "recorded_at": p["recorded_at"].isoformat(),
            "value_mg_dl": round(float(p["value"]), 1),
            "measurement_context": p["measurement_context"],
            "meal_type": p["meal_type"],
        }
        if p["value"] < 54:
            level_2.append(event)
        elif p["value"] < 70:
            level_1.append(event)

    low_events = {
        "is_available": True,
        "level_1_count": len(level_1),
        "level_2_count": len(level_2),
        "total_low_count": len(level_1) + len(level_2),
        "level_1_events": level_1,
        "level_2_events": level_2,
        "pattern_language_available": (len(level_1) + len(level_2)) >= 2,
    }

    # --------------------------------------------------------------
    # Logged Readings in Target — contract is present now, but no patient
    # glucose target profile exists yet. Do not silently apply a universal
    # target and do not call sparse manual readings "Time in Range".
    # --------------------------------------------------------------
    logged_readings_in_target = {
        "is_available": False,
        "reason_code": "patient_target_profile_not_configured",
        "eligible_count": 0,
        "in_target_count": 0,
        "percentage": None,
        "label": "Logged Readings in Target",
    }

    # --------------------------------------------------------------
    # Meal-response / glucose excursion.
    # Prefer explicit meal_event_id. Conservative fallback pairing is allowed
    # only when one pre-meal and one post-meal reading exist for the same
    # local date + meal and post-meal timing is recorded.
    # --------------------------------------------------------------
    meal_pairs = []
    used_pair_keys = set()

    explicit_events = {}
    for p in points:
        if p["meal_event_id"]:
            explicit_events.setdefault(p["meal_event_id"], []).append(p)

    for event_id, event_rows in explicit_events.items():
        pres = [p for p in event_rows if p["measurement_context"] == "pre_meal"]
        posts = [p for p in event_rows if p["measurement_context"] == "post_meal"]
        if len(pres) == 1 and len(posts) == 1 and posts[0]["recorded_at"] > pres[0]["recorded_at"]:
            pre = pres[0]
            post = posts[0]
            pair_key = ("event", event_id)
            used_pair_keys.add(pair_key)
            meal_pairs.append({
                "pairing_method": "meal_event_id",
                "meal_event_id": event_id,
                "local_date": _hr_local_datetime(post).date().isoformat(),
                "meal_type": post["meal_type"] or pre["meal_type"],
                "minutes_after_meal": post["minutes_after_meal"],
                "pre_value_mg_dl": round(float(pre["value"]), 1),
                "post_value_mg_dl": round(float(post["value"]), 1),
                "excursion_mg_dl": round(float(post["value"] - pre["value"]), 1),
                "pre_recorded_at": pre["recorded_at"].isoformat(),
                "post_recorded_at": post["recorded_at"].isoformat(),
            })

    fallback_groups = {}
    for p in points:
        if p["meal_event_id"] or p["meal_type"] is None:
            continue
        if p["measurement_context"] not in ("pre_meal", "post_meal"):
            continue
        key = (_hr_local_datetime(p).date(), p["meal_type"])
        fallback_groups.setdefault(key, []).append(p)

    for (local_day, meal_type), group in fallback_groups.items():
        pres = [p for p in group if p["measurement_context"] == "pre_meal"]
        posts = [p for p in group if p["measurement_context"] == "post_meal"]
        if (
            len(pres) == 1
            and len(posts) == 1
            and posts[0]["recorded_at"] > pres[0]["recorded_at"]
            and posts[0]["minutes_after_meal"] is not None
        ):
            pre = pres[0]
            post = posts[0]
            meal_pairs.append({
                "pairing_method": "unambiguous_same_day_meal",
                "meal_event_id": None,
                "local_date": local_day.isoformat(),
                "meal_type": meal_type,
                "minutes_after_meal": post["minutes_after_meal"],
                "pre_value_mg_dl": round(float(pre["value"]), 1),
                "post_value_mg_dl": round(float(post["value"]), 1),
                "excursion_mg_dl": round(float(post["value"] - pre["value"]), 1),
                "pre_recorded_at": pre["recorded_at"].isoformat(),
                "post_recorded_at": post["recorded_at"].isoformat(),
            })

    # Do not average different post-meal intervals together. Exact recorded
    # minutes are the grouping key until a clinically reviewed interval-bucket
    # scheme is explicitly adopted.
    excursion_groups = {}
    for pair in meal_pairs:
        key = (pair["meal_type"], pair["minutes_after_meal"])
        excursion_groups.setdefault(key, []).append(pair)

    meal_excursion_summaries = []
    for (meal_type, minutes_after_meal), pairs in sorted(
        excursion_groups.items(),
        key=lambda item: (
            str(item[0][0] or ""),
            item[0][1] if item[0][1] is not None else -1,
        ),
    ):
        vals = [p["excursion_mg_dl"] for p in pairs]
        meal_excursion_summaries.append({
            "meal_type": meal_type,
            "minutes_after_meal": minutes_after_meal,
            "pair_count": len(pairs),
            "is_available": len(pairs) >= 3,
            "reason_code": None if len(pairs) >= 3 else "insufficient_pairs",
            "mean_excursion_mg_dl": round(float(np.mean(vals)), 1) if len(pairs) >= 3 else None,
            "median_excursion_mg_dl": round(float(np.median(vals)), 1) if len(pairs) >= 3 else None,
            "min_excursion_mg_dl": round(float(np.min(vals)), 1) if len(pairs) >= 3 else None,
            "max_excursion_mg_dl": round(float(np.max(vals)), 1) if len(pairs) >= 3 else None,
        })

    meal_excursions = {
        "is_available": len(meal_pairs) > 0,
        "reason_code": None if meal_pairs else "insufficient_pairs",
        "pair_count": len(meal_pairs),
        "pairs": meal_pairs,
        "summaries": meal_excursion_summaries,
    }

    # --------------------------------------------------------------
    # Descriptive variability by comparable context.
    # Basic spread: >=5 readings. BGM CV: >=10 readings; returned as
    # descriptive data only and never compared with CGM CV targets.
    # --------------------------------------------------------------
    variability = {}
    any_variability = False
    for context in known_contexts:
        group = context_groups.get(context) or []
        vals = [p["value"] for p in group]
        block = {
            "is_available": len(vals) >= 5,
            "reason_code": None if len(vals) >= 5 else "insufficient_count",
            "sample_count": len(vals),
            "sd": None,
            "iqr": None,
            "min": None,
            "max": None,
            "cv_pct": None,
            "cv_available": False,
        }
        if len(vals) >= 5:
            q1 = float(np.percentile(vals, 25))
            q3 = float(np.percentile(vals, 75))
            mean_val = float(np.mean(vals))
            block.update({
                "sd": round(float(statistics.stdev(vals)), 1),
                "iqr": round(q3 - q1, 1),
                "min": round(float(np.min(vals)), 1),
                "max": round(float(np.max(vals)), 1),
            })
            if len(vals) >= 10 and mean_val != 0:
                block["cv_pct"] = round(
                    100.0 * float(statistics.stdev(vals)) / mean_val,
                    1,
                )
                block["cv_available"] = True
            any_variability = True
        variability[context] = block

    # --------------------------------------------------------------
    # Time-of-day patterns, stratified within measurement context.
    # Suggested local buckets from the engineering spec.
    # --------------------------------------------------------------
    def _time_bucket(p):
        hour = _hr_local_datetime(p).hour
        if 4 <= hour <= 11:
            return "morning"
        if 12 <= hour <= 16:
            return "afternoon"
        if 17 <= hour <= 21:
            return "evening"
        return "overnight"

    time_of_day = {}
    any_time_pattern = False
    for context in known_contexts:
        group = context_groups.get(context) or []
        buckets = {
            "morning": [], "afternoon": [], "evening": [], "overnight": []
        }
        for p in group:
            buckets[_time_bucket(p)].append(p["value"])

        bucket_results = {}
        qualifying = []
        for name, vals in buckets.items():
            available = len(vals) >= 3
            if available:
                qualifying.append(name)
            bucket_results[name] = {
                "sample_count": len(vals),
                "is_available": available,
                "mean": round(float(np.mean(vals)), 1) if available else None,
                "median": round(float(np.median(vals)), 1) if available else None,
                "min": round(float(np.min(vals)), 1) if available else None,
                "max": round(float(np.max(vals)), 1) if available else None,
            }

        context_available = len(qualifying) >= 2
        highest = lowest = None
        if context_available:
            medians = {
                name: bucket_results[name]["median"]
                for name in qualifying
            }
            highest = max(medians, key=medians.get)
            lowest = min(medians, key=medians.get)
            any_time_pattern = True

        time_of_day[context] = {
            "is_available": context_available,
            "reason_code": None if context_available else "insufficient_bucket_support",
            "qualifying_bucket_count": len(qualifying),
            "highest_median_period": highest,
            "lowest_median_period": lowest,
            "buckets": bucket_results,
        }

    # --------------------------------------------------------------
    # Medication-change association: same-context 14 days before vs 14 days
    # after each recorded medication change. Observational data only.
    # --------------------------------------------------------------
    medication_correlations = []
    if medication_changes:
        for medication_id, medication_name, change_type, effective_date in medication_changes:
            if effective_date is None:
                continue
            for context in known_contexts:
                group = context_groups.get(context) or []
                before = []
                after = []
                for p in group:
                    local_day = _hr_local_datetime(p).date()
                    day_delta = (local_day - effective_date).days
                    if -14 <= day_delta <= -1:
                        before.append(p)
                    elif 0 <= day_delta <= 13:
                        after.append(p)

                if len(before) < 5 or len(after) < 5:
                    continue

                before_vals = [p["value"] for p in before]
                after_vals = [p["value"] for p in after]
                before_fit = _ols(before)
                after_fit = _ols(after)
                medication_correlations.append({
                    "medication_id": str(medication_id),
                    "medication_name": medication_name,
                    "change_type": change_type,
                    "effective_date": effective_date.isoformat(),
                    "measurement_context": context,
                    "window_days_before": 14,
                    "window_days_after": 14,
                    "before_n": len(before),
                    "after_n": len(after),
                    "before_mean": round(float(np.mean(before_vals)), 1),
                    "after_mean": round(float(np.mean(after_vals)), 1),
                    "mean_delta": round(
                        float(np.mean(after_vals) - np.mean(before_vals)), 1
                    ),
                    "before_median": round(float(np.median(before_vals)), 1),
                    "after_median": round(float(np.median(after_vals)), 1),
                    "median_delta": round(
                        float(np.median(after_vals) - np.median(before_vals)), 1
                    ),
                    "before_slope_mg_dl_per_day": (
                        before_fit["slope_mg_dl_per_day"] if before_fit else None
                    ),
                    "after_slope_mg_dl_per_day": (
                        after_fit["slope_mg_dl_per_day"] if after_fit else None
                    ),
                })

    # --------------------------------------------------------------
    # CGM / GMI capability structure.
    # Even if a row is tagged source_type=cgm, this schema currently has no
    # expected-sample/active-coverage metadata, so standardized CGM analysis
    # cannot qualify yet.
    # --------------------------------------------------------------
    cgm_count = source_counts.get("cgm", 0)
    if cgm_count:
        cgm_reason_code = "cgm_coverage_metadata_not_available"
        cgm_reason = (
            "CGM-tagged readings exist, but active-coverage metadata is not "
            "available to qualify a standardized CGM summary."
        )
    else:
        cgm_reason_code = "qualified_cgm_data_not_available"
        cgm_reason = (
            "GMI and standardized CGM metrics require qualified dense CGM data; "
            "manual/BGM spot readings are not used for this calculation."
        )

    cgm_requirement = {
        "minimum_days": 14,
        "minimum_active_coverage_pct": 70.0,
    }
    if cgm_count:
        gmi_display_message = (
            "GMI will appear after Vitals has enough qualified CGM data: "
            "at least 14 days represented with 70% or greater active coverage."
        )
    else:
        gmi_display_message = (
            "Vitals can calculate GMI from CGM data. GMI will appear after "
            "at least 14 days of CGM data with 70% or greater active coverage."
        )

    cgm_summary = {
        "is_available": False,
        "status": "not_enough_qualified_cgm_data",
        "reason_code": cgm_reason_code,
        "reason": cgm_reason,
        "display_message": gmi_display_message,
        "source_reading_count": cgm_count,
        "coverage_days": None,
        "active_coverage_pct": None,
        "qualification_requirement": cgm_requirement,
        "mean_glucose_mg_dl": None,
        "gmi_pct": None,
        "tir": None,
        "tar": None,
        "tbr": None,
        "cv_pct": None,
    }
    gmi = {
        "is_available": False,
        "status": "not_enough_qualified_cgm_data",
        "reason_code": cgm_reason_code,
        "reason": cgm_reason,
        "display_message": gmi_display_message,
        "qualification_requirement": cgm_requirement,
        "value_pct": None,
        "formula": "3.31 + 0.02392 * mean_cgm_glucose_mg_dl",
        "disclosure": (
            "GMI is calculated from mean CGM glucose. It is not a laboratory "
            "A1C result and may differ from your measured A1C."
        ),
    }

    # --------------------------------------------------------------
    # Capability / data-support metadata
    # --------------------------------------------------------------
    unavailable = []

    if not any(
        b.get("is_available")
        for b in context_summaries.values()
    ):
        unavailable.append({
            "analysis": "context_specific_summaries",
            "reason_code": "insufficient_contextual_support",
            "reason": "needs >=3 readings in the same recorded glucose context",
        })

    if not any_context_trend:
        unavailable.append({
            "analysis": "context_specific_trends",
            "reason_code": "insufficient_contextual_longitudinal_support",
            "reason": "needs >=5 readings in one context spanning >=7 days",
        })

    unavailable.append({
        "analysis": "logged_readings_in_target",
        "reason_code": "patient_target_profile_not_configured",
        "reason": (
            "context-specific glucose targets must be configured before "
            "manual/BGM logged readings can be classified against a target"
        ),
    })

    if not meal_pairs:
        unavailable.append({
            "analysis": "meal_excursions",
            "reason_code": "insufficient_pairs",
            "reason": (
                "needs an unambiguous pre-meal/post-meal pair with meal identity "
                "and post-meal timing"
            ),
        })

    if not any_variability:
        unavailable.append({
            "analysis": "variability",
            "reason_code": "insufficient_contextual_support",
            "reason": "needs >=5 comparable readings in the same glucose context",
        })

    if not any_time_pattern:
        unavailable.append({
            "analysis": "time_of_day_patterns",
            "reason_code": "insufficient_bucket_support",
            "reason": (
                "needs >=3 comparable readings in at least two local time-of-day "
                "buckets within the same glucose context"
            ),
        })

    if not medication_correlations:
        unavailable.append({
            "analysis": "medication_change_correlation",
            "reason_code": (
                "no_medication_changes"
                if not medication_changes
                else "insufficient_before_after_support"
            ),
            "reason": (
                "no medication-change events are recorded"
                if not medication_changes
                else "needs >=5 same-context readings in both the 14 days before and after a medication change"
            ),
        })

    unavailable.append({
        "analysis": "gmi",
        "reason_code": cgm_reason_code,
        "reason": cgm_reason,
    })

    support_state = (
        "trend"
        if any_context_trend
        else "descriptive"
        if len(points) >= 2
        else "snapshot"
    )

    return {
        "analysis_version": 2,
        "vital_type": "glucose",
        "unit": "mg/dL",
        "latest": latest_block,
        "reading_count": len(points),

        # Backwards-compatible mixed-context descriptive block for the current
        # MAUI/PDF surface. Dedicated Glucose UI should prefer context_summaries.
        "summary": summary,
        "change_from_first": None,
        "trend": None,

        "source_summary": {
            "counts": source_counts,
            "manual_bgm_count": source_counts.get("manual_bgm", 0),
            "cgm_count": cgm_count,
            "unknown_source_count": source_counts.get("unknown", 0),
        },
        "context_summary": {
            "counts": context_counts,
            "recorded_count": context_recorded_count,
            "unknown_count": context_counts.get("unknown", 0),
            "completeness_pct": context_completeness_pct,
        },
        "context_summaries": context_summaries,
        "context_trends": context_trends,
        "low_events": low_events,
        "logged_readings_in_target": logged_readings_in_target,
        "meal_excursions": meal_excursions,
        "variability": variability,
        "time_of_day": time_of_day,
        "medication_correlations": medication_correlations,
        "cgm_summary": cgm_summary,
        "gmi": gmi,
        "data_support": {
            "n": len(points),
            "distinct_days": distinct_days,
            "span_days": round(span_days, 1),
            "support_state": support_state,
            "context_completeness_pct": context_completeness_pct,
            "unavailable_analyses": unavailable,
        },
        "limitations": [
            (
                "Context-specific glucose analyses compare only readings recorded "
                "in the same measurement context; Vitals does not model one trend "
                "through mixed fasting, meal-related, bedtime, and random readings."
            ),
            (
                "Manual/BGM sampling is sparse and depends on when the patient "
                "chooses to test, so descriptive variability and time-of-day "
                "patterns do not represent continuous day-long glucose exposure."
            ),
            (
                "Medication comparisons are observational before/after summaries "
                "and do not establish that a medication caused a glucose change."
            ),
            (
                "GMI, Time in Range, and other standardized CGM summaries remain "
                "unavailable until sufficiently dense CGM data and coverage "
                "metadata are available."
            ),
        ],
    }


def _load_weight_patient_demographics(cur, patient_id: str, household_id: str) -> dict | None:
    """
    Loads current anthropometrics plus the complete ACTIVE dated height
    timeline required by Weight v6. Superseded corrections stay auditable in
    patient_height_history but must never participate in BMI calculations.
    """
    cur.execute("""
        SELECT
            p.dob,
            p.height_inches,
            h.effective_date,
            h.source
        FROM patients p
        LEFT JOIN LATERAL (
            SELECT effective_date, source
            FROM patient_height_history
            WHERE patient_id = p.patient_id
              AND household_id = p.household_id
              AND is_active = true
              AND height_inches = p.height_inches
            ORDER BY effective_date DESC, created_at DESC
            LIMIT 1
        ) h ON true
        WHERE p.patient_id = %s
          AND p.household_id = %s;
    """, (patient_id, household_id))
    demographic_row = cur.fetchone()
    if demographic_row is None:
        return None

    cur.execute("""
        SELECT height_inches, effective_date, source, entry_type, created_at
        FROM patient_height_history
        WHERE patient_id = %s
          AND household_id = %s
          AND is_active = true
        ORDER BY effective_date ASC, created_at ASC;
    """, (patient_id, household_id))
    height_rows = cur.fetchall()

    return {
        "dob": demographic_row[0],
        "height_inches": demographic_row[1],
        "height_effective_date": demographic_row[2],
        "height_source": demographic_row[3],
        "height_history": [
            {
                "height_inches": r[0],
                "effective_date": r[1],
                "source": r[2],
                "entry_type": r[3],
                "created_at": r[4],
            }
            for r in height_rows
        ],
    }


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
    "spo2": {
        "from_clause": "vitals",
        # Same-row vitals are included for linked/cross-vital context. There
        # is no spo2_context table yet, so activity/symptoms/oxygen-support
        # metadata remain explicitly unavailable rather than inferred.
        "columns": (
            "vitals.recorded_at, vitals.local_offset_minutes, vitals.oxygen_saturation, "
            "vitals.heart_rate, vitals.systolic, vitals.diastolic, "
            "vitals.temperature, vitals.weight, vitals.blood_glucose"
        ),
        "where_clause": "vitals.oxygen_saturation IS NOT NULL",
        "analysis_fn": run_spo2_analysis,
        # Fixed personal-baseline window: recent 7 days vs days 8-37.
        "baseline_lookback_days": 37,
    },
    "temperature": {
        "from_clause": "vitals",
        "columns": (
            "vitals.recorded_at, vitals.local_offset_minutes, vitals.temperature, "
            "vitals.temperature_site, vitals.source, vitals.heart_rate, "
            "vitals.oxygen_saturation, vitals.systolic, vitals.diastolic, "
            "vitals.blood_glucose, vitals.weight"
        ),
        "where_clause": "vitals.temperature IS NOT NULL",
        "analysis_fn": run_temperature_analysis,
        # Implementation lookback only. The baseline eligibility gate itself
        # is defined inside run_temperature_analysis and is independent of
        # this fetch window.
        "baseline_lookback_days": 60,
    },
    "weight": {
        "from_clause": "vitals",
        "columns": "vitals.recorded_at, vitals.local_offset_minutes, vitals.weight",
        "where_clause": "vitals.weight IS NOT NULL",
        "analysis_fn": run_weight_analysis,
        "needs_patient_demographics": True,
        # Increment when the Weight contract changes. The read path uses this
        # to ignore an older JSON cache row and recompute it immediately.
        "analysis_version": 6,
    },
    "glucose": {
        "from_clause": (
            "vitals LEFT JOIN glucose_context "
            "ON glucose_context.vital_id = vitals.vital_id"
        ),
        "columns": (
            "vitals.recorded_at, vitals.local_offset_minutes, vitals.blood_glucose, "
            "glucose_context.measurement_context, glucose_context.meal_type, "
            "glucose_context.minutes_after_meal, glucose_context.meal_event_id, "
            "glucose_context.source_type, glucose_context.original_value, "
            "glucose_context.original_unit, glucose_context.source_device"
        ),
        # Legacy rows have no glucose_context record and therefore NULL
        # is_invalidated; they remain visible as unknown-context history.
        "where_clause": (
            "vitals.blood_glucose IS NOT NULL "
            "AND (glucose_context.is_invalidated IS NULL "
            "OR glucose_context.is_invalidated = false)"
        ),
        "analysis_fn": run_glucose_analysis,
        "needs_medication_changes": True,
        # First dedicated context-aware Glucose contract. This invalidates
        # stale generic descriptive cache JSON as soon as the branch deploys.
        "analysis_version": 2,
    },
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

        # Patient demographics are fetched only for analyses that declare
        # the dependency. Weight uses DOB + current profile height plus the
        # dated height-history metadata for age-gated adult current BMI.
        patient_demographics = None
        if entry.get("needs_patient_demographics"):
            patient_demographics = _load_weight_patient_demographics(
                cur,
                patient_id,
                household_id,
            )

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
        if entry.get("needs_patient_demographics"):
            extra_kwargs["patient_demographics"] = patient_demographics

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
                cached = row[0]
                required_version = entry.get("analysis_version")
                if (
                    required_version is None
                    or (
                        isinstance(cached, dict)
                        and cached.get("analysis_version") == required_version
                    )
                ):
                    return cached  # valid cache hit
                # Contract changed (currently used by Weight v6). Fall through
                # and recompute this window instead of serving stale JSON.

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

        patient_demographics = None
        if entry.get("needs_patient_demographics"):
            patient_demographics = _load_weight_patient_demographics(
                cur,
                patient_id,
                household_id,
            )

        extra_kwargs = {}
        if lookback:
            extra_kwargs["baseline_rows"] = baseline_rows
        if entry.get("needs_medication_changes"):
            extra_kwargs["medication_changes"] = medication_changes
        if prior_lookback:
            extra_kwargs["prior_period_rows"] = prior_period_rows
        if entry.get("needs_patient_demographics"):
            extra_kwargs["patient_demographics"] = patient_demographics

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


def get_weight_analysis_for_window(
    patient_id: str,
    household_id: str,
    days: int,
) -> dict | None:
    """
    Weight is normally sparse (weekly/monthly is common), so a short report
    window must not make the entire Weight section disappear.

    First use the normal windowed/cache path. If that window contains no
    Weight reading at all, fall back to the patient's most recent known Weight
    as a SNAPSHOT only. Longitudinal calculations remain unavailable because
    run_weight_analysis receives exactly one point in that fallback case.

    This wrapper is shared by the app Analysis endpoint and the clinician PDF
    so the two surfaces cannot disagree about whether a Weight snapshot exists.
    """
    result = get_cached_or_compute_analysis(
        patient_id,
        household_id,
        "weight",
        days,
    )
    if result is not None:
        return result

    conn = get_conn()
    cur = conn.cursor()
    try:
        cur.execute("""
            SELECT recorded_at, local_offset_minutes, weight
            FROM vitals
            WHERE patient_id = %s
              AND household_id = %s
              AND weight IS NOT NULL
            ORDER BY recorded_at DESC
            LIMIT 1;
        """, (patient_id, household_id))
        latest_weight = cur.fetchone()

        if latest_weight is None:
            return None

        patient_demographics = _load_weight_patient_demographics(
            cur,
            patient_id,
            household_id,
        )

        result = run_weight_analysis(
            [latest_weight],
            patient_demographics=patient_demographics,
        )
        if result is None:
            return None

        support = result.setdefault("data_support", {})
        support["support_state"] = "snapshot"
        support["outside_report_window"] = True
        support["report_window_days"] = int(days)

        limitations = result.setdefault("limitations", [])
        limitations.insert(
            0,
            (
                f"No weight reading falls within the selected {days}-day window. "
                "The most recent known weight is shown as a current snapshot; "
                "change, variability, and trend calculations require additional "
                "measurements in the selected analysis period."
            ),
        )
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


PERMANENT_COMPLIMENTARY_TIERS = {"founder", "beta"}

# Patient capacity follows the EFFECTIVE entitlement, not the household's
# stored tier/limit. This matters when a Trial, paid subscription, or grace
# period expires: the stored commercial row may still describe the previous
# plan while effective access has already fallen back to Basic.
EFFECTIVE_PLAN_PATIENT_LIMITS = {
    "basic": 2,
    "trial": 5,
    "standard": 2,
    "family": 5,
    "founder": None,
    "beta": None,
}

# Standard and Basic are owner-managed households. Family permits a delegated
# manager. Trial, Founder, and Beta represent full-access / Family-capacity
# experiences, so they also honor an explicitly assigned manager role.
DELEGATED_HOUSEHOLD_MANAGER_PLANS = {"trial", "family", "founder", "beta"}


def is_permanent_complimentary_tier(tier: Optional[str]) -> bool:
    return (tier or "").strip().lower() in PERMANENT_COMPLIMENTARY_TIERS


def effective_patient_limit(
    effective_plan: Optional[str],
    stored_patient_limit: Optional[int],
) -> Optional[int]:
    """
    Resolve capacity from the effective entitlement plan.

    Known Vitals plans use their product-defined limits regardless of a stale
    stored patient_limit value. Unknown/legacy plans retain the stored limit
    when present and otherwise fall back safely to two patients.
    """
    normalized_plan = (effective_plan or "").strip().lower()
    if normalized_plan in EFFECTIVE_PLAN_PATIENT_LIMITS:
        return EFFECTIVE_PLAN_PATIENT_LIMITS[normalized_plan]
    return stored_patient_limit if stored_patient_limit is not None else 2


def count_reserved_slots(cur, household_id: str) -> tuple[Optional[int], int, int]:
    """
    Returns (patient_limit, actual_patient_count, active_pending_invite_count).

    patient_limit=NULL means unlimited and is reserved for permanent
    complimentary households (Founder or Beta). Every unused, unexpired
    invite reserves one finite slot for normal households; complimentary
    households bypass the slot check.
    """
    # Reuse the central entitlement resolver so slot enforcement follows the
    # same effective plan returned by /api/household/entitlement. In
    # particular, an expired Trial must immediately enforce Basic's 2-patient
    # capacity even if the stored row still says tier='trial', patient_limit=5.
    entitlement = get_household_entitlement(cur, household_id)
    patient_limit = entitlement["patient_limit"]
    patient_count = entitlement["patient_count"]

    cur.execute("""
        SELECT COUNT(*) FROM household_invites
        WHERE household_id = %s AND used_at IS NULL AND expires_at > now()
    """, (household_id,))
    pending_invite_count = cur.fetchone()[0]

    return patient_limit, patient_count, pending_invite_count


def get_household_entitlement(cur, household_id: str, user_id: Optional[str] = None) -> dict:
    """
    Central Phase 7 entitlement calculation.

    This function is deliberately read-only. It resolves both commercial
    access and effective household-management authorization so every endpoint
    uses the same owner/manager rules.

    Founder and Beta are permanent complimentary full-access tiers with no
    expiration and no patient limit. Existing alpha trials that predate
    trial_ends_at remain active
    until they are explicitly migrated; this avoids accidentally locking
    current testers during the entitlement rollout.
    """
    cur.execute("""
        SELECT
            tier,
            subscription_status,
            trial_started_at,
            trial_ends_at,
            patient_limit,
            owner_user_id,
            billing_owner_user_id,
            billing_provider,
            billing_product_id,
            subscription_started_at,
            subscription_ends_at,
            grace_ends_at,
            cancel_at_period_end
        FROM households
        WHERE household_id = %s
    """, (household_id,))
    row = cur.fetchone()
    if not row:
        raise HTTPException(status_code=404, detail="Household not found")

    (
        tier,
        subscription_status,
        trial_started_at,
        trial_ends_at,
        patient_limit,
        owner_user_id,
        billing_owner_user_id,
        billing_provider,
        billing_product_id,
        subscription_started_at,
        subscription_ends_at,
        grace_ends_at,
        cancel_at_period_end,
    ) = row

    now = datetime.now(timezone.utc)
    tier = (tier or "basic").lower()
    subscription_status = (subscription_status or "basic").lower()

    is_founder = tier == "founder"
    is_beta = tier == "beta"
    is_permanent_complimentary = is_founder or is_beta
    legacy_trial = subscription_status == "trial" and trial_ends_at is None
    trial_active = (
        subscription_status == "trial"
        and (trial_ends_at is None or trial_ends_at > now)
    )
    trial_expired = (
        subscription_status == "trial"
        and trial_ends_at is not None
        and trial_ends_at <= now
    )

    subscription_active = (
        subscription_status == "active"
        and tier in ("standard", "family", "founder", "beta")
        and (
            is_permanent_complimentary
            or subscription_ends_at is None
            or subscription_ends_at > now
        )
    )
    subscription_expired = (
        subscription_status == "active"
        and not is_permanent_complimentary
        and subscription_ends_at is not None
        and subscription_ends_at <= now
    )

    grace_active = (
        subscription_status == "grace"
        and grace_ends_at is not None
        and grace_ends_at > now
    )
    grace_expired = (
        subscription_status == "grace"
        and (grace_ends_at is None or grace_ends_at <= now)
    )

    has_premium_access = bool(
        is_permanent_complimentary or trial_active or subscription_active or grace_active
    )

    if is_founder:
        access_state = "founder"
        effective_plan = "founder"
    elif is_beta:
        access_state = "beta"
        effective_plan = "beta"
    elif trial_active:
        access_state = "trial"
        effective_plan = "trial"
    elif trial_expired:
        access_state = "trial_expired"
        effective_plan = "basic"
    elif subscription_active:
        access_state = "active"
        effective_plan = tier
    elif subscription_expired:
        access_state = "subscription_expired"
        effective_plan = "basic"
    elif grace_active:
        access_state = "grace"
        effective_plan = tier
    elif grace_expired:
        access_state = "grace_expired"
        effective_plan = "basic"
    else:
        access_state = "basic"
        effective_plan = "basic"

    # Capacity must follow the resolved entitlement. A stored Trial/Family
    # limit of 5 must not survive an effective fallback to Basic.
    resolved_patient_limit = effective_patient_limit(effective_plan, patient_limit)

    cur.execute("""
        SELECT
            COUNT(*) AS patient_count,
            COUNT(*) FILTER (WHERE entitlement_locked = false) AS stored_unlocked_count,
            COUNT(*) FILTER (WHERE entitlement_locked = true) AS stored_locked_count
        FROM patients
        WHERE household_id = %s
    """, (household_id,))
    patient_count, stored_unlocked_count, stored_locked_count = cur.fetchone()

    lock_enforcement_required = (
        resolved_patient_limit is not None
        and patient_count > resolved_patient_limit
    )
    active_patient_count = (
        stored_unlocked_count if lock_enforcement_required else patient_count
    )
    locked_patient_count = (
        stored_locked_count if lock_enforcement_required else 0
    )

    household_role = "member"
    if user_id:
        cur.execute("""
            SELECT household_role
            FROM users
            WHERE user_id = %s AND household_id = %s
        """, (user_id, household_id))
        role_row = cur.fetchone()
        if role_row and role_row[0]:
            household_role = role_row[0]

    owner_id = str(owner_user_id) if owner_user_id else None
    billing_owner_id = str(billing_owner_user_id) if billing_owner_user_id else None
    caller_id = str(user_id) if user_id else None

    # Ownership is authoritative from households.owner_user_id, not merely
    # users.household_role='owner'. That prevents a stale/malformed role row
    # from granting owner powers.
    is_household_owner = bool(caller_id and owner_id == caller_id)
    delegated_management_allowed = (
        effective_plan in DELEGATED_HOUSEHOLD_MANAGER_PLANS
    )
    is_delegated_household_manager = bool(
        household_role == "manager"
        and delegated_management_allowed
    )
    # "is_household_manager" means effectively authorized to administer the
    # household; owners always qualify. A stored manager on Standard/Basic
    # remains a manager role in the database but is not authorized there.
    is_household_manager = bool(
        is_household_owner or is_delegated_household_manager
    )
    is_billing_owner = bool(caller_id and billing_owner_id == caller_id)
    can_manage_household = is_household_manager
    can_start_purchase = (
        is_household_manager
        and billing_owner_id is None
        and not is_permanent_complimentary
    )
    # Apple/Google store billing belongs to the account that made the
    # purchase. Household ownership can transfer without transferring the
    # underlying store subscription, so only the billing owner can manage
    # that store purchase.
    can_manage_billing = bool(
        not is_permanent_complimentary
        and caller_id
        and is_billing_owner
        and billing_owner_id is not None
    )

    return {
        "plan": tier,
        "effective_plan": effective_plan,
        "subscription_status": subscription_status,
        "access_state": access_state,
        "has_premium_access": has_premium_access,
        "is_founder": is_founder,
        "is_beta": is_beta,
        "is_unlimited": is_permanent_complimentary,
        "patient_limit": resolved_patient_limit,
        "patient_count": patient_count,
        "active_patient_count": active_patient_count,
        "locked_patient_count": locked_patient_count,
        "trial_started_at": trial_started_at,
        "trial_ends_at": trial_ends_at,
        "legacy_trial": legacy_trial,
        "subscription_started_at": subscription_started_at,
        "subscription_ends_at": subscription_ends_at,
        "grace_ends_at": grace_ends_at,
        "cancel_at_period_end": bool(cancel_at_period_end),
        "billing_provider": billing_provider,
        "billing_product_id": billing_product_id,
        "household_role": household_role,
        "delegated_management_allowed": delegated_management_allowed,
        "is_household_owner": is_household_owner,
        "is_household_manager": is_household_manager,
        "is_billing_owner": is_billing_owner,
        "can_manage_household": can_manage_household,
        "can_start_purchase": can_start_purchase,
        "can_manage_billing": can_manage_billing,
        "requires_basic_patient_selection": (
            lock_enforcement_required
            and active_patient_count != resolved_patient_limit
        ),
    }

def require_household_management(
    cur,
    household_id: str,
    user_id: Optional[str],
) -> dict:
    """
    Fail closed unless the caller is the household owner or an effectively
    authorized delegated manager under the CURRENT entitlement.

    Standard/Basic intentionally do not permit delegated managers; Family and
    full-access equivalents do. Returning the entitlement lets callers reuse
    the already-resolved plan/role state without duplicating policy checks.
    """
    if not user_id:
        raise HTTPException(
            status_code=401,
            detail="This requires a signed-in account."
        )

    entitlement = get_household_entitlement(cur, household_id, user_id)
    if entitlement["can_manage_household"]:
        return entitlement

    if (
        entitlement["household_role"] == "manager"
        and not entitlement["delegated_management_allowed"]
    ):
        plan_name = (entitlement["effective_plan"] or "current").title()
        raise HTTPException(
            status_code=403,
            detail=f"Only the household owner can manage household members on the {plan_name} plan."
        )

    raise HTTPException(
        status_code=403,
        detail="Only the household owner or an authorized household manager can manage household members."
    )


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
            heart_rate, temperature, temperature_site, blood_glucose,
            weight, source, notes
        )
        VALUES (%s,%s,COALESCE(%s, now()),%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s)
        RETURNING vital_id;
    """, (
        household_id, vital.patient_id, vital.recorded_at, vital.local_offset_minutes,
        vital.systolic, vital.diastolic, vital.oxygen_saturation,
        vital.heart_rate, vital.temperature,
        (vital.temperature_site or "unknown") if vital.temperature is not None else None,
        vital.blood_glucose, vital.weight, vital.source, vital.notes
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

    # Glucose context follows the same one-to-one pattern as heart rate:
    # the value itself stays in vitals while the metadata that determines
    # whether readings are analytically comparable lives in a context row.
    # Legacy readings without this row remain valid and are treated as
    # unknown-context by the dedicated Glucose engine.
    if vital.blood_glucose is not None:
        measurement_context = vital.glucose_context or "unknown"
        meal_type = (
            vital.glucose_meal_type
            if measurement_context in ("pre_meal", "post_meal")
            else None
        )
        minutes_after_meal = (
            vital.glucose_minutes_after_meal
            if measurement_context == "post_meal"
            else None
        )
        cur.execute("""
            INSERT INTO glucose_context (
                vital_id, measurement_context, meal_type, minutes_after_meal,
                meal_event_id, source_type, original_value, original_unit,
                source_device, is_invalidated
            )
            VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, false);
        """, (
            vital_id,
            measurement_context,
            meal_type,
            minutes_after_meal,
            vital.glucose_meal_event_id,
            vital.glucose_source_type or "manual_bgm",
            vital.glucose_original_value,
            vital.glucose_original_unit,
            vital.glucose_source_device,
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
    if vital.oxygen_saturation is not None:
        background_tasks.add_task(recompute_vital_cache, vital.patient_id, household_id, "spo2")
    if vital.temperature is not None:
        background_tasks.add_task(recompute_vital_cache, vital.patient_id, household_id, "temperature")
    if vital.weight is not None:
        background_tasks.add_task(recompute_vital_cache, vital.patient_id, household_id, "weight")
    if vital.blood_glucose is not None:
        background_tasks.add_task(recompute_vital_cache, vital.patient_id, household_id, "glucose")

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
    # "Latest" is per vital, not "whatever happened to be populated on
    # the newest row." A user may log weight today and BP yesterday; the
    # dashboard should still show yesterday's BP as the latest BP instead
    # of replacing it with an em dash just because today's row has no BP.
    cur.execute(f"""
        SELECT
            (SELECT recorded_at FROM vitals
             WHERE patient_id = %s AND household_id = %s
             ORDER BY recorded_at DESC LIMIT 1) AS latest_event_at,

            (SELECT systolic FROM vitals
             WHERE patient_id = %s AND household_id = %s
               AND systolic IS NOT NULL AND diastolic IS NOT NULL
             ORDER BY recorded_at DESC LIMIT 1) AS systolic,

            (SELECT diastolic FROM vitals
             WHERE patient_id = %s AND household_id = %s
               AND systolic IS NOT NULL AND diastolic IS NOT NULL
             ORDER BY recorded_at DESC LIMIT 1) AS diastolic,

            (SELECT oxygen_saturation FROM vitals
             WHERE patient_id = %s AND household_id = %s
               AND oxygen_saturation IS NOT NULL
             ORDER BY recorded_at DESC LIMIT 1) AS oxygen_saturation,

            (SELECT heart_rate FROM vitals
             WHERE patient_id = %s AND household_id = %s
               AND heart_rate IS NOT NULL
             ORDER BY recorded_at DESC LIMIT 1) AS heart_rate,

            (SELECT temperature FROM vitals
             WHERE patient_id = %s AND household_id = %s
               AND temperature IS NOT NULL
             ORDER BY recorded_at DESC LIMIT 1) AS temperature,

            (SELECT temperature_site FROM vitals
             WHERE patient_id = %s AND household_id = %s
               AND temperature IS NOT NULL
             ORDER BY recorded_at DESC LIMIT 1) AS temperature_site,

            (SELECT weight FROM vitals
             WHERE patient_id = %s AND household_id = %s
               AND weight IS NOT NULL
             ORDER BY recorded_at DESC LIMIT 1) AS weight,

            (SELECT blood_glucose FROM vitals
             WHERE patient_id = %s AND household_id = %s
               AND blood_glucose IS NOT NULL
             ORDER BY recorded_at DESC LIMIT 1) AS blood_glucose;
    """, (
        patient_id, household_id,
        patient_id, household_id,
        patient_id, household_id,
        patient_id, household_id,
        patient_id, household_id,
        patient_id, household_id,
        patient_id, household_id,
        patient_id, household_id,
        patient_id, household_id,
    ))
    row = cur.fetchone()
    cur.close()
    conn.close()
    if not row or row[0] is None:
        return {}
    return {
        "recorded_at": row[0],
        "systolic": row[1],
        "diastolic": row[2],
        "oxygen_saturation": row[3],
        "heart_rate": row[4],
        "temperature": row[5],
        "temperature_site": row[6],
        "weight": float(row[7]) if row[7] is not None else None,
        "blood_glucose": row[8]
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
        SELECT vital_id, recorded_at, systolic, diastolic, oxygen_saturation,
               heart_rate, round(temperature, 1), temperature_site, weight, blood_glucose
        FROM vitals
        WHERE patient_id = %s
          AND household_id = %s
          AND recorded_at >= now() - interval '%s days'
        ORDER BY recorded_at;
    """, (patient_id, household_id, days))
    rows = cur.fetchall()
    cur.close()
    conn.close()
    return {
        "rows": [
            {
                "vital_id": str(r[0]),
                "date": r[1].isoformat(),
                "systolic": r[2],
                "diastolic": r[3],
                "spo2": r[4],
                "heart_rate": r[5],
                "temperature": float(r[6]) if r[6] is not None else None,
                "temperature_site": r[7],
                "weight": float(r[8]) if r[8] is not None else None,
                "blood_glucose": r[9]
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
    days: int = 15,
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

    # Heart Rate, SpO2, and Temperature use dedicated engines through
    # the shared registry/cache path. Each is independently computed and
    # gated on its OWN observations.
    hr_analysis      = get_cached_or_compute_analysis(patient_id, household_id, "heart_rate", days)
    spo2_analysis    = get_cached_or_compute_analysis(patient_id, household_id, "spo2", days)
    temp_analysis    = get_cached_or_compute_analysis(patient_id, household_id, "temperature", days)
    weight_analysis  = get_weight_analysis_for_window(patient_id, household_id, days)
    glucose_analysis = get_cached_or_compute_analysis(patient_id, household_id, "glucose", days)

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
            "weight":        weight_analysis,
            "glucose":       glucose_analysis,
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
        "weight":         weight_analysis,
        "glucose":        glucose_analysis,
        "pcp_name":       pcp_name,
        "next_followup":  next_followup,
    }

# --------------------
# VITAL RECORD MANAGEMENT ENDPOINTS
# --------------------
# These routes intentionally come AFTER the static /api/vitals/latest,
# /history, /averages and /analysis routes so the {vital_id} path parameter
# can never swallow one of those literal route names.

@app.get("/api/vitals/{vital_id}")
def get_vital_record(
    vital_id: str,
    x_api_key: str = Header(..., alias="X-API-KEY"),
    household_id: str = Depends(get_household_id)
):
    check_key(x_api_key)
    try:
        UUID(vital_id)
    except Exception:
        raise HTTPException(status_code=400, detail="Invalid vital record id")

    conn = get_conn()
    cur = conn.cursor()
    try:
        verify_child_record_household(
            cur, "vitals", "vital_id", vital_id, household_id
        )
        cur.execute("""
            SELECT
                v.vital_id, v.patient_id, v.recorded_at, v.local_offset_minutes,
                v.systolic, v.diastolic, v.oxygen_saturation, v.heart_rate,
                v.temperature, v.temperature_site, v.blood_glucose, v.weight,
                v.source, v.notes,
                h.activity_context, h.posture, h.source_type,
                g.measurement_context, g.meal_type, g.minutes_after_meal,
                g.meal_event_id, g.source_type, g.original_value,
                g.original_unit, g.source_device
            FROM vitals v
            LEFT JOIN heart_rate_context h ON h.vital_id = v.vital_id
            LEFT JOIN glucose_context g ON g.vital_id = v.vital_id
            WHERE v.vital_id = %s
              AND v.household_id = %s;
        """, (vital_id, household_id))
        row = cur.fetchone()
        if row is None:
            raise HTTPException(status_code=404, detail="Vital record not found")

        return {
            "vital_id": str(row[0]),
            "patient_id": str(row[1]),
            "recorded_at": row[2],
            "local_offset_minutes": row[3],
            "systolic": row[4],
            "diastolic": row[5],
            "oxygen_saturation": row[6],
            "heart_rate": row[7],
            "temperature": float(row[8]) if row[8] is not None else None,
            "temperature_site": row[9],
            "blood_glucose": row[10],
            "weight": float(row[11]) if row[11] is not None else None,
            "source": row[12],
            "notes": row[13] or "",
            "hr_activity_context": row[14],
            "hr_posture": row[15],
            "hr_source_type": row[16],
            "glucose_context": row[17],
            "glucose_meal_type": row[18],
            "glucose_minutes_after_meal": row[19],
            "glucose_meal_event_id": str(row[20]) if row[20] is not None else None,
            "glucose_source_type": row[21],
            "glucose_original_value": float(row[22]) if row[22] is not None else None,
            "glucose_original_unit": row[23],
            "glucose_source_device": row[24],
        }
    finally:
        cur.close()
        conn.close()


@app.patch("/api/vitals/{vital_id}")
def update_vital_record(
    vital_id: str,
    vital: VitalCreate,
    background_tasks: BackgroundTasks,
    x_api_key: str = Header(..., alias="X-API-KEY"),
    household_id: str = Depends(get_household_id)
):
    check_key(x_api_key)
    try:
        UUID(vital_id)
    except Exception:
        raise HTTPException(status_code=400, detail="Invalid vital record id")

    conn = get_conn()
    cur = conn.cursor()
    try:
        # Lock the record while the full measurement event (vitals row +
        # one-to-one context rows) is being corrected.
        cur.execute("""
            SELECT patient_id
            FROM vitals
            WHERE vital_id = %s AND household_id = %s
            FOR UPDATE;
        """, (vital_id, household_id))
        existing = cur.fetchone()
        if existing is None:
            raise HTTPException(status_code=404, detail="Vital record not found")

        old_patient_id = str(existing[0])
        verify_patient_household(cur, old_patient_id, household_id)
        verify_patient_household(cur, vital.patient_id, household_id)

        # The correction endpoint accepts a complete editable representation
        # of the historical measurement event. Source/created_at stay immutable;
        # everything a caregiver can actually correct is written atomically.
        cur.execute("""
            UPDATE vitals
            SET patient_id = %s,
                recorded_at = COALESCE(%s, recorded_at),
                local_offset_minutes = %s,
                systolic = %s,
                diastolic = %s,
                oxygen_saturation = %s,
                heart_rate = %s,
                temperature = %s,
                temperature_site = %s,
                blood_glucose = %s,
                weight = %s,
                notes = %s
            WHERE vital_id = %s AND household_id = %s;
        """, (
            vital.patient_id,
            vital.recorded_at,
            vital.local_offset_minutes,
            vital.systolic,
            vital.diastolic,
            vital.oxygen_saturation,
            vital.heart_rate,
            vital.temperature,
            (vital.temperature_site or "unknown") if vital.temperature is not None else None,
            vital.blood_glucose,
            vital.weight,
            vital.notes,
            vital_id,
            household_id,
        ))

        if vital.heart_rate is None:
            # heart_rate_context currently has ON DELETE NO ACTION, so keeping
            # context lifecycle explicit also makes DELETE safe below.
            cur.execute("DELETE FROM heart_rate_context WHERE vital_id = %s;", (vital_id,))
        else:
            cur.execute("""
                INSERT INTO heart_rate_context (
                    vital_id, activity_context, posture, source_type, is_invalidated
                )
                VALUES (%s, %s, %s, %s, false)
                ON CONFLICT (vital_id)
                DO UPDATE SET
                    activity_context = EXCLUDED.activity_context,
                    posture = EXCLUDED.posture,
                    source_type = EXCLUDED.source_type;
            """, (
                vital_id,
                vital.hr_activity_context,
                vital.hr_posture,
                vital.hr_source_type or "manual",
            ))

        if vital.blood_glucose is None:
            cur.execute("DELETE FROM glucose_context WHERE vital_id = %s;", (vital_id,))
        else:
            measurement_context = vital.glucose_context or "unknown"
            meal_type = (
                vital.glucose_meal_type
                if measurement_context in ("pre_meal", "post_meal")
                else None
            )
            minutes_after_meal = (
                vital.glucose_minutes_after_meal
                if measurement_context == "post_meal"
                else None
            )
            cur.execute("""
                INSERT INTO glucose_context (
                    vital_id, measurement_context, meal_type, minutes_after_meal,
                    meal_event_id, source_type, original_value, original_unit,
                    source_device, is_invalidated
                )
                VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, false)
                ON CONFLICT (vital_id)
                DO UPDATE SET
                    measurement_context = EXCLUDED.measurement_context,
                    meal_type = EXCLUDED.meal_type,
                    minutes_after_meal = EXCLUDED.minutes_after_meal,
                    meal_event_id = EXCLUDED.meal_event_id,
                    source_type = EXCLUDED.source_type,
                    original_value = EXCLUDED.original_value,
                    original_unit = EXCLUDED.original_unit,
                    source_device = EXCLUDED.source_device;
            """, (
                vital_id,
                measurement_context,
                meal_type,
                minutes_after_meal,
                vital.glucose_meal_event_id,
                vital.glucose_source_type or "manual_bgm",
                vital.glucose_original_value,
                vital.glucose_original_unit,
                vital.glucose_source_device,
            ))

        conn.commit()
    except Exception:
        conn.rollback()
        raise
    finally:
        cur.close()
        conn.close()

    # Corrections are rare and can alter same-row/cross-vital context, so
    # favor correctness over micro-optimizing which cached analyses might
    # have changed. Reassignment recomputes both the old and new patient.
    affected_patients = {old_patient_id, vital.patient_id}
    for patient_id in affected_patients:
        for vital_type in VITAL_ANALYSIS_REGISTRY:
            background_tasks.add_task(
                recompute_vital_cache,
                patient_id,
                household_id,
                vital_type,
            )

    return {"status": "success", "message": "Vital record updated"}


@app.delete("/api/vitals/{vital_id}")
def delete_vital_record(
    vital_id: str,
    background_tasks: BackgroundTasks,
    x_api_key: str = Header(..., alias="X-API-KEY"),
    household_id: str = Depends(get_household_id)
):
    check_key(x_api_key)
    try:
        UUID(vital_id)
    except Exception:
        raise HTTPException(status_code=400, detail="Invalid vital record id")

    conn = get_conn()
    cur = conn.cursor()
    try:
        cur.execute("""
            SELECT patient_id
            FROM vitals
            WHERE vital_id = %s AND household_id = %s
            FOR UPDATE;
        """, (vital_id, household_id))
        existing = cur.fetchone()
        if existing is None:
            raise HTTPException(status_code=404, detail="Vital record not found")

        patient_id = str(existing[0])
        verify_patient_household(cur, patient_id, household_id)

        # Explicitly remove context first. glucose_context already cascades,
        # but heart_rate_context does not; doing both here keeps the behavior
        # obvious and transactionally consistent.
        cur.execute("DELETE FROM heart_rate_context WHERE vital_id = %s;", (vital_id,))
        cur.execute("DELETE FROM glucose_context WHERE vital_id = %s;", (vital_id,))
        cur.execute(
            "DELETE FROM vitals WHERE vital_id = %s AND household_id = %s;",
            (vital_id, household_id),
        )
        conn.commit()
    except Exception:
        conn.rollback()
        raise
    finally:
        cur.close()
        conn.close()

    for vital_type in VITAL_ANALYSIS_REGISTRY:
        background_tasks.add_task(
            recompute_vital_cache,
            patient_id,
            household_id,
            vital_type,
        )

    return {"status": "success", "message": "Vital record deleted"}


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
            SELECT p.patient_id, p.first_name, p.last_name, p.dob, p.gender, p.height_inches,
                   p.entitlement_locked
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
            SELECT p.patient_id, p.first_name, p.last_name, p.dob, p.gender, p.height_inches,
                   p.entitlement_locked
            FROM patients p
            LEFT JOIN patient_users pu
                ON pu.patient_id = p.patient_id
                AND pu.user_id = %s
                AND pu.relationship = 'self'
            WHERE p.household_id = %s
            ORDER BY (pu.patient_id IS NULL), p.first_name
        """, (caller_user_id, household_id))

    rows = cur.fetchall()

    entitlement = get_household_entitlement(cur, household_id)
    patient_limit = entitlement["patient_limit"]
    enforce_locks = (
        patient_limit is not None
        and entitlement["patient_count"] > patient_limit
    )

    patients = [
        {
            "patient_id": r[0],
            "first_name": r[1],
            "last_name": r[2],
            "dob": r[3],
            "gender": r[4],
            "height_inches": r[5],
            "is_entitlement_locked": bool(enforce_locks and r[6]),
        }
        for r in rows
    ]

    if exclude_self_claimed:
        patients = [p for p in patients if not p["is_entitlement_locked"]]

    cur.close()
    conn.close()
    return patients

@app.post("/api/patients", response_model=PatientOut)
def create_patient(
    p: PatientCreate,
    household_id: str = Depends(get_household_id),
    auth: dict = Depends(get_auth),
):
    conn = get_conn()
    cur = conn.cursor()

    entitlement = get_household_entitlement(cur, household_id)
    patient_limit = entitlement["patient_limit"]
    current_count = entitlement["patient_count"]
    if patient_limit is not None and current_count >= patient_limit:
        cur.close()
        conn.close()
        raise HTTPException(
            status_code=403,
            detail=f"You've reached your plan's limit of {patient_limit} patient{'s' if patient_limit != 1 else ''}. Upgrade to add more."
        )

    cur.execute("""
        INSERT INTO patients (first_name, last_name, dob, gender, height_inches, household_id)
        VALUES (%s, %s, %s, %s, %s, %s)
        RETURNING patient_id, first_name, last_name, dob, gender, height_inches
    """, (p.first_name, p.last_name, p.dob, p.gender, p.height_inches, household_id))
    row = cur.fetchone()

    creating_user_id = None
    if auth.get("type") != "api_key":
        creating_user_id = auth.get("sub")

    if p.height_inches is not None:
        cur.execute("""
            INSERT INTO patient_height_history
                (patient_id, household_id, height_inches, effective_date,
                 entry_type, source, created_by)
            VALUES (%s, %s, %s, current_date, 'measurement', 'onboarding', %s);
        """, (
            row[0],
            household_id,
            p.height_inches,
            creating_user_id,
        ))

    # Record who created this patient and how they relate to them. Not an
    # access-control mechanism — household-wide access is still the model —
    # just relationship metadata (see PatientCreate.relationship) so a
    # future feature (primary caregiver, per-patient notifications, a real
    # visibility-restriction mode) has real data to work from instead of
    # needing a backfill migration first. Only meaningful for real
    # JWT-authenticated users; the legacy API-key path (Home Assistant) has
    # no actual user_id to link, so it's skipped there.
    if auth.get("type") != "api_key":
        if creating_user_id:
            cur.execute("""
                INSERT INTO patient_users (patient_id, user_id, relationship, created_at)
                VALUES (%s, %s, %s, now())
            """, (row[0], creating_user_id, p.relationship or "caregiver"))
    conn.commit()
    cur.close()
    conn.close()
    return {
        "patient_id": str(row[0]),
        "first_name": row[1],
        "last_name": row[2],
        "dob": row[3],
        "gender": row[4],
        "height_inches": row[5],
    }


@app.patch("/api/patients/{patient_id}", response_model=PatientOut)
def update_patient_demographics(
    patient_id: UUID,
    body: PatientDemographicsUpdate,
    household_id: str = Depends(get_household_id),
    auth: dict = Depends(get_auth),
):
    """
    Updates patient demographics.

    New app builds record height through the dated /height endpoint below.
    height_inches remains accepted here for backward compatibility; when an
    older build explicitly changes it, a dated measurement is automatically
    captured so history cannot silently diverge from patients.height_inches.
    Fields omitted from PATCH are preserved; an explicit null can still clear
    the legacy current-height cache if an older client requests that.
    """
    fields_set = getattr(
        body,
        "model_fields_set",
        getattr(body, "__fields_set__", set()),
    )
    gender_was_sent = "gender" in fields_set
    height_was_sent = "height_inches" in fields_set

    conn = get_conn()
    cur = conn.cursor()
    try:
        verify_patient_household(cur, str(patient_id), household_id)

        cur.execute("""
            SELECT gender, height_inches
            FROM patients
            WHERE patient_id = %s
              AND household_id = %s
            FOR UPDATE;
        """, (str(patient_id), household_id))
        existing = cur.fetchone()
        if not existing:
            raise HTTPException(status_code=404, detail="Patient not found")

        old_gender, old_height = existing
        next_gender = body.gender if gender_was_sent else old_gender
        next_height = body.height_inches if height_was_sent else old_height

        cur.execute("""
            UPDATE patients
            SET gender = %s,
                height_inches = %s
            WHERE patient_id = %s
              AND household_id = %s
            RETURNING patient_id, first_name, last_name, dob, gender, height_inches;
        """, (
            next_gender,
            next_height,
            str(patient_id),
            household_id,
        ))
        row = cur.fetchone()

        if height_was_sent and next_height is not None and next_height != old_height:
            created_by = None if auth.get("type") == "api_key" else auth.get("sub")
            cur.execute("""
                INSERT INTO patient_height_history
                    (patient_id, household_id, height_inches, effective_date,
                     entry_type, source, created_by)
                VALUES (%s, %s, %s, current_date, 'measurement',
                        'legacy_profile_update', %s);
            """, (
                str(patient_id),
                household_id,
                next_height,
                created_by,
            ))

        if height_was_sent and next_height != old_height:
            cur.execute("""
                DELETE FROM vitals_analysis_cache
                WHERE patient_id = %s
                  AND vital_type = 'weight';
            """, (str(patient_id),))

        conn.commit()
        return {
            "patient_id": str(row[0]),
            "first_name": row[1],
            "last_name": row[2],
            "dob": row[3],
            "gender": row[4],
            "height_inches": row[5],
        }
    except HTTPException:
        conn.rollback()
        raise
    except Exception as e:
        conn.rollback()
        raise HTTPException(status_code=500, detail=f"Patient update error: {e}")
    finally:
        cur.close()
        conn.close()


@app.get(
    "/api/patients/{patient_id}/height-history",
    response_model=list[PatientHeightOut],
)
def get_patient_height_history(
    patient_id: UUID,
    household_id: str = Depends(get_household_id),
):
    """Returns the complete auditable height history, including superseded rows."""
    conn = get_conn()
    cur = conn.cursor()
    try:
        verify_patient_household(cur, str(patient_id), household_id)
        cur.execute("""
            SELECT
                height_id,
                patient_id,
                height_inches,
                effective_date,
                entry_type,
                source,
                supersedes_height_id,
                is_active,
                created_at
            FROM patient_height_history
            WHERE patient_id = %s
              AND household_id = %s
            ORDER BY effective_date DESC, created_at DESC;
        """, (str(patient_id), household_id))

        rows = cur.fetchall()
        return [
            {
                "height_id": str(r[0]),
                "patient_id": str(r[1]),
                "height_inches": r[2],
                "effective_date": r[3],
                "entry_type": r[4],
                "source": r[5],
                "supersedes_height_id": str(r[6]) if r[6] else None,
                "is_active": r[7],
                "created_at": r[8],
            }
            for r in rows
        ]
    finally:
        cur.close()
        conn.close()


@app.post(
    "/api/patients/{patient_id}/height",
    response_model=PatientHeightSaveResponse,
)
def record_patient_height(
    patient_id: UUID,
    body: PatientHeightCreate,
    household_id: str = Depends(get_household_id),
    auth: dict = Depends(get_auth),
):
    """
    Records a new dated height measurement or corrects the current/latest
    active height record. Corrections preserve the superseded row for audit.

    patients.height_inches remains a denormalized current-value cache and is
    synchronized to the most recent active height observation.
    """
    requested_date = body.effective_date or date.today()
    if requested_date > date.today():
        raise HTTPException(
            status_code=400,
            detail="Height effective date cannot be in the future",
        )

    conn = get_conn()
    cur = conn.cursor()
    try:
        verify_patient_household(cur, str(patient_id), household_id)
        created_by = None if auth.get("type") == "api_key" else auth.get("sub")

        supersedes_height_id = None
        effective_date = requested_date
        entry_type = "measurement"

        if body.update_type == "correction":
            cur.execute("""
                SELECT height_id, effective_date
                FROM patient_height_history
                WHERE patient_id = %s
                  AND household_id = %s
                  AND is_active = true
                ORDER BY effective_date DESC, created_at DESC
                LIMIT 1
                FOR UPDATE;
            """, (str(patient_id), household_id))
            prior = cur.fetchone()

            if prior is None:
                raise HTTPException(
                    status_code=409,
                    detail="There is no existing height record to correct",
                )

            supersedes_height_id = prior[0]
            if body.effective_date is None:
                effective_date = prior[1]
            entry_type = "correction"

            cur.execute("""
                UPDATE patient_height_history
                SET is_active = false
                WHERE height_id = %s;
            """, (supersedes_height_id,))

        cur.execute("""
            INSERT INTO patient_height_history
                (patient_id, household_id, height_inches, effective_date,
                 entry_type, source, supersedes_height_id, created_by)
            VALUES (%s, %s, %s, %s, %s, 'manual_profile', %s, %s)
            RETURNING
                height_id,
                patient_id,
                height_inches,
                effective_date,
                entry_type,
                source,
                supersedes_height_id,
                is_active,
                created_at;
        """, (
            str(patient_id),
            household_id,
            body.height_inches,
            effective_date,
            entry_type,
            supersedes_height_id,
            created_by,
        ))
        inserted = cur.fetchone()

        # Current profile height = most recent active observation as of today.
        cur.execute("""
            SELECT height_inches
            FROM patient_height_history
            WHERE patient_id = %s
              AND household_id = %s
              AND is_active = true
              AND effective_date <= current_date
            ORDER BY effective_date DESC, created_at DESC
            LIMIT 1;
        """, (str(patient_id), household_id))
        latest_active = cur.fetchone()
        current_height = latest_active[0] if latest_active else None

        cur.execute("""
            UPDATE patients
            SET height_inches = %s
            WHERE patient_id = %s
              AND household_id = %s;
        """, (current_height, str(patient_id), household_id))

        cur.execute("""
            DELETE FROM vitals_analysis_cache
            WHERE patient_id = %s
              AND vital_type = 'weight';
        """, (str(patient_id),))

        conn.commit()
        return {
            "record": {
                "height_id": str(inserted[0]),
                "patient_id": str(inserted[1]),
                "height_inches": inserted[2],
                "effective_date": inserted[3],
                "entry_type": inserted[4],
                "source": inserted[5],
                "supersedes_height_id": str(inserted[6]) if inserted[6] else None,
                "is_active": inserted[7],
                "created_at": inserted[8],
            },
            "current_height_inches": current_height,
        }
    except HTTPException:
        conn.rollback()
        raise
    except Exception as e:
        conn.rollback()
        raise HTTPException(status_code=500, detail=f"Height update error: {e}")
    finally:
        cur.close()
        conn.close()


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
        verify_patient_household(cur, patient_id, household_id)

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
    auth: dict = Depends(get_auth),
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

    # Optional-vital tracking is PATIENT-scoped. This persisted Settings
    # preference is the single source of truth for Dashboard, Entry, Analysis,
    # and the clinician PDF. Recorded data does not override visibility, and
    # client-local query flags do not override the server preference.
    #
    # Blood pressure remains always enabled and therefore has no preference
    # column here.
    cur.execute("""
        SELECT show_heart_rate, show_spo2, show_temperature,
               show_weight, show_glucose
        FROM patients
        WHERE patient_id = %s
          AND household_id = %s;
    """, (str(patient_id), household_id))
    pref_row = cur.fetchone()

    # verify_patient_household() above guarantees the patient row exists.
    # Keep a defensive failure here rather than silently inventing defaults
    # that could expose a vital the user did not select in Settings.
    if pref_row is None:
        raise HTTPException(
            status_code=404,
            detail="Patient preferences not found",
        )

    show_hr, show_spo2, show_temp, show_weight, show_glucose = [
        bool(value) for value in pref_row
    ]

    tracked_conditions = [
        "(systolic IS NOT NULL AND diastolic IS NOT NULL)"
    ]
    if show_hr:
        tracked_conditions.append("heart_rate IS NOT NULL")
    if show_spo2:
        tracked_conditions.append("oxygen_saturation IS NOT NULL")
    if show_temp:
        tracked_conditions.append("temperature IS NOT NULL")
    if show_weight:
        tracked_conditions.append("weight IS NOT NULL")
    if show_glucose:
        tracked_conditions.append("blood_glucose IS NOT NULL")
    tracked_where = " OR ".join(tracked_conditions)

    cur.execute("""
        SELECT first_name, last_name, dob
        FROM patients WHERE patient_id = %s AND household_id = %s;
    """, (str(patient_id), household_id))
    p = cur.fetchone()
    patient_name = f"{p[0]} {p[1]}" if p else "Unknown Patient"
    patient_dob  = p[2].strftime("%m/%d/%Y") if p and p[2] else "Unknown DOB"

    cur.execute(f"""
        SELECT
            (SELECT recorded_at FROM vitals
             WHERE patient_id = %s AND household_id = %s
               AND ({tracked_where})
             ORDER BY recorded_at DESC LIMIT 1),
            (SELECT systolic FROM vitals
             WHERE patient_id = %s AND household_id = %s
               AND systolic IS NOT NULL AND diastolic IS NOT NULL
             ORDER BY recorded_at DESC LIMIT 1),
            (SELECT diastolic FROM vitals
             WHERE patient_id = %s AND household_id = %s
               AND systolic IS NOT NULL AND diastolic IS NOT NULL
             ORDER BY recorded_at DESC LIMIT 1),
            (SELECT heart_rate FROM vitals
             WHERE patient_id = %s AND household_id = %s
               AND heart_rate IS NOT NULL
             ORDER BY recorded_at DESC LIMIT 1),
            (SELECT oxygen_saturation FROM vitals
             WHERE patient_id = %s AND household_id = %s
               AND oxygen_saturation IS NOT NULL
             ORDER BY recorded_at DESC LIMIT 1),
            (SELECT temperature FROM vitals
             WHERE patient_id = %s AND household_id = %s
               AND temperature IS NOT NULL
             ORDER BY recorded_at DESC LIMIT 1),
            (SELECT weight FROM vitals
             WHERE patient_id = %s AND household_id = %s
               AND weight IS NOT NULL
             ORDER BY recorded_at DESC LIMIT 1),
            (SELECT blood_glucose FROM vitals
             WHERE patient_id = %s AND household_id = %s
               AND blood_glucose IS NOT NULL
             ORDER BY recorded_at DESC LIMIT 1);
    """, (
        str(patient_id), household_id,
        str(patient_id), household_id,
        str(patient_id), household_id,
        str(patient_id), household_id,
        str(patient_id), household_id,
        str(patient_id), household_id,
        str(patient_id), household_id,
        str(patient_id), household_id,
    ))
    latest = cur.fetchone()

    cur.execute(f"""
        SELECT recorded_at, systolic, diastolic, heart_rate, oxygen_saturation,
               temperature, weight, blood_glucose
        FROM vitals WHERE patient_id = %s AND household_id = %s
          AND recorded_at >= now() - interval '%s days'
          AND ({tracked_where})
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

    cur.execute(f"""
        SELECT recorded_at, systolic, diastolic, heart_rate, oxygen_saturation,
               temperature, weight, blood_glucose
        FROM vitals WHERE patient_id = %s AND household_id = %s
          AND recorded_at >= now() - interval '%s days'
          AND ({tracked_where})
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

    # Heart Rate, SpO2, and Temperature all use the same dedicated
    # analysis engines that power the app's Analysis view. Keeping the
    # PDF on those contracts prevents the report from drifting back to
    # the retired generic average/classification/interpolated-burden path.
    hr_analysis = (
        get_cached_or_compute_analysis(str(patient_id), household_id, "heart_rate", days)
        if show_hr else None
    )
    spo2_analysis = (
        get_cached_or_compute_analysis(str(patient_id), household_id, "spo2", days)
        if show_spo2 else None
    )
    temp_analysis = (
        get_cached_or_compute_analysis(str(patient_id), household_id, "temperature", days)
        if show_temp else None
    )
    weight_analysis = (
        get_weight_analysis_for_window(str(patient_id), household_id, days)
        if show_weight else None
    )
    glucose_analysis = (
        get_cached_or_compute_analysis(str(patient_id), household_id, "glucose", days)
        if show_glucose else None
    )

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

    def build_spo2_clinical_summary(spo2) -> list:
        """
        Mirrors build_hr_clinical_summary(hr) in depth and style, using
        SpO2's own data shape (run_spo2_analysis via the cache — the
        same engine powering the app's Analysis tab). Deliberately
        NOT modeled on BP's AUC/duration-weighted burden methodology
        for low-observation counts or reference bands — spot SpO2
        readings don't support a continuous-coverage assumption, so
        those are framed explicitly as reading counts, never "time
        spent" or "time below target."

        Trend consistency (R²) is only interpreted when the trend is
        actually rising or falling — a low R² against an otherwise
        flat, stable series isn't a data-quality concern, it just means
        a straight-line model explains little of essentially-no
        variation. Same reasoning the app's own summary applies.
        """
        if spo2 is None:
            return []

        paragraphs = []

        ss = spo2.get("spot_summary")
        if ss:
            paragraphs.append(
                f"Across {ss['n']} logged readings on {ss['distinct_days']} days, oxygen "
                f"saturation averaged {ss['mean']:.1f}%, with a typical value near "
                f"{ss['median']:.1f}% and a recorded range of {ss['min']}\u2013{ss['max']}%."
            )
        else:
            bpm_val = spo2.get('spo2')
            paragraphs.append(
                f"Most recent oxygen saturation reading: {bpm_val}%. Insufficient logged "
                f"readings for trend analysis at this time (requires at least 3 readings "
                f"across 3 distinct days)."
            )
            return paragraphs

        disp = spo2.get("dispersion")
        if disp:
            paragraphs.append(
                f"Reading-to-reading variability: SD {disp['sd']:.1f} points, "
                f"IQR {disp['iqr']:.1f} points (Q1 {disp['q1']:.1f}, Q3 {disp['q3']:.1f})."
            )

        trend = spo2.get("trend")
        if trend:
            sig_note = ""
            if trend.get("p_value") is not None:
                sig_note = (
                    f", statistically significant (p={trend['p_value']:.3f})"
                    if trend["p_value"] < 0.05 else
                    f", not statistically significant (p={trend['p_value']:.3f})"
                )
            paragraphs.append(
                f"Trend: {trend['trend_label'].replace('_', ' ')} at "
                f"{trend['slope_pct_points_per_day']:+.2f} points/day over a "
                f"{trend['span_days']:.0f}-day span{sig_note}."
            )
            # Only interpret consistency when the trend is actually rising
            # or falling — see docstring.
            if trend.get("trend_label") != "stable" and trend.get("r2") is not None:
                paragraphs.append(
                    f"Trend fit: R\u00b2={trend['r2']:.2f} "
                    f"({trend.get('consistency') or 'n/a'} consistency)."
                )

        bd = spo2.get("baseline_deviation")
        if bd:
            direction = "higher" if bd["delta_pct_points"] >= 0 else "lower"
            paragraphs.append(
                f"The most recent 7 days (median {bd['recent_median']:.1f}%) are "
                f"{direction} than the prior 30-day baseline (median "
                f"{bd['baseline_median']:.1f}%) by {abs(bd['delta_pct_points']):.1f} points."
            )

        low = spo2.get("low_observations")
        if low and low.get("count", 0) > 0:
            pct_note = f" ({low['pct_of_logged_readings']:.0f}% of logged readings)" if low.get("pct_of_logged_readings") is not None else ""
            paragraphs.append(
                f"{low['count']} logged reading(s) were below {low['threshold']}%{pct_note}. "
                "These are reading counts, not a measure of continuous time below target."
            )

            confirmed = spo2.get("confirmed_low_observations")
            if confirmed and confirmed.get("episode_count", 0) > 0:
                paragraphs.append(
                    f"{confirmed['episode_count']} of these were repeat-confirmed episodes "
                    f"\u2014 at least two below-target readings within "
                    f"{confirmed['confirmation_rule_minutes']} minutes of each other."
                )

            marked = spo2.get("marked_low_observations")
            if marked and marked.get("count", 0) > 0:
                paragraphs.append(
                    f"{marked['count']} reading(s) were below {marked['threshold']}% "
                    "\u2014 symptoms occurring near these readings are important context "
                    "for the care team."
                )

        tod = spo2.get("time_of_day")
        if tod and tod.get("pattern_summary"):
            ps = tod["pattern_summary"]
            paragraphs.append(
                f"Time-of-day pattern: readings tend to be highest during "
                f"{ps['highest_period']} and lowest during {ps['lowest_period']}, "
                f"a difference of about {ps['median_delta']:.1f} points."
            )

        corr = spo2.get("cross_vital_correlations")
        if corr:
            corr_parts = [f"{k.replace('_', ' ')} (r={v:.2f})" for k, v in corr.items()]
            if corr_parts:
                paragraphs.append(
                    "Correlation with other same-window vitals (physician reference only, "
                    "does not establish cause): " + ", ".join(corr_parts) + "."
                )

        ds = spo2.get("data_support")
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

    def build_temperature_clinical_summary(temp) -> list:
        """
        Episode-centric, measurement-site-aware Temperature summary for
        the clinician PDF. Mirrors run_temperature_analysis rather than
        the retired generic temperature classifier. Sparse spot readings
        are described as logged observations; no continuous fever duration
        or time-in-range burden is inferred.
        """
        if temp is None:
            return []

        paragraphs = []
        latest = temp.get("latest") or {}
        if latest:
            site = (latest.get("site") or "unknown").replace("_", " ").title()
            source = (latest.get("source") or "unknown").replace("_", " ")
            paragraphs.append(
                f"Latest temperature: {latest.get('value_f', 0):.1f} F "
                f"({latest.get('value_c', 0):.1f} C), site: {site}, "
                f"source: {source}."
            )

        baseline = temp.get("baseline")
        if baseline:
            delta = baseline.get("delta_current_f", 0.0)
            direction = "above" if delta > 0 else "below" if delta < 0 else "at"
            delta_text = (
                f"{abs(delta):.1f} F {direction}" if direction != "at"
                else "approximately at"
            )
            paragraphs.append(
                f"Same-site personal baseline ({baseline.get('site', 'unknown')}): median "
                f"{baseline.get('median_f', 0):.1f} F across {baseline.get('n', 0)} readings "
                f"on {baseline.get('distinct_days', 0)} distinct days spanning "
                f"{baseline.get('span_days', 0):.1f} days. The latest reading is "
                f"{delta_text} that baseline."
            )

        ranges = temp.get("range_events") or {}
        fever_count = ranges.get("fever_count", 0) or 0
        if fever_count > 0:
            paragraphs.append(
                f"Fever-range observations: {fever_count} logged reading(s) at or above "
                f"{ranges.get('fever_threshold_f', 100.4):.1f} F "
                f"({ranges.get('fever_logged_pct', 0):.1f}% of logged temperature readings), "
                f"recorded on {ranges.get('febrile_days', 0)} distinct day(s). This is a count "
                f"of logged observations, not an estimate of continuous time with fever."
            )
        else:
            paragraphs.append(
                f"No logged temperature readings met the configured "
                f"{ranges.get('fever_threshold_f', 100.4):.1f} F fever-range reference "
                f"during this report window."
            )

        episodes = temp.get("episodes") or []
        if episodes:
            latest_ep = temp.get("latest_episode") or episodes[-1]
            paragraphs.append(
                f"The fever-range readings group into {len(episodes)} recorded episode(s). "
                f"The latest episode peaked at {latest_ep.get('peak_f', 0):.1f} F and had an "
                f"observed fever-range span of {latest_ep.get('observed_span_hours', 0):.1f} "
                f"hours. 'Observed span' describes the interval between logged fever-range "
                f"measurements and does not establish continuous fever duration."
            )
            if latest_ep.get("delta_latest_from_peak_f") is not None:
                d = latest_ep["delta_latest_from_peak_f"]
                if d < 0:
                    paragraphs.append(
                        f"The latest comparable same-site reading is {abs(d):.1f} F below the "
                        f"recorded episode peak."
                    )
                elif d > 0:
                    paragraphs.append(
                        f"The latest comparable same-site reading is {d:.1f} F above the "
                        f"previously recorded episode peak."
                    )
                else:
                    paragraphs.append(
                        "The latest comparable same-site reading matches the recorded episode peak."
                    )

        acute = temp.get("acute_trend")
        if acute:
            paragraphs.append(
                f"Acute same-site trajectory: {acute.get('trend_label', 'stable')} across "
                f"{acute.get('span_hours', 0):.1f} hours ({acute.get('n', 0)} readings), "
                f"modeled at {acute.get('slope_f_per_12_hours', 0):+.2f} F per 12 hours. "
                f"This is a short-window episode model, not a long-term temperature trend."
            )

        hypo_count = ranges.get("hypothermia_range_count", 0) or 0
        if hypo_count > 0:
            paragraphs.append(
                f"Low-temperature safety context: {hypo_count} reading(s) were below the "
                f"{ranges.get('hypothermia_threshold_f', 95.0):.1f} F hypothermia-range "
                f"reference; the lowest logged value was {ranges.get('lowest_f', 0):.1f} F."
            )
        elif ranges:
            paragraphs.append(
                f"Lowest logged temperature: {ranges.get('lowest_f', 0):.1f} F; no readings "
                f"were below the {ranges.get('hypothermia_threshold_f', 95.0):.1f} F "
                f"hypothermia-range reference."
            )

        cvc = temp.get("cross_vital_context") or {}
        paired = cvc.get("paired_counts") or {}
        paired_parts = []
        if paired.get("heart_rate", 0):
            paired_parts.append(f"heart rate with {paired['heart_rate']}")
        if paired.get("oxygen_saturation", 0):
            paired_parts.append(f"SpO2 with {paired['oxygen_saturation']}")
        if paired.get("blood_pressure", 0):
            paired_parts.append(f"blood pressure with {paired['blood_pressure']}")
        if paired_parts:
            paragraphs.append(
                "Same-event context: " + "; ".join(paired_parts) +
                " fever-range observation(s). These measurements occurred alongside one "
                "another and do not establish causation."
            )

        ds = temp.get("data_support") or {}
        if ds:
            site_note = (
                f", known measurement site on {ds.get('known_site_pct', 0):.1f}% of readings"
            )
            if ds.get("site_consistency_pct") is not None:
                site_note += f", {ds['site_consistency_pct']:.1f}% same-site consistency"
            paragraphs.append(
                f"Data confidence: {ds.get('support_state', 'descriptive').replace('_', ' ')} "
                f"({ds.get('n', 0)} readings across {ds.get('distinct_days', 0)} days, "
                f"{ds.get('span_days', 0):.1f}-day span{site_note})."
            )

        return paragraphs

    def draw_weight_clinical_analysis(y, analysis):
        """
        Clinician-facing Weight section built directly from the SAME Weight
        analysis contract used by Vitals Analysis. Do not fall back to the
        older generic scalar helper here: Weight v6 has current/historical BMI
        context, daily-median summaries, robust baseline/recent comparisons,
        variability, and a Theil-Sen trend that need dedicated rendering.
        """
        if analysis is None:
            y = check_page_break(y, needed=105)
            pdf.setFont("Helvetica-Bold", 13)
            pdf.drawString(LEFT, y, "Weight Clinical Analysis")
            y -= 20
            pdf.setFont("Helvetica-Bold", 10)
            pdf.drawString(LEFT, y, "No Weight Data Yet")
            y -= 14
            y = draw_wrapped_line(
                y,
                "Weight tracking is enabled for this patient, but no weight reading "
                "has been recorded yet. One reading is enough to establish a current "
                "Weight snapshot and, when height/date-of-birth permit it, adult BMI "
                "screening context.",
                fontsize=9,
                indent=10,
                line_spacing=12,
            )
            y -= 10
            pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
            pdf.line(LEFT, y, RIGHT, y)
            pdf.setStrokeColorRGB(0, 0, 0)
            return y - 14

        unit_w = analysis.get("unit") or "lb"
        latest_w = analysis.get("latest") or {}
        anthropometrics = analysis.get("anthropometrics") or {}
        historical_bmi_w = analysis.get("historical_bmi") or {}
        summary_w = analysis.get("daily_summary") or analysis.get("summary") or {}
        baseline_w = analysis.get("baseline_change")
        recent_w = analysis.get("recent_change")
        variation_w = analysis.get("variation")
        trend_w = analysis.get("trend")
        support_w = analysis.get("data_support") or {}
        limitations_w = analysis.get("limitations") or []

        y = check_page_break(y, needed=210)
        pdf.setFont("Helvetica-Bold", 13)
        pdf.drawString(LEFT, y, "Weight Clinical Analysis")
        y -= 20

        # -------------------------------------------------
        # CLINICAL SUMMARY
        # -------------------------------------------------
        pdf.setFont("Helvetica-Bold", 11)
        pdf.drawString(LEFT, y, "Clinical Summary")
        y -= 4
        pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
        pdf.line(LEFT, y, RIGHT, y)
        pdf.setStrokeColorRGB(0, 0, 0)
        y -= 12

        latest_value = latest_w.get("value")
        if latest_value is not None:
            summary_line = f"Latest recorded weight: {latest_value:.1f} {unit_w}."
            if anthropometrics.get("bmi_available") and anthropometrics.get("bmi") is not None:
                category_display = {
                    "underweight": "Underweight",
                    "healthy_weight": "Healthy weight",
                    "overweight": "Overweight",
                    "obesity_class_1": "Obesity - Class 1",
                    "obesity_class_2": "Obesity - Class 2",
                    "obesity_class_3": "Obesity - Class 3",
                }.get(
                    anthropometrics.get("adult_category"),
                    str(anthropometrics.get("adult_category") or "").replace("_", " ").title(),
                )
                summary_line += (
                    f" Adult BMI: {anthropometrics['bmi']:.1f} kg/m2 "
                    f"({category_display} screening category)."
                )
            y = draw_wrapped_line(
                y,
                summary_line,
                fontsize=9,
                indent=0,
                line_spacing=13,
            )
            y -= 4

        if trend_w:
            direction_display = {
                "increasing": "increasing",
                "decreasing": "decreasing",
                "no_clear_trend": "no clear directional",
            }.get(trend_w.get("direction"), "descriptive")
            y = draw_wrapped_line(
                y,
                f"Robust longitudinal pattern: {direction_display}; "
                f"Theil-Sen slope {trend_w.get('slope_per_week', 0):+.2f} {unit_w}/week "
                f"with 95% slope interval "
                f"{trend_w.get('ci95_low_per_week', 0):+.2f} to "
                f"{trend_w.get('ci95_high_per_week', 0):+.2f} {unit_w}/week.",
                fontsize=9,
                indent=0,
                line_spacing=13,
            )
            y -= 4
        elif support_w:
            y = draw_wrapped_line(
                y,
                f"Data support: {(support_w.get('support_state') or 'snapshot').replace('_', ' ')} "
                f"with {support_w.get('n', 0)} logged reading(s) across "
                f"{support_w.get('distinct_days', 0)} distinct day(s).",
                fontsize=9,
                indent=0,
                line_spacing=13,
            )
            y -= 4

        pdf.setFont("Helvetica-Oblique", 8)
        pdf.setFillColorRGB(0.4, 0.4, 0.4)
        y = draw_wrapped_line(
            y,
            "BMI is screening context only. Weight changes are described without "
            "judging whether gain or loss is desirable unless individualized goals "
            "and clinical context are available.",
            fontsize=8,
            indent=0,
            line_spacing=11,
        )
        pdf.setFillColorRGB(0, 0, 0)
        y -= 8

        # -------------------------------------------------
        # CURRENT WEIGHT
        # -------------------------------------------------
        y = check_page_break(y, needed=65)
        pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
        pdf.line(LEFT, y, RIGHT, y)
        pdf.setStrokeColorRGB(0, 0, 0)
        y -= 14
        pdf.setFont("Helvetica-Bold", 10)
        pdf.drawString(LEFT, y, "Current Weight")
        y -= 14
        pdf.setFont("Helvetica", 9)
        if latest_value is not None:
            pdf.drawString(LEFT + 10, y, f"{latest_value:.1f} {unit_w}")
        else:
            pdf.drawString(LEFT + 10, y, "n/a")
        y -= 12
        if latest_w.get("recorded_at"):
            y = draw_wrapped_line(
                y,
                f"Recorded: {latest_w['recorded_at']}",
                fontsize=9,
                indent=10,
                line_spacing=12,
            )
        y -= 6

        # -------------------------------------------------
        # BMI SCREENING CONTEXT
        # -------------------------------------------------
        y = check_page_break(y, needed=95)
        pdf.setFont("Helvetica-Bold", 10)
        pdf.drawString(LEFT, y, "BMI Screening Context")
        y -= 14

        if anthropometrics.get("bmi_available") and anthropometrics.get("bmi") is not None:
            category_display = {
                "underweight": "Underweight",
                "healthy_weight": "Healthy weight",
                "overweight": "Overweight",
                "obesity_class_1": "Obesity - Class 1",
                "obesity_class_2": "Obesity - Class 2",
                "obesity_class_3": "Obesity - Class 3",
            }.get(
                anthropometrics.get("adult_category"),
                str(anthropometrics.get("adult_category") or "").replace("_", " ").title(),
            )

            bmi_rows = [
                ["BMI", f"{anthropometrics['bmi']:.1f} kg/m2"],
                ["Adult screening category", category_display],
            ]

            total_inches = anthropometrics.get("height_inches")
            if total_inches is not None:
                total_inches = int(total_inches)
                bmi_rows.append([
                    "Current profile height used",
                    f"{total_inches // 12} ft {total_inches % 12} in",
                ])
            if anthropometrics.get("height_measured_at"):
                bmi_rows.append([
                    "Height effective date",
                    str(anthropometrics["height_measured_at"]),
                ])
            if anthropometrics.get("age_years") is not None:
                bmi_rows.append([
                    "Age on latest weight date",
                    str(anthropometrics["age_years"]),
                ])

            bmi_widths = [210, 302]
            y = draw_table_row(
                y,
                ["Metric", "Value"],
                bmi_widths,
                fontsize=8,
                bold=True,
                fill_bg=True,
            )
            for row in bmi_rows:
                y = check_page_break(y, needed=32)
                y = draw_table_row(y, row, bmi_widths, fontsize=8)

            y -= 6
            pdf.setFont("Helvetica-Oblique", 8)
            pdf.setFillColorRGB(0.4, 0.4, 0.4)
            y = draw_wrapped_line(
                y,
                "BMI is a screening measure, not a diagnosis or a direct measure of body fat.",
                fontsize=8,
                indent=10,
                line_spacing=11,
            )
            pdf.setFillColorRGB(0, 0, 0)
            y -= 6
        else:
            reason = {
                "missing_height": "Current height is not available.",
                "missing_date_of_birth": "Date of birth is not available for adult age gating.",
                "pediatric_strategy_required": (
                    "Adult BMI categories are not applied to patients under age 20; "
                    "pediatric BMI-for-age requires a separate strategy."
                ),
                "invalid_height": "The stored height is not usable for BMI calculation.",
            }.get(
                anthropometrics.get("reason_unavailable"),
                "Adult BMI is unavailable for the current patient profile.",
            )
            pdf.setFont("Helvetica", 9)
            y = draw_wrapped_line(
                y,
                f"BMI unavailable: {reason}",
                fontsize=9,
                indent=10,
                line_spacing=12,
            )
            y -= 6

        # -------------------------------------------------
        # HISTORICAL BMI TRAJECTORY
        # -------------------------------------------------
        if historical_bmi_w:
            y = check_page_break(y, needed=95)
            pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
            pdf.line(LEFT, y, RIGHT, y)
            pdf.setStrokeColorRGB(0, 0, 0)
            y -= 14
            pdf.setFont("Helvetica-Bold", 10)
            pdf.drawString(LEFT, y, "Historical BMI Trajectory")
            y -= 14

            if historical_bmi_w.get("available"):
                point_count = int(historical_bmi_w.get("point_count") or 0)
                first_bmi = historical_bmi_w.get("first_bmi")
                latest_bmi_hist = historical_bmi_w.get("latest_bmi")
                bmi_change = historical_bmi_w.get("absolute_change")
                span_days_hist = historical_bmi_w.get("span_days") or 0

                if point_count == 1:
                    single_bmi = (
                        latest_bmi_hist
                        if latest_bmi_hist is not None
                        else first_bmi
                    )
                    summary_text = "1 historically valid adult BMI point."
                    if single_bmi is not None:
                        summary_text += f" BMI {single_bmi:.1f} kg/m2."
                else:
                    summary_text = (
                        f"{point_count} historically valid adult BMI points across "
                        f"{span_days_hist:.0f} day(s)."
                    )
                    if first_bmi is not None and latest_bmi_hist is not None:
                        summary_text += (
                            f" First BMI {first_bmi:.1f}; latest BMI "
                            f"{latest_bmi_hist:.1f} kg/m2."
                        )
                    if bmi_change is not None:
                        summary_text += (
                            f" Change across valid points: {bmi_change:+.1f} kg/m2."
                        )

                y = draw_wrapped_line(
                    y,
                    summary_text,
                    fontsize=9,
                    indent=10,
                    line_spacing=12,
                )
                y -= 4

                pdf.setFont("Helvetica-Oblique", 8)
                pdf.setFillColorRGB(0.4, 0.4, 0.4)
                y = draw_wrapped_line(
                    y,
                    "Each point uses that day's median Weight and the newest active "
                    "height observation effective on or before that Weight date. "
                    "Later heights are not backfilled into earlier dates.",
                    fontsize=8,
                    indent=10,
                    line_spacing=11,
                )
                pdf.setFillColorRGB(0, 0, 0)
                y -= 6

                hist_widths = [100, 95, 205, 112]
                y = draw_table_row(
                    y,
                    ["Date", "Weight", "Height used", "BMI"],
                    hist_widths,
                    fontsize=8,
                    bold=True,
                    fill_bg=True,
                )
                for point in historical_bmi_w.get("points") or []:
                    y = check_page_break(y, needed=32)
                    total_inches = int(point.get("height_inches") or 0)
                    height_text = (
                        f"{total_inches // 12} ft {total_inches % 12} in"
                        if total_inches > 0
                        else "n/a"
                    )
                    if point.get("height_effective_date"):
                        height_text += f" (eff {point['height_effective_date']})"

                    y = draw_table_row(
                        y,
                        [
                            str(point.get("local_date") or "n/a"),
                            f"{point.get('weight_lb', 0):.1f} {unit_w}",
                            height_text,
                            f"{point.get('bmi', 0):.1f} kg/m2",
                        ],
                        hist_widths,
                        fontsize=8,
                    )
                y -= 7
            else:
                reason = historical_bmi_w.get("reason") or (
                    "Historical BMI is unavailable for the selected Weight dates."
                )
                y = draw_wrapped_line(
                    y,
                    f"Historical BMI unavailable: {reason}",
                    fontsize=9,
                    indent=10,
                    line_spacing=12,
                )
                y -= 7

        # -------------------------------------------------
        # LOGGED READING SUMMARY (same daily-median view as the app)
        # -------------------------------------------------
        if summary_w:
            y = check_page_break(y, needed=135)
            pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
            pdf.line(LEFT, y, RIGHT, y)
            pdf.setStrokeColorRGB(0, 0, 0)
            y -= 14
            pdf.setFont("Helvetica-Bold", 10)
            pdf.drawString(LEFT, y, "Logged Reading Summary")
            y -= 14
            pdf.setFont("Helvetica-Oblique", 8)
            pdf.setFillColorRGB(0.4, 0.4, 0.4)
            pdf.drawString(
                LEFT + 10,
                y,
                "Daily median values" if analysis.get("daily_summary") else "Logged reading values",
            )
            pdf.setFillColorRGB(0, 0, 0)
            y -= 12

            summary_widths = [210, 302]
            y = draw_table_row(
                y,
                ["Metric", "Weight"],
                summary_widths,
                fontsize=8,
                bold=True,
                fill_bg=True,
            )
            summary_rows = [
                ["Mean", f"{summary_w.get('mean', 0):.1f} {unit_w}"],
                ["Median", f"{summary_w.get('median', 0):.1f} {unit_w}"],
                ["Minimum", f"{summary_w.get('min', 0):.1f} {unit_w}"],
                ["Maximum", f"{summary_w.get('max', 0):.1f} {unit_w}"],
            ]
            for row in summary_rows:
                y = check_page_break(y, needed=32)
                y = draw_table_row(y, row, summary_widths, fontsize=8)
            y -= 8

        # -------------------------------------------------
        # BASELINE CHANGE
        # -------------------------------------------------
        if baseline_w:
            y = check_page_break(y, needed=75)
            pdf.setFont("Helvetica-Bold", 10)
            pdf.drawString(LEFT, y, "Baseline Change")
            y -= 14
            pct = (
                f" ({baseline_w['pct_change']:+.1f}%)"
                if baseline_w.get("pct_change") is not None
                else ""
            )
            y = draw_wrapped_line(
                y,
                f"Baseline median: {baseline_w.get('baseline_value', 0):.1f} {unit_w} "
                f"using {baseline_w.get('baseline_days_used', 0)} distinct day(s) "
                f"({baseline_w.get('baseline_start_date', 'n/a')} to "
                f"{baseline_w.get('baseline_end_date', 'n/a')}). "
                f"Latest daily median: {baseline_w.get('latest_daily_value', 0):.1f} {unit_w}. "
                f"Change: {baseline_w.get('absolute_change', 0):+.1f} {unit_w}{pct}.",
                fontsize=9,
                indent=10,
                line_spacing=12,
            )
            y -= 7

        # -------------------------------------------------
        # RECENT 7-DAY COMPARISON
        # -------------------------------------------------
        if recent_w:
            y = check_page_break(y, needed=70)
            pdf.setFont("Helvetica-Bold", 10)
            pdf.drawString(LEFT, y, "Recent 7-Day Comparison")
            y -= 14
            pct = (
                f" ({recent_w['pct_change']:+.1f}%)"
                if recent_w.get("pct_change") is not None
                else ""
            )
            y = draw_wrapped_line(
                y,
                f"Recent 7-day median: {recent_w.get('recent_median', 0):.1f} {unit_w} "
                f"({recent_w.get('recent_days_with_readings', 0)} measured day(s)); "
                f"previous 7-day median: {recent_w.get('prior_median', 0):.1f} {unit_w} "
                f"({recent_w.get('prior_days_with_readings', 0)} measured day(s)). "
                f"Change: {recent_w.get('absolute_change', 0):+.1f} {unit_w}{pct}.",
                fontsize=9,
                indent=10,
                line_spacing=12,
            )
            y -= 7

        # -------------------------------------------------
        # LONGITUDINAL TREND
        # -------------------------------------------------
        if trend_w:
            y = check_page_break(y, needed=95)
            pdf.setFont("Helvetica-Bold", 10)
            pdf.drawString(LEFT, y, "Longitudinal Trend")
            y -= 14

            direction_display = {
                "increasing": "Increasing pattern",
                "decreasing": "Decreasing pattern",
                "no_clear_trend": "No clear directional trend",
            }.get(trend_w.get("direction"), "Descriptive trend")

            y = draw_wrapped_line(
                y,
                f"{direction_display}: Theil-Sen slope "
                f"{trend_w.get('slope_per_week', 0):+.2f} {unit_w}/week "
                f"(95% slope interval "
                f"{trend_w.get('ci95_low_per_week', 0):+.2f} to "
                f"{trend_w.get('ci95_high_per_week', 0):+.2f} {unit_w}/week) "
                f"across {trend_w.get('n', 0)} distinct measurement day(s) and "
                f"{trend_w.get('span_days', 0):.1f} calendar days.",
                fontsize=9,
                indent=10,
                line_spacing=12,
            )
            if trend_w.get("r2") is not None or trend_w.get("p_value") is not None:
                y = draw_wrapped_line(
                    y,
                    f"Secondary OLS fit diagnostics: "
                    f"R2={trend_w.get('r2', 0):.2f}, "
                    f"p={trend_w.get('p_value', 0):.3f}. "
                    f"Trend direction is determined from the Theil-Sen slope interval, "
                    f"not from the OLS p-value.",
                    fontsize=8,
                    indent=10,
                    line_spacing=11,
                )
            y -= 7

        # -------------------------------------------------
        # DAILY VARIABILITY
        # -------------------------------------------------
        if variation_w:
            y = check_page_break(y, needed=105)
            pdf.setFont("Helvetica-Bold", 10)
            pdf.drawString(LEFT, y, "Daily Variability")
            y -= 14
            var_widths = [210, 302]
            y = draw_table_row(
                y,
                ["Metric", "Value"],
                var_widths,
                fontsize=8,
                bold=True,
                fill_bg=True,
            )
            var_rows = [
                ["Standard deviation", f"{variation_w.get('sd', 0):.1f} {unit_w}"],
                ["Interquartile range", f"{variation_w.get('iqr', 0):.1f} {unit_w}"],
                ["Median absolute deviation", f"{variation_w.get('mad', 0):.1f} {unit_w}"],
            ]
            for row in var_rows:
                y = check_page_break(y, needed=32)
                y = draw_table_row(y, row, var_widths, fontsize=8)
            y -= 8

        # -------------------------------------------------
        # DATA SUPPORT / CONFIDENCE
        # -------------------------------------------------
        if support_w:
            y = check_page_break(y, needed=90)
            pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
            pdf.line(LEFT, y, RIGHT, y)
            pdf.setStrokeColorRGB(0, 0, 0)
            y -= 14
            pdf.setFont("Helvetica-Bold", 10)
            pdf.drawString(LEFT, y, "Data Support")
            y -= 14
            y = draw_wrapped_line(
                y,
                f"State: {(support_w.get('support_state') or 'snapshot').replace('_', ' ')}. "
                f"{support_w.get('n', 0)} logged reading(s), "
                f"{support_w.get('distinct_days', 0)} distinct measurement day(s), "
                f"{support_w.get('span_days', 0):.1f}-day raw span, "
                f"{support_w.get('daily_span_days', 0):.1f}-day daily-median span.",
                fontsize=9,
                indent=10,
                line_spacing=12,
            )

            for item in support_w.get("unavailable_analyses") or []:
                y = check_page_break(y, needed=32)
                name = (item.get("analysis") or "analysis").replace("_", " ").title()
                reason = item.get("reason") or item.get("reason_code") or "not available"
                y = draw_wrapped_line(
                    y,
                    f"- {name}: {reason}",
                    fontsize=8,
                    indent=10,
                    line_spacing=11,
                )
            y -= 8

        # -------------------------------------------------
        # INTERPRETATION LIMITS
        # -------------------------------------------------
        if limitations_w:
            y = check_page_break(y, needed=70)
            pdf.setFont("Helvetica-Bold", 10)
            pdf.drawString(LEFT, y, "Interpretation Limits")
            y -= 14
            for limitation in limitations_w:
                y = check_page_break(y, needed=30)
                y = draw_wrapped_line(
                    y,
                    f"- {limitation}",
                    fontsize=8,
                    indent=10,
                    line_spacing=11,
                )
            y -= 8

        pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
        pdf.line(LEFT, y, RIGHT, y)
        pdf.setStrokeColorRGB(0, 0, 0)
        y -= 14
        return y


    def draw_glucose_clinical_analysis(y, analysis, window_days):
        """
        Clinician-facing Glucose section rendered from the same dedicated
        Glucose analysis contract used by the MAUI Analysis view.

        Mixed manual/BGM readings are summarized descriptively, but clinical
        trends are shown only inside one recorded measurement context. CGM-only
        metrics remain capability-gated. The PDF keeps GMI visible as a
        readiness item even before qualified CGM data exists, matching the
        product decision used by the app while preserving the required
        disclosure that GMI is not a laboratory A1C.
        """
        if analysis is None:
            y = check_page_break(y, needed=105)
            pdf.setFont("Helvetica-Bold", 13)
            pdf.drawString(LEFT, y, "Blood Glucose Clinical Analysis")
            y -= 20
            pdf.setFont("Helvetica-Bold", 10)
            pdf.drawString(LEFT, y, "No Glucose Data Yet")
            y -= 14
            y = draw_wrapped_line(
                y,
                "Blood glucose tracking is enabled for this patient, but no glucose "
                "reading is available in the selected report window.",
                fontsize=9,
                indent=10,
                line_spacing=12,
            )
            y -= 10
            pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
            pdf.line(LEFT, y, RIGHT, y)
            pdf.setStrokeColorRGB(0, 0, 0)
            return y - 14

        unit_g = analysis.get("unit") or "mg/dL"
        latest_g = analysis.get("latest") or {}
        summary_g = analysis.get("summary") or {}
        source_g = analysis.get("source_summary") or {}
        context_g = analysis.get("context_summary") or {}
        context_summaries_g = analysis.get("context_summaries") or {}
        context_trends_g = analysis.get("context_trends") or {}
        low_g = analysis.get("low_events") or {}
        meal_g = analysis.get("meal_excursions") or {}
        medication_g = analysis.get("medication_correlations") or []
        cgm_g = analysis.get("cgm_summary") or {}
        gmi_g = analysis.get("gmi") or {}
        support_g = analysis.get("data_support") or {}
        limitations_g = analysis.get("limitations") or []
        reading_count_g = analysis.get("reading_count", 0) or 0

        context_labels = {
            "fasting": "Fasting",
            "pre_meal": "Before Meal",
            "post_meal": "After Meal",
            "bedtime": "Bedtime",
            "random": "Random",
            "other": "Other",
            "unknown": "Unknown",
        }
        meal_labels = {
            "breakfast": "Breakfast",
            "lunch": "Lunch",
            "dinner": "Dinner",
            "snack": "Snack",
            "other": "Other",
        }

        def _context_label(value):
            return context_labels.get(
                value,
                str(value or "unknown").replace("_", " ").title(),
            )

        def _fmt_num(value, digits=1, suffix=""):
            if value is None:
                return "-"
            return f"{float(value):.{digits}f}{suffix}"

        y = check_page_break(y, needed=220)
        pdf.setFont("Helvetica-Bold", 13)
        pdf.drawString(LEFT, y, "Blood Glucose Clinical Analysis")
        y -= 20

        # -------------------------------------------------
        # CLINICAL SUMMARY / DATA SOURCE
        # -------------------------------------------------
        pdf.setFont("Helvetica-Bold", 11)
        pdf.drawString(LEFT, y, "Clinical Summary")
        y -= 4
        pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
        pdf.line(LEFT, y, RIGHT, y)
        pdf.setStrokeColorRGB(0, 0, 0)
        y -= 12

        latest_value = latest_g.get("value")
        latest_context = _context_label(latest_g.get("measurement_context"))
        latest_parts = []
        if latest_g.get("meal_type"):
            latest_parts.append(meal_labels.get(
                latest_g.get("meal_type"),
                str(latest_g.get("meal_type")).replace("_", " ").title(),
            ))
        if latest_g.get("minutes_after_meal") is not None:
            latest_parts.append(f"{latest_g['minutes_after_meal']} min after meal")
        source_type = latest_g.get("source_type") or "unknown"
        source_label = {
            "manual_bgm": "Manual/BGM",
            "cgm": "CGM",
            "import": "Imported",
            "other": "Other",
            "unknown": "Unknown",
        }.get(source_type, str(source_type).replace("_", " ").title())

        if latest_value is not None:
            detail = f"Latest: {latest_value:.0f} {unit_g} ({latest_context}"
            if latest_parts:
                detail += "; " + ", ".join(latest_parts)
            detail += f"; source: {source_label})."
            if latest_g.get("recorded_at"):
                detail += f" Recorded: {latest_g['recorded_at']}."
            y = draw_wrapped_line(
                y,
                detail,
                fontsize=9,
                indent=0,
                line_spacing=13,
            )
            y -= 3

        source_counts = source_g.get("counts") or {}
        source_parts = []
        for key, label in (
            ("manual_bgm", "Manual/BGM"),
            ("cgm", "CGM"),
            ("import", "Imported"),
            ("other", "Other"),
            ("unknown", "Unknown"),
        ):
            count = source_counts.get(key, 0) or 0
            if count:
                source_parts.append(f"{label}: {count}")
        source_text = "; ".join(source_parts) if source_parts else "source not classified"

        context_pct = context_g.get("completeness_pct")
        context_text = (
            f"{context_pct:.1f}% with recorded measurement context"
            if context_pct is not None
            else "context completeness unavailable"
        )
        y = draw_wrapped_line(
            y,
            f"Window: last {window_days} days. Observations: {reading_count_g}. "
            f"Source mix: {source_text}. Context coverage: {context_text}.",
            fontsize=9,
            indent=0,
            line_spacing=13,
        )
        y -= 4

        total_low = low_g.get("total_low_count", 0) or 0
        if total_low:
            y = draw_wrapped_line(
                y,
                f"Low-glucose observations: {total_low} total "
                f"({low_g.get('level_1_count', 0) or 0} at 54-69 mg/dL; "
                f"{low_g.get('level_2_count', 0) or 0} below 54 mg/dL).",
                fontsize=9,
                indent=0,
                line_spacing=13,
            )
            y -= 4

        # -------------------------------------------------
        # LOGGED READING SUMMARY — neutral mixed-context values
        # -------------------------------------------------
        if summary_g:
            y = check_page_break(y, needed=70)
            pdf.setFont("Helvetica-Bold", 10)
            pdf.drawString(LEFT, y, "Logged Reading Summary")
            y -= 14
            sum_widths = [82, 108, 108, 107, 107]
            y = draw_table_row(
                y,
                ["Readings", "Mean", "Median", "Minimum", "Maximum"],
                sum_widths,
                fontsize=8,
                bold=True,
                fill_bg=True,
            )
            y = draw_table_row(
                y,
                [
                    str(reading_count_g),
                    _fmt_num(summary_g.get("mean"), 1, f" {unit_g}"),
                    _fmt_num(summary_g.get("median"), 1, f" {unit_g}"),
                    _fmt_num(summary_g.get("min"), 1, f" {unit_g}"),
                    _fmt_num(summary_g.get("max"), 1, f" {unit_g}"),
                ],
                sum_widths,
                fontsize=8,
            )
            y -= 6
            pdf.setFont("Helvetica-Oblique", 8)
            pdf.setFillColorRGB(0.4, 0.4, 0.4)
            y = draw_wrapped_line(
                y,
                "These overall statistics may combine fasting, meal-related, bedtime, "
                "random, and legacy unknown-context readings. They are descriptive and "
                "are not classified against one universal glucose target.",
                fontsize=8,
                indent=10,
                line_spacing=11,
            )
            pdf.setFillColorRGB(0, 0, 0)
            y -= 8

        # -------------------------------------------------
        # CONTEXT-SPECIFIC SUMMARIES
        # -------------------------------------------------
        available_context_summaries = [
            (ctx, block)
            for ctx, block in context_summaries_g.items()
            if block and block.get("is_available")
        ]
        if available_context_summaries:
            y = check_page_break(y, needed=95)
            pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
            pdf.line(LEFT, y, RIGHT, y)
            pdf.setStrokeColorRGB(0, 0, 0)
            y -= 14
            pdf.setFont("Helvetica-Bold", 10)
            pdf.drawString(LEFT, y, "Context-Specific Summaries")
            y -= 14
            ctx_widths = [90, 35, 40, 55, 75, 75, 142]
            y = draw_table_row(
                y,
                ["Context", "n", "Days", "Span", "Mean", "Median", "Recorded Range"],
                ctx_widths,
                fontsize=8,
                bold=True,
                fill_bg=True,
            )
            for ctx, block in sorted(
                available_context_summaries,
                key=lambda item: (
                    "fasting pre_meal post_meal bedtime random other unknown".split()
                    .index(item[0])
                    if item[0] in "fasting pre_meal post_meal bedtime random other unknown".split()
                    else 99
                ),
            ):
                y = check_page_break(y, needed=36)
                range_text = (
                    f"{_fmt_num(block.get('min'), 0)}-"
                    f"{_fmt_num(block.get('max'), 0)} {unit_g}"
                )
                y = draw_table_row(
                    y,
                    [
                        _context_label(ctx),
                        str(block.get("sample_count", 0) or 0),
                        str(block.get("distinct_days", 0) or 0),
                        f"{block.get('span_days', 0):.1f} d",
                        _fmt_num(block.get("mean"), 1, f" {unit_g}"),
                        _fmt_num(block.get("median"), 1, f" {unit_g}"),
                        range_text,
                    ],
                    ctx_widths,
                    fontsize=8,
                )
            y -= 8

        # -------------------------------------------------
        # CONTEXT-SPECIFIC TRENDS
        # -------------------------------------------------
        available_trends = [
            (ctx, block)
            for ctx, block in context_trends_g.items()
            if block and block.get("is_available")
        ]
        if available_trends:
            y = check_page_break(y, needed=95)
            pdf.setFont("Helvetica-Bold", 10)
            pdf.drawString(LEFT, y, "Context-Specific Trends")
            y -= 14
            trend_widths = [100, 42, 62, 80, 115, 113]
            y = draw_table_row(
                y,
                ["Context", "n", "Span", "Direction", "Slope", "Regression"],
                trend_widths,
                fontsize=8,
                bold=True,
                fill_bg=True,
            )
            for ctx, block in sorted(
                available_trends,
                key=lambda item: (
                    "fasting pre_meal post_meal bedtime random other unknown".split()
                    .index(item[0])
                    if item[0] in "fasting pre_meal post_meal bedtime random other unknown".split()
                    else 99
                ),
            ):
                y = check_page_break(y, needed=40)
                if block.get("significance_available") and block.get("p_value") is not None:
                    regression = f"p={block['p_value']:.3f}"
                    if block.get("r2") is not None:
                        regression += f"; R2={block['r2']:.2f}"
                else:
                    regression = "significance gated"
                y = draw_table_row(
                    y,
                    [
                        _context_label(ctx),
                        str(block.get("sample_count", 0) or 0),
                        f"{block.get('span_days', 0):.1f} d",
                        str(block.get("direction") or "-").title(),
                        _fmt_num(block.get("slope_mg_dl_per_day"), 3, " mg/dL/day"),
                        regression,
                    ],
                    trend_widths,
                    fontsize=8,
                )
            y -= 6
            pdf.setFont("Helvetica-Oblique", 8)
            pdf.setFillColorRGB(0.4, 0.4, 0.4)
            y = draw_wrapped_line(
                y,
                "Trend models compare only readings recorded in the same measurement "
                "context. A single regression is not fit through mixed fasting, "
                "meal-related, bedtime, and random observations.",
                fontsize=8,
                indent=10,
                line_spacing=11,
            )
            pdf.setFillColorRGB(0, 0, 0)
            y -= 8

        # -------------------------------------------------
        # LOW-GLUCOSE EVENTS
        # -------------------------------------------------
        if low_g:
            y = check_page_break(y, needed=80)
            pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
            pdf.line(LEFT, y, RIGHT, y)
            pdf.setStrokeColorRGB(0, 0, 0)
            y -= 14
            pdf.setFont("Helvetica-Bold", 10)
            pdf.drawString(LEFT, y, "Low-Glucose Observations")
            y -= 14
            low_widths = [184, 164, 164]
            y = draw_table_row(
                y,
                ["Metric", "Level 1", "Level 2"],
                low_widths,
                fontsize=8,
                bold=True,
                fill_bg=True,
            )
            y = draw_table_row(
                y,
                [
                    "Logged observation count",
                    f"{low_g.get('level_1_count', 0) or 0} (54-69 mg/dL)",
                    f"{low_g.get('level_2_count', 0) or 0} (<54 mg/dL)",
                ],
                low_widths,
                fontsize=8,
            )
            y -= 6
            pdf.setFont("Helvetica-Oblique", 8)
            pdf.setFillColorRGB(0.4, 0.4, 0.4)
            y = draw_wrapped_line(
                y,
                "These are numeric observation classifications. Vitals does not infer "
                "Level 3 hypoglycemia, symptoms, diagnosis, or treatment from glucose "
                "values alone.",
                fontsize=8,
                indent=10,
                line_spacing=11,
            )
            pdf.setFillColorRGB(0, 0, 0)
            y -= 8

        # -------------------------------------------------
        # MEAL EXCURSIONS
        # -------------------------------------------------
        if meal_g.get("pair_count", 0):
            y = check_page_break(y, needed=85)
            pdf.setFont("Helvetica-Bold", 10)
            pdf.drawString(LEFT, y, "Meal Excursions")
            y -= 14
            excursion_summaries = [
                block for block in (meal_g.get("summaries") or [])
                if block and block.get("is_available")
            ]
            if excursion_summaries:
                meal_widths = [100, 92, 62, 120, 138]
                y = draw_table_row(
                    y,
                    ["Meal", "Timing", "Pairs", "Mean Excursion", "Median Excursion"],
                    meal_widths,
                    fontsize=8,
                    bold=True,
                    fill_bg=True,
                )
                for block in excursion_summaries:
                    y = check_page_break(y, needed=40)
                    timing = (
                        f"{block.get('minutes_after_meal')} min"
                        if block.get("minutes_after_meal") is not None
                        else "timing unknown"
                    )
                    y = draw_table_row(
                        y,
                        [
                            meal_labels.get(
                                block.get("meal_type"),
                                str(block.get("meal_type") or "Unknown").title(),
                            ),
                            timing,
                            str(block.get("pair_count", 0) or 0),
                            _fmt_num(block.get("mean_excursion_mg_dl"), 1, " mg/dL"),
                            _fmt_num(block.get("median_excursion_mg_dl"), 1, " mg/dL"),
                        ],
                        meal_widths,
                        fontsize=8,
                    )
            else:
                meal_pairs = meal_g.get("pairs") or []
                if meal_pairs:
                    pair_widths = [95, 90, 95, 95, 137]
                    y = draw_table_row(
                        y,
                        ["Meal", "Timing", "Pre", "Post", "Excursion"],
                        pair_widths,
                        fontsize=8,
                        bold=True,
                        fill_bg=True,
                    )
                    for pair in meal_pairs:
                        y = check_page_break(y, needed=40)
                        timing = (
                            f"{pair.get('minutes_after_meal')} min"
                            if pair.get("minutes_after_meal") is not None
                            else "timing unknown"
                        )
                        y = draw_table_row(
                            y,
                            [
                                meal_labels.get(
                                    pair.get("meal_type"),
                                    str(pair.get("meal_type") or "Unknown").title(),
                                ),
                                timing,
                                _fmt_num(pair.get("pre_value_mg_dl"), 1, " mg/dL"),
                                _fmt_num(pair.get("post_value_mg_dl"), 1, " mg/dL"),
                                _fmt_num(pair.get("excursion_mg_dl"), 1, " mg/dL"),
                            ],
                            pair_widths,
                            fontsize=8,
                        )
                    y -= 4
                y = draw_wrapped_line(
                    y,
                    f"{meal_g.get('pair_count', 0)} unambiguous pre/post-meal pair(s) "
                    "are available. At least 3 comparable pairs at the same meal/timing "
                    "are required before Vitals presents an averaged meal-response pattern.",
                    fontsize=8,
                    indent=10,
                    line_spacing=11,
                )
            y -= 8

        # -------------------------------------------------
        # MEDICATION-CHANGE ASSOCIATIONS
        # -------------------------------------------------
        if medication_g:
            y = check_page_break(y, needed=100)
            pdf.setFont("Helvetica-Bold", 10)
            pdf.drawString(LEFT, y, "Medication-Change Associations")
            y -= 14
            med_widths = [100, 60, 68, 64, 64, 60, 48, 48]
            y = draw_table_row(
                y,
                ["Medication", "Date", "Context", "Before", "After", "Delta", "n Pre", "n Post"],
                med_widths,
                fontsize=7,
                bold=True,
                fill_bg=True,
            )
            for item in medication_g:
                y = check_page_break(y, needed=45)
                y = draw_table_row(
                    y,
                    [
                        str(item.get("medication_name") or "-"),
                        str(item.get("effective_date") or "-"),
                        _context_label(item.get("measurement_context")),
                        _fmt_num(item.get("before_mean"), 1),
                        _fmt_num(item.get("after_mean"), 1),
                        _fmt_num(item.get("mean_delta"), 1),
                        str(item.get("before_n", 0) or 0),
                        str(item.get("after_n", 0) or 0),
                    ],
                    med_widths,
                    fontsize=7,
                )
            y -= 6
            pdf.setFont("Helvetica-Oblique", 8)
            pdf.setFillColorRGB(0.4, 0.4, 0.4)
            y = draw_wrapped_line(
                y,
                "These are observational before/after summaries using the same glucose "
                "context. They describe readings following a recorded medication change "
                "and do not establish that the medication caused the difference.",
                fontsize=8,
                indent=10,
                line_spacing=11,
            )
            pdf.setFillColorRGB(0, 0, 0)
            y -= 8

        # -------------------------------------------------
        # CGM / GMI
        # -------------------------------------------------
        y = check_page_break(y, needed=105)
        pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
        pdf.line(LEFT, y, RIGHT, y)
        pdf.setStrokeColorRGB(0, 0, 0)
        y -= 14
        pdf.setFont("Helvetica-Bold", 10)
        pdf.drawString(LEFT, y, "Glucose Management Indicator (GMI)")
        y -= 14

        if gmi_g.get("is_available") and gmi_g.get("value_pct") is not None:
            pdf.setFont("Helvetica-Bold", 11)
            pdf.drawString(LEFT + 10, y, f"{gmi_g['value_pct']:.1f}%")
            y -= 14
            qualified_parts = []
            if cgm_g.get("mean_glucose_mg_dl") is not None:
                qualified_parts.append(
                    f"mean CGM glucose {cgm_g['mean_glucose_mg_dl']:.1f} mg/dL"
                )
            if cgm_g.get("coverage_days") is not None:
                qualified_parts.append(f"{cgm_g['coverage_days']} days represented")
            if cgm_g.get("active_coverage_pct") is not None:
                qualified_parts.append(
                    f"{cgm_g['active_coverage_pct']:.1f}% active coverage"
                )
            if qualified_parts:
                y = draw_wrapped_line(
                    y,
                    "Qualified CGM basis: " + "; ".join(qualified_parts) + ".",
                    fontsize=9,
                    indent=10,
                    line_spacing=12,
                )
        else:
            pdf.setFont("Helvetica-Bold", 9)
            pdf.drawString(LEFT + 10, y, "Not enough qualified CGM data yet")
            y -= 14
            requirement = (
                gmi_g.get("qualification_requirement")
                or cgm_g.get("qualification_requirement")
                or {}
            )
            min_days = requirement.get("minimum_days", 14)
            min_coverage = requirement.get("minimum_active_coverage_pct", 70)
            y = draw_wrapped_line(
                y,
                f"Vitals can calculate GMI from qualified CGM data. Qualification gate: "
                f"at least {min_days} days represented with "
                f"{float(min_coverage):.0f}% or greater active coverage.",
                fontsize=9,
                indent=10,
                line_spacing=12,
            )

        disclosure = gmi_g.get("disclosure") or (
            "GMI is calculated from mean CGM glucose. It is not a laboratory A1C "
            "result and may differ from your measured A1C."
        )
        if "healthcare professional" not in disclosure.lower():
            disclosure = (
                disclosure.rstrip().rstrip(".")
                + ". Talk with your healthcare professional about laboratory A1C "
                "testing and interpretation."
            )
        y -= 3
        pdf.setFont("Helvetica-Oblique", 8)
        pdf.setFillColorRGB(0.4, 0.4, 0.4)
        y = draw_wrapped_line(
            y,
            disclosure,
            fontsize=8,
            indent=10,
            line_spacing=11,
        )
        pdf.setFillColorRGB(0, 0, 0)
        y -= 8

        # -------------------------------------------------
        # DATA SUPPORT / INTERPRETATION LIMITS
        # -------------------------------------------------
        if support_g:
            y = check_page_break(y, needed=85)
            pdf.setFont("Helvetica-Bold", 10)
            pdf.drawString(LEFT, y, "Data Support")
            y -= 14
            y = draw_wrapped_line(
                y,
                f"State: {(support_g.get('support_state') or 'snapshot').replace('_', ' ')}. "
                f"{support_g.get('n', 0)} reading(s) across "
                f"{support_g.get('distinct_days', 0)} distinct day(s), "
                f"{support_g.get('span_days', 0):.1f}-day span. "
                f"Context completeness: {support_g.get('context_completeness_pct', 0):.1f}%.",
                fontsize=9,
                indent=10,
                line_spacing=12,
            )
            for item in support_g.get("unavailable_analyses") or []:
                if item.get("analysis") == "gmi":
                    continue
                y = check_page_break(y, needed=32)
                name = (item.get("analysis") or "analysis").replace("_", " ").title()
                reason = item.get("reason") or item.get("reason_code") or "not available"
                y = draw_wrapped_line(
                    y,
                    f"- {name}: {reason}",
                    fontsize=8,
                    indent=10,
                    line_spacing=11,
                )
            y -= 8

        if limitations_g:
            y = check_page_break(y, needed=70)
            pdf.setFont("Helvetica-Bold", 10)
            pdf.drawString(LEFT, y, "Interpretation Limits")
            y -= 14
            for limitation in limitations_g:
                y = check_page_break(y, needed=30)
                y = draw_wrapped_line(
                    y,
                    f"- {limitation}",
                    fontsize=8,
                    indent=10,
                    line_spacing=11,
                )
            y -= 8

        pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
        pdf.line(LEFT, y, RIGHT, y)
        pdf.setStrokeColorRGB(0, 0, 0)
        return y - 14


    def draw_scalar_clinical_analysis(
        y,
        title,
        analysis,
        *,
        context_note=None,
        show_trend=True,
    ):
        if analysis is None:
            return y

        y = check_page_break(y, needed=180)
        pdf.setFont("Helvetica-Bold", 13)
        pdf.drawString(LEFT, y, title)
        y -= 20

        latest_s = analysis.get("latest") or {}
        summary_s = analysis.get("summary") or {}
        support_s = analysis.get("data_support") or {}
        unit_s = analysis.get("unit") or ""

        pdf.setFont("Helvetica-Bold", 10)
        pdf.drawString(LEFT, y, "Latest Logged Value")
        y -= 14
        pdf.setFont("Helvetica", 9)
        latest_value = latest_s.get("value")
        latest_display = (
            f"{latest_value:.1f} {unit_s}"
            if latest_value is not None
            else "n/a"
        )
        pdf.drawString(LEFT + 10, y, latest_display)
        y -= 12
        if latest_s.get("recorded_at"):
            y = draw_wrapped_line(
                y,
                f"Recorded: {latest_s['recorded_at']}",
                fontsize=9,
                indent=10,
                line_spacing=12,
            )
        y -= 6

        if summary_s:
            y = check_page_break(y, needed=65)
            pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
            pdf.line(LEFT, y, RIGHT, y)
            pdf.setStrokeColorRGB(0, 0, 0)
            y -= 14
            pdf.setFont("Helvetica-Bold", 10)
            pdf.drawString(LEFT, y, "Logged Reading Summary")
            y -= 14
            pdf.setFont("Helvetica", 9)
            y = draw_wrapped_line(
                y,
                f"Readings: {analysis.get('reading_count', 0)}   |   "
                f"Mean: {summary_s.get('mean', 0):.1f} {unit_s}   |   "
                f"Median: {summary_s.get('median', 0):.1f} {unit_s}   |   "
                f"Range: {summary_s.get('min', 0):.1f}-{summary_s.get('max', 0):.1f} {unit_s}",
                fontsize=9,
                indent=10,
                line_spacing=12,
            )
            y -= 6

        change_s = analysis.get("change_from_first")
        if change_s:
            y = check_page_break(y, needed=55)
            pdf.setFont("Helvetica-Bold", 10)
            pdf.drawString(LEFT, y, "Change Across Logged Window")
            y -= 14
            pct_s = (
                f" ({change_s['pct_change']:+.1f}%)"
                if change_s.get("pct_change") is not None
                else ""
            )
            pdf.setFont("Helvetica", 9)
            y = draw_wrapped_line(
                y,
                f"First: {change_s.get('first_value', 0):.1f} {unit_s}   |   "
                f"Latest: {change_s.get('latest_value', 0):.1f} {unit_s}   |   "
                f"Change: {change_s.get('absolute_change', 0):+.1f} {unit_s}{pct_s}",
                fontsize=9,
                indent=10,
                line_spacing=12,
            )
            y -= 6

        trend_s = analysis.get("trend")
        if show_trend and trend_s:
            y = check_page_break(y, needed=65)
            pdf.setFont("Helvetica-Bold", 10)
            pdf.drawString(LEFT, y, "Descriptive Longitudinal Trend")
            y -= 14
            pdf.setFont("Helvetica", 9)
            y = draw_wrapped_line(
                y,
                f"Modeled rate: {trend_s.get('slope_per_week', 0):+.2f} {unit_s}/week   |   "
                f"Span: {trend_s.get('span_days', 0):.1f} days   |   "
                f"R2: {trend_s.get('r2', 0):.2f}   |   p={trend_s.get('p_value', 0):.3f}",
                fontsize=9,
                indent=10,
                line_spacing=12,
            )
            pdf.setFont("Helvetica-Oblique", 8)
            pdf.setFillColorRGB(0.4, 0.4, 0.4)
            y = draw_wrapped_line(
                y,
                "This is a descriptive modeled pattern and does not by itself determine "
                "whether the change is medically desirable or harmful.",
                fontsize=8,
                indent=10,
                line_spacing=11,
            )
            pdf.setFillColorRGB(0, 0, 0)
            y -= 6

        if context_note:
            y = check_page_break(y, needed=55)
            pdf.setFont("Helvetica-Oblique", 8)
            pdf.setFillColorRGB(0.4, 0.4, 0.4)
            y = draw_wrapped_line(
                y,
                context_note,
                fontsize=8,
                indent=10,
                line_spacing=11,
            )
            pdf.setFillColorRGB(0, 0, 0)
            y -= 8

        if support_s:
            y = check_page_break(y, needed=75)
            pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
            pdf.line(LEFT, y, RIGHT, y)
            pdf.setStrokeColorRGB(0, 0, 0)
            y -= 14
            pdf.setFont("Helvetica-Bold", 10)
            pdf.drawString(LEFT, y, "Data Support")
            y -= 14
            pdf.setFont("Helvetica", 9)
            y = draw_wrapped_line(
                y,
                f"State: {(support_s.get('support_state') or 'snapshot').replace('_', ' ')}   |   "
                f"{support_s.get('n', 0)} readings on {support_s.get('distinct_days', 0)} "
                f"distinct days across {support_s.get('span_days', 0):.1f} days",
                fontsize=9,
                indent=10,
                line_spacing=12,
            )

            unavailable_s = support_s.get("unavailable_analyses") or []
            for item in unavailable_s:
                y = check_page_break(y, needed=32)
                name = (item.get("analysis") or "analysis").replace("_", " ").title()
                reason = item.get("reason") or item.get("reason_code") or "not available"
                y = draw_wrapped_line(
                    y,
                    f"- {name}: {reason}",
                    fontsize=8,
                    indent=10,
                    line_spacing=11,
                )
            y -= 8

        pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
        pdf.line(LEFT, y, RIGHT, y)
        pdf.setStrokeColorRGB(0, 0, 0)
        y -= 14
        return y


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
    pdf.drawString(LEFT, y, "Most Recent Vitals & Period Averages")
    y -= 18

    conn2 = get_conn()
    cur2 = conn2.cursor()
    cur2.execute("""
        SELECT round(avg(systolic),1), round(avg(diastolic),1),
               round(avg(heart_rate),1), round(avg(oxygen_saturation),1),
               round(avg(temperature),1), round(avg(weight),1),
               round(avg(blood_glucose),1)
        FROM vitals WHERE patient_id = %s AND household_id = %s
          AND recorded_at >= now() - interval '%s days'
    """, (str(patient_id), household_id, days))
    avg = cur2.fetchone()
    cur2.close()
    conn2.close()

    if latest and latest[0] is not None:
        taken, sys, dia, hr, spo2, temp, weight, glucose = latest
        avg_sys, avg_dia, avg_hr, avg_spo2, avg_temp, avg_weight, avg_glucose = (
            avg if avg else (None,) * 7
        )

        pdf.setFont("Helvetica", 9)
        pdf.drawString(LEFT, y, f"Last vital entry: {taken.strftime('%m/%d/%Y %I:%M %p')}")
        y -= 16

        summary_rows = []
        if (
            (sys is not None and dia is not None) or
            (avg_sys is not None and avg_dia is not None)
        ):
            summary_rows.append((
                "Blood Pressure",
                f"{sys}/{dia} mmHg" if sys is not None and dia is not None else "n/a",
                f"{avg_sys:.0f}/{avg_dia:.0f} mmHg"
                if avg_sys is not None and avg_dia is not None else "n/a",
            ))

        if show_hr and (hr is not None or avg_hr is not None):
            summary_rows.append((
                "Heart Rate",
                f"{hr} BPM" if hr is not None else "n/a",
                f"{avg_hr:.0f} BPM" if avg_hr is not None else "n/a",
            ))
        if show_spo2 and (spo2 is not None or avg_spo2 is not None):
            summary_rows.append((
                "Oxygen Saturation",
                f"{spo2}%" if spo2 is not None else "n/a",
                f"{avg_spo2:.1f}%" if avg_spo2 is not None else "n/a",
            ))
        if show_temp and (temp is not None or avg_temp is not None):
            summary_rows.append((
                "Temperature",
                f"{float(temp):.1f} F" if temp is not None else "n/a",
                f"{avg_temp:.1f} F" if avg_temp is not None else "n/a",
            ))
        if show_weight and (weight is not None or avg_weight is not None):
            summary_rows.append((
                "Weight",
                f"{float(weight):.1f} lb" if weight is not None else "n/a",
                f"{avg_weight:.1f} lb" if avg_weight is not None else "n/a",
            ))
        if show_glucose and (glucose is not None or avg_glucose is not None):
            summary_rows.append((
                "Blood Glucose",
                f"{glucose} mg/dL" if glucose is not None else "n/a",
                f"{avg_glucose:.0f} mg/dL" if avg_glucose is not None else "n/a",
            ))

        if summary_rows:
            summary_widths = [170, 160, 182]
            y = draw_table_row(
                y,
                ["Vital", "Latest", f"{days}-Day Logged Average"],
                summary_widths,
                fontsize=8,
                bold=True,
                fill_bg=True,
            )
            for label, latest_display, avg_display in summary_rows:
                y = check_page_break(y, needed=35)
                y = draw_table_row(
                    y,
                    [label, latest_display, avg_display],
                    summary_widths,
                    fontsize=8,
                )
            y -= 12
        else:
            pdf.setFont("Helvetica", 9)
            pdf.drawString(LEFT, y, "No tracked vital values are available in this period.")
            y -= 20
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
        dates        = [r[0] for r in chart_data]
        sys_vals     = [r[1] for r in chart_data]
        dia_vals     = [r[2] for r in chart_data]
        hr_vals      = [r[3] for r in chart_data]
        spo2_vals    = [r[4] for r in chart_data]
        temp_vals    = [float(r[5]) if r[5] is not None else None for r in chart_data]
        weight_vals  = [float(r[6]) if r[6] is not None else None for r in chart_data]
        glucose_vals = [float(r[7]) if r[7] is not None else None for r in chart_data]

        def has_values(values):
            return any(v is not None for v in values)

        def make_chart(
            title,
            datasets,
            ylabel,
            chart_width=480,
            chart_height=160,
            smooth=True,
        ):
            fig, ax = plt.subplots(figsize=(chart_width/72, chart_height/72))
            for label, values, color in datasets:
                paired = [(d, v) for d, v in zip(dates, values) if v is not None]
                if not paired:
                    continue

                d_clean, v_clean = zip(*paired)
                d_clean = list(d_clean)
                v_clean = list(v_clean)

                ax.scatter(
                    d_clean,
                    v_clean,
                    color=color,
                    alpha=0.45 if not smooth else 0.25,
                    s=18 if not smooth else 14,
                    zorder=2,
                    label=label if not smooth else None,
                )

                if smooth and len(v_clean) >= 4:
                    x_ord = np.array([d.toordinal() for d in d_clean], dtype=float)
                    y_arr = np.array(v_clean, dtype=float)
                    sort_idx = np.argsort(x_ord)
                    y_loess = loess_smooth(x_ord[sort_idx], y_arr[sort_idx], frac=0.4)
                    ax.plot(
                        [d_clean[i] for i in sort_idx],
                        y_loess,
                        color=color,
                        linewidth=2.0,
                        zorder=3,
                        label=label,
                    )
                elif smooth:
                    ax.plot(
                        d_clean,
                        v_clean,
                        color=color,
                        linewidth=1.5,
                        marker='o',
                        markersize=3,
                        zorder=3,
                        label=label,
                    )

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

        chart_specs = []
        if has_values(sys_vals) or has_values(dia_vals):
            chart_specs.append((
                "Blood Pressure (mmHg)",
                [("Systolic", sys_vals, "#d32f2f"), ("Diastolic", dia_vals, "#1976d2")],
                "mmHg",
                True,
            ))
        if show_hr and has_values(hr_vals):
            chart_specs.append((
                "Heart Rate (BPM)",
                [("Heart Rate", hr_vals, "#388e3c")],
                "BPM",
                True,
            ))
        if show_spo2 and has_values(spo2_vals):
            chart_specs.append((
                "Oxygen Saturation (%)",
                [("SpO2", spo2_vals, "#7b1fa2")],
                "%",
                True,
            ))
        if show_temp and has_values(temp_vals):
            chart_specs.append((
                "Temperature (F)",
                [("Temperature", temp_vals, "#f57c00")],
                "F",
                True,
            ))
        if show_weight and has_values(weight_vals):
            chart_specs.append((
                "Weight (lb)",
                [("Weight", weight_vals, "#00897b")],
                "lb",
                True,
            ))
        if show_glucose and has_values(glucose_vals):
            chart_specs.append((
                "Blood Glucose (mg/dL)",
                [("Blood Glucose", glucose_vals, "#8e24aa")],
                "mg/dL",
                False,
            ))

        if chart_specs:
            pdf.showPage()
            y = height - 50
            pdf.setFont("Helvetica-Bold", 13)
            pdf.drawString(LEFT, y, f"Vital Trends (Last {days} Days)")
            y -= 20

            chart_w = 480
            chart_h = 160

            for title, datasets, ylabel, smooth in chart_specs:
                y = check_page_break(y, needed=chart_h + 22)
                chart_buf = make_chart(
                    title,
                    datasets,
                    ylabel,
                    chart_width=chart_w,
                    chart_height=chart_h,
                    smooth=smooth,
                )
                pdf.drawImage(
                    ImageReader(chart_buf),
                    LEFT,
                    y - chart_h,
                    width=chart_w,
                    height=chart_h,
                )
                y -= chart_h + 20

                if not smooth:
                    pdf.setFont("Helvetica-Oblique", 7)
                    pdf.setFillColorRGB(0.4, 0.4, 0.4)
                    y = draw_wrapped_line(
                        y,
                        "Glucose values are shown as raw logged observations in this overview. "
                        "Measurement contexts are not mixed into one smoothed trajectory; "
                        "eligible same-context trends are reported in the Glucose Clinical Analysis section.",
                        fontsize=7,
                        indent=6,
                        line_spacing=9,
                    )
                    pdf.setFillColorRGB(0, 0, 0)
                    y -= 6

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

    # Blood pressure is the core vital and uses its own >=7-reading gate.
    # Mirror the app's scoped "Not Enough Data Yet" behavior in the PDF
    # instead of silently omitting BP whenever optional-vital analysis exists.
    if bp is None:
        pdf.showPage()
        y = height - 50
        pdf.setFont("Helvetica-Bold", 13)
        pdf.drawString(LEFT, y, f"Vitals Analysis (Last {days} Days)")
        y -= 20

        pdf.setFont("Helvetica-Bold", 11)
        pdf.drawString(LEFT, y, "Blood Pressure Clinical Analysis")
        y -= 14
        pdf.setFont("Helvetica-Bold", 10)
        pdf.drawString(LEFT + 10, y, "Not Enough Data Yet")
        y -= 14
        pdf.setFont("Helvetica", 9)
        bp_count = len(bp_analysis_rows)
        y = draw_wrapped_line(
            y,
            f"{bp_count} blood pressure reading(s) are available in this report window. "
            f"Blood pressure analysis requires at least 7 readings. Other tracked vitals "
            f"below are analyzed independently when their own data requirements are met.",
            fontsize=9,
            indent=10,
            line_spacing=12,
        )
        y -= 10
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

    # =====================================================
    # SPO2 — CLINICAL ANALYSIS
    # Mirrors the Heart Rate section above in structure, using
    # spo2_analysis — the same run_spo2_analysis engine powering the
    # app's Analysis tab — not the older analyze_vital_series output
    # Temperature below still uses. Reference bands and low-
    # observation counts are deliberately NOT framed as "time in
    # range"/"time below target" — spot readings don't support that
    # continuous-coverage assumption (see the methodology note drawn
    # with that block below).
    # =====================================================
    if spo2_analysis is not None:
        y = check_page_break(y, needed=200)
        pdf.setFont("Helvetica-Bold", 13)
        pdf.drawString(LEFT, y, "Oxygen Saturation (SpO2) Clinical Analysis")
        y -= 20

        pdf.setFont("Helvetica-Bold", 11)
        pdf.drawString(LEFT, y, "Clinical Summary")
        y -= 4
        pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
        pdf.line(LEFT, y, RIGHT, y)
        pdf.setStrokeColorRGB(0, 0, 0)
        y -= 12

        for para in build_spo2_clinical_summary(spo2_analysis):
            y = check_page_break(y, needed=40)
            y = draw_wrapped_line(y, para, fontsize=9, indent=0, line_spacing=13)
            y -= 6

        y -= 6
        pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
        pdf.line(LEFT, y, RIGHT, y)
        pdf.setStrokeColorRGB(0, 0, 0)
        y -= 14

        ss = spo2_analysis.get("spot_summary")
        if ss:
            pdf.setFont("Helvetica-Bold", 11)
            pdf.drawString(LEFT, y, "Detailed Metrics")
            y -= 14

            pdf.setFont("Helvetica-Bold", 10)
            pdf.drawString(LEFT, y, "Readings analyzed:")
            pdf.setFont("Helvetica", 10)
            pdf.drawString(LEFT + 130, y, f"{ss['n']} ({ss['distinct_days']} distinct days)")
            y -= 14

            pdf.setFont("Helvetica-Bold", 10)
            pdf.drawString(LEFT, y, "Mean / median:")
            pdf.setFont("Helvetica", 10)
            pdf.drawString(LEFT + 150, y,
                f"{ss['mean']:.1f}% / {ss['median']:.1f}%  (range {ss['min']}\u2013{ss['max']}%)")
            y -= 20

            disp = spo2_analysis.get("dispersion")
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
                    f"SD: {disp['sd']:.1f} pts   |   IQR: {disp['iqr']:.1f} pts "
                    f"(Q1={disp['q1']:.1f}, Q3={disp['q3']:.1f})")
                y -= 20

            trend = spo2_analysis.get("trend")
            if trend:
                y = check_page_break(y, needed=60)
                pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
                pdf.line(LEFT, y, RIGHT, y)
                pdf.setStrokeColorRGB(0, 0, 0)
                y -= 14
                pdf.setFont("Helvetica-Bold", 10)
                pdf.drawString(LEFT, y, "Trend")
                y -= 14
                pdf.setFont("Helvetica", 9)
                p_display = f"{trend['p_value']:.3f}" if trend.get("p_value") is not None else "n/a (insufficient span/n)"
                pdf.drawString(LEFT + 10, y,
                    f"Trend: {trend['trend_label'].replace('_', ' ').title()}   |   "
                    f"Rate: {trend['slope_pct_points_per_day']:+.2f} pts/day   |   "
                    f"Span: {trend['span_days']:.0f} days")
                y -= 12
                # Only shown for an actual rising/falling trend — same
                # reasoning as the app and build_spo2_clinical_summary
                # above: R² against a flat/stable series isn't a
                # quality signal.
                if trend.get("trend_label") != "stable" and trend.get("r2") is not None:
                    pdf.drawString(LEFT + 10, y,
                        f"p-value: {p_display}   |   Trend fit: R\u00b2={trend['r2']:.2f}   |   "
                        f"Consistency: {trend.get('consistency') or 'n/a'}")
                    y -= 12
                y -= 8

            bd = spo2_analysis.get("baseline_deviation")
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
                    f"Prior 30-day baseline: {bd['baseline_median']:.1f}% (n={bd['baseline_n']})   |   "
                    f"Recent 7 days: {bd['recent_median']:.1f}% (n={bd['recent_n']})")
                y -= 12
                pdf.drawString(LEFT + 10, y, f"Change: {bd['delta_pct_points']:+.1f} points")
                y -= 20

            low = spo2_analysis.get("low_observations")
            if low and low.get("count", 0) > 0:
                y = check_page_break(y, needed=70)
                pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
                pdf.line(LEFT, y, RIGHT, y)
                pdf.setStrokeColorRGB(0, 0, 0)
                y -= 14
                pdf.setFont("Helvetica-Bold", 10)
                pdf.drawString(LEFT, y, "Readings Below Target")
                y -= 14
                pdf.setFont("Helvetica", 9)
                pct_note = f", {low['pct_of_logged_readings']:.0f}%" if low.get("pct_of_logged_readings") is not None else ""
                pdf.drawString(LEFT + 10, y,
                    f"Below {low['threshold']}%: {low['count']} reading(s){pct_note} of logged readings")
                y -= 12

                confirmed = spo2_analysis.get("confirmed_low_observations")
                if confirmed and confirmed.get("episode_count", 0) > 0:
                    pdf.drawString(LEFT + 10, y,
                        f"Repeat-confirmed episodes: {confirmed['episode_count']} "
                        f"(>=2 readings within {confirmed['confirmation_rule_minutes']} min of each other)")
                    y -= 12

                marked = spo2_analysis.get("marked_low_observations")
                if marked and marked.get("count", 0) > 0:
                    pdf.drawString(LEFT + 10, y,
                        f"Markedly low (below {marked['threshold']}%): {marked['count']} reading(s)")
                    y -= 12

                pdf.setFont("Helvetica-Oblique", 8)
                pdf.setFillColorRGB(0.4, 0.4, 0.4)
                pdf.drawString(LEFT + 10, y,
                    "Methodology: discrete reading counts against fixed thresholds \u2014 "
                    "not a measure of continuous time below target.")
                pdf.setFillColorRGB(0, 0, 0)
                y -= 20

            bands = spo2_analysis.get("reference_bands")
            if bands:
                y = check_page_break(y, needed=70)
                pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
                pdf.line(LEFT, y, RIGHT, y)
                pdf.setStrokeColorRGB(0, 0, 0)
                y -= 14
                pdf.setFont("Helvetica-Bold", 10)
                pdf.drawString(LEFT, y, "Reference Bands (Logged Readings)")
                y -= 14

                band_headers = ["Normal (>=95%)", "Mild (92-94%)", "Moderate (88-91%)", "Severe (<88%)"]
                band_keys    = ["at_or_above_95", "92_to_94", "88_to_91", "below_88"]
                col_w        = USABLE_WIDTH / len(band_headers)
                col_widths   = [col_w] * len(band_headers)
                y = draw_table_row(y, band_headers, col_widths, bold=True, fill_bg=True)
                values = [str(bands.get(k, {}).get("count", 0)) for k in band_keys]
                y = draw_table_row(y, values, col_widths)

                y -= 6
                pdf.setFont("Helvetica-Oblique", 8)
                pdf.setFillColorRGB(0.4, 0.4, 0.4)
                pdf.drawString(LEFT + 10, y,
                    "Counts of logged readings only \u2014 not a measure of time spent in each range.")
                pdf.setFillColorRGB(0, 0, 0)
                y -= 20

            corr = spo2_analysis.get("cross_vital_correlations")
            if corr:
                y = check_page_break(y, needed=40)
                pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
                pdf.line(LEFT, y, RIGHT, y)
                pdf.setStrokeColorRGB(0, 0, 0)
                y -= 14
                pdf.setFont("Helvetica-Bold", 10)
                pdf.drawString(LEFT, y, "Correlation with Other Vitals (Physician Reference Only)")
                y -= 14
                pdf.setFont("Helvetica", 9)
                corr_str = "   |   ".join(f"{k.replace('_', ' ')}: r={v:.2f}" for k, v in corr.items())
                pdf.drawString(LEFT + 10, y, corr_str)
                y -= 12
                pdf.setFont("Helvetica-Oblique", 8)
                pdf.setFillColorRGB(0.4, 0.4, 0.4)
                pdf.drawString(LEFT + 10, y, "Correlation does not establish cause.")
                pdf.setFillColorRGB(0, 0, 0)
                y -= 20

        y -= 6
        pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
        pdf.line(LEFT, y, RIGHT, y)
        pdf.setStrokeColorRGB(0, 0, 0)
        y -= 14


    # =====================================================
    # TEMPERATURE — CLINICAL ANALYSIS
    # Uses run_temperature_analysis via the cache: episode-centric,
    # measurement-site-aware, and based on discrete logged readings.
    # No generic "normal/elevated" classification, 30-day global OLS,
    # or interpolated fever burden is used here.
    # =====================================================
    if temp_analysis is not None:
        y = check_page_break(y, needed=220)
        pdf.setFont("Helvetica-Bold", 13)
        pdf.drawString(LEFT, y, "Temperature Clinical Analysis")
        y -= 20

        pdf.setFont("Helvetica-Bold", 11)
        pdf.drawString(LEFT, y, "Clinical Summary")
        y -= 4
        pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
        pdf.line(LEFT, y, RIGHT, y)
        pdf.setStrokeColorRGB(0, 0, 0)
        y -= 12

        for para in build_temperature_clinical_summary(temp_analysis):
            y = check_page_break(y, needed=42)
            y = draw_wrapped_line(y, para, fontsize=9, indent=0, line_spacing=13)
            y -= 6

        y -= 6
        pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
        pdf.line(LEFT, y, RIGHT, y)
        pdf.setStrokeColorRGB(0, 0, 0)
        y -= 14

        latest_t = temp_analysis.get("latest") or {}
        ds_t = temp_analysis.get("data_support") or {}
        ranges_t = temp_analysis.get("range_events") or {}
        profile_t = temp_analysis.get("target_profile") or {}

        pdf.setFont("Helvetica-Bold", 11)
        pdf.drawString(LEFT, y, "Detailed Metrics")
        y -= 16

        # Latest / measurement context
        y = check_page_break(y, needed=75)
        pdf.setFont("Helvetica-Bold", 10)
        pdf.drawString(LEFT, y, "Latest Temperature")
        y -= 14
        pdf.setFont("Helvetica", 9)
        recorded_at = latest_t.get("recorded_at") or "n/a"
        pdf.drawString(LEFT + 10, y,
            f"Value: {latest_t.get('value_f', 0):.1f} F ({latest_t.get('value_c', 0):.1f} C)   |   "
            f"Site: {(latest_t.get('site') or 'unknown').replace('_', ' ').title()}")
        y -= 12
        y = draw_wrapped_line(
            y,
            f"Recorded: {recorded_at}   |   Ingestion source: "
            f"{(latest_t.get('source') or 'unknown').replace('_', ' ')}",
            fontsize=9, indent=10, line_spacing=12
        )
        y -= 8

        # Data support and site quality
        y = check_page_break(y, needed=75)
        pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
        pdf.line(LEFT, y, RIGHT, y)
        pdf.setStrokeColorRGB(0, 0, 0)
        y -= 14
        pdf.setFont("Helvetica-Bold", 10)
        pdf.drawString(LEFT, y, "Data Support / Measurement Site")
        y -= 14
        pdf.setFont("Helvetica", 9)
        pdf.drawString(LEFT + 10, y,
            f"Readings: {ds_t.get('n', temp_analysis.get('reading_count', 0))}   |   "
            f"Distinct days: {ds_t.get('distinct_days', 0)}   |   "
            f"Observed span: {ds_t.get('span_days', 0):.1f} days")
        y -= 12
        site_consistency = (
            f"{ds_t['site_consistency_pct']:.1f}%"
            if ds_t.get("site_consistency_pct") is not None else "n/a"
        )
        pdf.drawString(LEFT + 10, y,
            f"Known-site coverage: {ds_t.get('known_site_pct', 0):.1f}%   |   "
            f"Modal site: {ds_t.get('modal_site') or 'n/a'}   |   "
            f"Same-site consistency: {site_consistency}")
        y -= 20

        # Personal baseline
        baseline_t = temp_analysis.get("baseline")
        if baseline_t:
            y = check_page_break(y, needed=70)
            pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
            pdf.line(LEFT, y, RIGHT, y)
            pdf.setStrokeColorRGB(0, 0, 0)
            y -= 14
            pdf.setFont("Helvetica-Bold", 10)
            pdf.drawString(LEFT, y, "Personal Same-Site Baseline")
            y -= 14
            pdf.setFont("Helvetica", 9)
            pdf.drawString(LEFT + 10, y,
                f"Site: {baseline_t.get('site', 'unknown')}   |   Median: "
                f"{baseline_t.get('median_f', 0):.1f} F ({baseline_t.get('median_c', 0):.1f} C)   |   "
                f"Latest delta: {baseline_t.get('delta_current_f', 0):+.1f} F")
            y -= 12
            pdf.drawString(LEFT + 10, y,
                f"Support: {baseline_t.get('n', 0)} readings on "
                f"{baseline_t.get('distinct_days', 0)} days spanning "
                f"{baseline_t.get('span_days', 0):.1f} days")
            y -= 20

        # Fever-range logged observations
        y = check_page_break(y, needed=80)
        pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
        pdf.line(LEFT, y, RIGHT, y)
        pdf.setStrokeColorRGB(0, 0, 0)
        y -= 14
        pdf.setFont("Helvetica-Bold", 10)
        pdf.drawString(LEFT, y, "Fever-Range Logged Observations")
        y -= 14
        pdf.setFont("Helvetica", 9)
        pdf.drawString(LEFT + 10, y,
            f"Configured fever reference: >= {profile_t.get('fever_threshold_f', ranges_t.get('fever_threshold_f', 100.4)):.1f} F "
            f"({profile_t.get('fever_threshold_c', 38.0):.1f} C)")
        y -= 12
        pdf.drawString(LEFT + 10, y,
            f"Fever-range readings: {ranges_t.get('fever_count', 0)}   |   "
            f"Percent of logged readings: {ranges_t.get('fever_logged_pct', 0):.1f}%   |   "
            f"Febrile days: {ranges_t.get('febrile_days', 0)}")
        y -= 12
        pdf.setFont("Helvetica-Oblique", 8)
        pdf.setFillColorRGB(0.4, 0.4, 0.4)
        pdf.drawString(LEFT + 10, y,
            "Discrete logged observations only - not an estimate of continuous time with fever.")
        pdf.setFillColorRGB(0, 0, 0)
        y -= 20

        # Episode table
        episodes_t = temp_analysis.get("episodes") or []
        if episodes_t:
            y = check_page_break(y, needed=90)
            pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
            pdf.line(LEFT, y, RIGHT, y)
            pdf.setStrokeColorRGB(0, 0, 0)
            y -= 14
            pdf.setFont("Helvetica-Bold", 10)
            pdf.drawString(LEFT, y, "Recorded Fever Episodes")
            y -= 14
            ep_widths = [42, 92, 92, 94, 94, 98]
            ep_headers = ["#", "First fever", "Last fever", "Peak", "Minimum", "Observed span"]
            y = draw_table_row(y, ep_headers, ep_widths, fontsize=8, bold=True, fill_bg=True)
            for ep in episodes_t:
                y = check_page_break(y, needed=55)
                peak_site = (ep.get("peak_site") or "unknown").replace("_", " ")
                min_site = (ep.get("minimum_site") or "unknown").replace("_", " ")
                y = draw_table_row(
                    y,
                    [
                        str(ep.get("episode_id", "")),
                        str(ep.get("first_fever_at", "")),
                        str(ep.get("last_fever_at", "")),
                        f"{ep.get('peak_f', 0):.1f} F ({peak_site})",
                        f"{ep.get('minimum_f', 0):.1f} F ({min_site})",
                        f"{ep.get('observed_span_hours', 0):.1f} h; "
                        f"{ep.get('n_fever_readings', 0)} reading(s)",
                    ],
                    ep_widths, fontsize=8
                )
            y -= 6
            pdf.setFont("Helvetica-Oblique", 8)
            pdf.setFillColorRGB(0.4, 0.4, 0.4)
            y = draw_wrapped_line(
                y,
                "Episode grouping uses a Vitals 24-hour gap rule. Observed span is the interval "
                "between logged fever-range readings, not confirmed continuous fever duration.",
                fontsize=8, indent=10, line_spacing=11
            )
            pdf.setFillColorRGB(0, 0, 0)
            y -= 10

        # Acute same-site trajectory
        acute_t = temp_analysis.get("acute_trend")
        if acute_t:
            y = check_page_break(y, needed=80)
            pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
            pdf.line(LEFT, y, RIGHT, y)
            pdf.setStrokeColorRGB(0, 0, 0)
            y -= 14
            pdf.setFont("Helvetica-Bold", 10)
            pdf.drawString(LEFT, y, "Acute Same-Site Episode Trend")
            y -= 14
            pdf.setFont("Helvetica", 9)
            pdf.drawString(LEFT + 10, y,
                f"Direction: {acute_t.get('trend_label', 'stable').title()}   |   "
                f"Site: {acute_t.get('site', 'unknown')}   |   "
                f"Support: n={acute_t.get('n', 0)}, span {acute_t.get('span_hours', 0):.1f} h")
            y -= 12
            p_disp = (
                f"{acute_t['p_value']:.3f}" if acute_t.get("p_value") is not None else "n/a"
            )
            r2_disp = (
                f"{acute_t['r2']:.2f}" if acute_t.get("r2") is not None else "n/a"
            )
            pdf.drawString(LEFT + 10, y,
                f"Slope: {acute_t.get('slope_f_per_12_hours', 0):+.2f} F/12 h   |   "
                f"Modeled change: {acute_t.get('modeled_change_f', 0):+.1f} F   |   "
                f"R2: {r2_disp}   |   p: {p_disp}")
            y -= 12
            pdf.setFont("Helvetica-Oblique", 8)
            pdf.setFillColorRGB(0.4, 0.4, 0.4)
            pdf.drawString(LEFT + 10, y,
                "Short-window same-site episode model; not a long-term temperature trend.")
            pdf.setFillColorRGB(0, 0, 0)
            y -= 20

        # Low-temperature / hypothermia-range observations
        y = check_page_break(y, needed=80)
        pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
        pdf.line(LEFT, y, RIGHT, y)
        pdf.setStrokeColorRGB(0, 0, 0)
        y -= 14
        pdf.setFont("Helvetica-Bold", 10)
        pdf.drawString(LEFT, y, "Low-Temperature / Hypothermia-Range Observations")
        y -= 14
        pdf.setFont("Helvetica", 9)
        pdf.drawString(LEFT + 10, y,
            f"Lowest logged: {ranges_t.get('lowest_f', 0):.1f} F "
            f"({ranges_t.get('lowest_c', 0):.1f} C), site: "
            f"{(ranges_t.get('lowest_site') or 'unknown').replace('_', ' ').title()}")
        y -= 12
        pdf.drawString(LEFT + 10, y,
            f"Hypothermia-range reference: < {ranges_t.get('hypothermia_threshold_f', 95.0):.1f} F   |   "
            f"Count: {ranges_t.get('hypothermia_range_count', 0)}")
        y -= 12
        pdf.setFont("Helvetica-Oblique", 8)
        pdf.setFillColorRGB(0.4, 0.4, 0.4)
        y = draw_wrapped_line(
            y,
            "A broader low-temperature product threshold is not configured. The recognized "
            "hypothermia-range reference below 95 F is evaluated separately.",
            fontsize=8, indent=10, line_spacing=11
        )
        pdf.setFillColorRGB(0, 0, 0)
        y -= 8

        hypo_readings = ranges_t.get("hypothermia_readings") or []
        if hypo_readings:
            low_widths = [155, 110, 110, 137]
            y = draw_table_row(y, ["Recorded", "Temperature", "Site", "Context"], low_widths, fontsize=8, bold=True, fill_bg=True)
            for r in hypo_readings:
                y = check_page_break(y, needed=45)
                y = draw_table_row(
                    y,
                    [
                        str(r.get("recorded_at", "")),
                        f"{r.get('value_f', 0):.1f} F",
                        (r.get("site") or "unknown").replace("_", " ").title(),
                        "Hypothermia-range",
                    ],
                    low_widths, fontsize=8
                )
            y -= 8

        # Same-event cross-vital context
        cvc_t = temp_analysis.get("cross_vital_context") or {}
        paired_t = cvc_t.get("paired_counts") or {}
        fever_obs_t = cvc_t.get("fever_observations") or []
        if any((paired_t.get("heart_rate", 0), paired_t.get("oxygen_saturation", 0), paired_t.get("blood_pressure", 0))):
            y = check_page_break(y, needed=80)
            pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
            pdf.line(LEFT, y, RIGHT, y)
            pdf.setStrokeColorRGB(0, 0, 0)
            y -= 14
            pdf.setFont("Helvetica-Bold", 10)
            pdf.drawString(LEFT, y, "Same-Event Cross-Vital Context")
            y -= 14
            pdf.setFont("Helvetica", 9)
            pdf.drawString(LEFT + 10, y,
                f"Paired fever observations - HR: {paired_t.get('heart_rate', 0)}   |   "
                f"SpO2: {paired_t.get('oxygen_saturation', 0)}   |   "
                f"BP: {paired_t.get('blood_pressure', 0)}")
            y -= 14
            cv_widths = [110, 65, 70, 65, 65, 137]
            y = draw_table_row(y, ["Recorded", "Temp", "Site", "HR", "SpO2", "Blood pressure"], cv_widths, fontsize=8, bold=True, fill_bg=True)
            for obs in fever_obs_t:
                ctx = obs.get("context") or {}
                bp_ctx = ctx.get("blood_pressure") or {}
                bp_text = (
                    f"{bp_ctx.get('systolic')}/{bp_ctx.get('diastolic')} mmHg"
                    if bp_ctx else "-"
                )
                y = check_page_break(y, needed=45)
                y = draw_table_row(
                    y,
                    [
                        str(obs.get("recorded_at", "")),
                        f"{obs.get('temperature_f', 0):.1f} F",
                        (obs.get("site") or "unknown").replace("_", " ").title(),
                        str(ctx.get("heart_rate") or "-"),
                        str(ctx.get("oxygen_saturation") or "-"),
                        bp_text,
                    ],
                    cv_widths, fontsize=8
                )
            y -= 6
            pdf.setFont("Helvetica-Oblique", 8)
            pdf.setFillColorRGB(0.4, 0.4, 0.4)
            pdf.drawString(LEFT + 10, y,
                "Measurements occurred alongside one another; temporal pairing does not establish causation.")
            pdf.setFillColorRGB(0, 0, 0)
            y -= 18

        # Unavailable capabilities / confidence notes
        unavailable_t = ds_t.get("unavailable_analyses") or []
        if unavailable_t:
            y = check_page_break(y, needed=60)
            pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
            pdf.line(LEFT, y, RIGHT, y)
            pdf.setStrokeColorRGB(0, 0, 0)
            y -= 14
            pdf.setFont("Helvetica-Bold", 10)
            pdf.drawString(LEFT, y, "Unavailable / Limited Analyses")
            y -= 14
            for item in unavailable_t:
                y = check_page_break(y, needed=35)
                name = (item.get("analysis") or "analysis").replace("_", " ").title()
                reason = item.get("reason") or item.get("reason_code") or "not available"
                y = draw_wrapped_line(
                    y, f"- {name}: {reason}", fontsize=8, indent=10, line_spacing=11
                )
                y -= 2

        y -= 6
        pdf.setStrokeColorRGB(0.7, 0.7, 0.7)
        pdf.line(LEFT, y, RIGHT, y)
        pdf.setStrokeColorRGB(0, 0, 0)
        y -= 14

    # =====================================================
    # WEIGHT — DEDICATED ANALYSIS CONTRACT
    # =====================================================
    if show_weight:
        y = draw_weight_clinical_analysis(y, weight_analysis)

    # =====================================================
    # GLUCOSE — DEDICATED ANALYSIS CONTRACT
    # =====================================================
    if show_glucose:
        y = draw_glucose_clinical_analysis(y, glucose_analysis, days)

    # =====================================================
    # HISTORICAL VITALS TABLE
    # =====================================================
    pdf.showPage()
    y = height - 50
    pdf.setFont("Helvetica-Bold", 13)
    pdf.drawString(LEFT, y, f"Historical Vitals (Last {days} Days)")
    y -= 25

    history_columns = [
        ("Date", lambda v: v[0].strftime("%m/%d/%Y")),
        (
            "BP (mmHg)",
            lambda v: (
                f"{v[1]}/{v[2]}"
                if v[1] is not None and v[2] is not None
                else ""
            ),
        ),
    ]

    if show_hr:
        history_columns.append(("HR (BPM)", lambda v: str(v[3]) if v[3] is not None else ""))
    if show_spo2:
        history_columns.append(("SpO2 (%)", lambda v: str(v[4]) if v[4] is not None else ""))
    if show_temp:
        history_columns.append(("Temp (F)", lambda v: f"{float(v[5]):.1f}" if v[5] is not None else ""))
    if show_weight:
        history_columns.append(("Weight (lb)", lambda v: f"{float(v[6]):.1f}" if v[6] is not None else ""))
    if show_glucose:
        history_columns.append(("Glucose", lambda v: str(v[7]) if v[7] is not None else ""))

    date_width = 74
    remaining_width = USABLE_WIDTH - date_width
    other_count = max(1, len(history_columns) - 1)
    other_width = remaining_width / other_count
    history_widths = [date_width] + [other_width] * (len(history_columns) - 1)
    history_headers = [name for name, _ in history_columns]

    y = draw_table_row(
        y,
        history_headers,
        history_widths,
        fontsize=7,
        bold=True,
        fill_bg=True,
    )

    if history:
        for v in history:
            y = check_page_break(y, needed=40)
            y = draw_table_row(
                y,
                [formatter(v) for _, formatter in history_columns],
                history_widths,
                fontsize=7,
            )
    else:
        y -= 5
        pdf.setFont("Helvetica", 9)
        pdf.drawString(LEFT, y, f"No vitals recorded in the last {days} days.")

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
                                    heart_rate, temperature, temperature_site, blood_glucose,
                                    weight, source, notes)
                VALUES (%s,%s,COALESCE(%s, now()),%s,%s,%s,%s,%s,%s,%s,%s,%s,%s)
            """, (household_id, visit.patient_id, visit.visit_date,
                  visit.systolic, visit.diastolic, visit.oxygen_saturation,
                  visit.heart_rate, visit.temperature,
                  (visit.temperature_site or "unknown") if visit.temperature is not None else None,
                  visit.blood_glucose, visit.weight, "doctor_visit", visit.notes))
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
# PATIENT VITAL PREFERENCES
# =====================================================
# Optional-vital tracking belongs to the selected patient. These endpoints
# deliberately avoid a user_id query parameter: household auth + patient
# ownership are sufficient, and keeping patient settings on a patient route
# prevents a user-preference failure from silently blocking vital saves.
@app.get("/api/patients/{patient_id}/vital-preferences")
def get_patient_vital_preferences(
    patient_id: UUID,
    household_id: str = Depends(get_household_id),
):
    conn = get_conn()
    cur = conn.cursor()
    try:
        verify_patient_household(cur, str(patient_id), household_id)
        cur.execute("""
            SELECT show_heart_rate, show_spo2, show_temperature,
                   show_weight, show_glucose
            FROM patients
            WHERE patient_id = %s
              AND household_id = %s;
        """, (str(patient_id), household_id))
        row = cur.fetchone()

        if row is None:
            raise HTTPException(status_code=404, detail="Patient preferences not found")

        return {
            "patient_id": str(patient_id),
            "show_heart_rate": bool(row[0]),
            "show_spo2": bool(row[1]),
            "show_temperature": bool(row[2]),
            "show_weight": bool(row[3]),
            "show_glucose": bool(row[4]),
        }
    finally:
        cur.close()
        conn.close()


@app.patch("/api/patients/{patient_id}/vital-preferences")
def update_patient_vital_preferences(
    patient_id: UUID,
    payload: dict = Body(...),
    household_id: str = Depends(get_household_id),
):
    allowed = {
        "show_heart_rate",
        "show_spo2",
        "show_temperature",
        "show_weight",
        "show_glucose",
    }
    updates = {k: bool(v) for k, v in payload.items() if k in allowed}

    if not updates:
        raise HTTPException(status_code=400, detail="No valid vital preference fields to update")

    conn = get_conn()
    cur = conn.cursor()
    try:
        verify_patient_household(cur, str(patient_id), household_id)

        fields = ", ".join(f"{key} = %s" for key in updates)
        values = list(updates.values()) + [str(patient_id), household_id]
        cur.execute(f"""
            UPDATE patients
            SET {fields}
            WHERE patient_id = %s
              AND household_id = %s;
        """, values)

        if cur.rowcount == 0:
            raise HTTPException(status_code=404, detail="Patient not found")

        conn.commit()

        cur.execute("""
            SELECT show_heart_rate, show_spo2, show_temperature,
                   show_weight, show_glucose
            FROM patients
            WHERE patient_id = %s
              AND household_id = %s;
        """, (str(patient_id), household_id))
        row = cur.fetchone()

        return {
            "patient_id": str(patient_id),
            "show_heart_rate": bool(row[0]),
            "show_spo2": bool(row[1]),
            "show_temperature": bool(row[2]),
            "show_weight": bool(row[3]),
            "show_glucose": bool(row[4]),
        }
    except HTTPException:
        conn.rollback()
        raise
    except Exception:
        conn.rollback()
        raise
    finally:
        cur.close()
        conn.close()


# =====================================================
# USER + PATIENT PREFERENCES
# =====================================================
# Theme remains a signed-in USER preference. Optional-vital tracking is a
# PATIENT preference: two people in the same household can legitimately
# track different vitals. patient_id is optional only for backward
# compatibility with older mobile builds; new clients always send it.
@app.get("/api/user/preferences")
def get_user_preferences(
    user_id: str = Query(...),
    patient_id: Optional[str] = Query(None),
    household_id: str = Depends(get_household_id),
    caller_user_id: str = Depends(get_own_user_id)
):
    if caller_user_id is not None and caller_user_id != user_id:
        raise HTTPException(status_code=403, detail="Cannot access another user's preferences")

    conn = get_conn()
    cur = conn.cursor()
    try:
        cur.execute("""
            SELECT user_id, display_name, theme,
                   show_heart_rate, show_spo2, show_temperature,
                   show_weight, show_glucose
            FROM users
            WHERE user_id = %s AND household_id = %s;
        """, (user_id, household_id))
        user_row = cur.fetchone()

        if not user_row:
            raise HTTPException(status_code=404, detail="User not found")

        # New path: vital visibility/tracking comes from the selected patient.
        if patient_id:
            cur.execute("""
                SELECT show_heart_rate, show_spo2, show_temperature,
                       show_weight, show_glucose
                FROM patients
                WHERE patient_id = %s
                  AND household_id = %s;
            """, (patient_id, household_id))
            patient_row = cur.fetchone()

            if not patient_row:
                raise HTTPException(status_code=404, detail="Patient not found")

            vital_values = patient_row
        else:
            # Legacy fallback for an older app that has not yet been rebuilt.
            vital_values = user_row[3:8]

        return {
            "user_id":          str(user_row[0]),
            "patient_id":       patient_id,
            "display_name":     user_row[1],
            "theme":            user_row[2],
            "show_heart_rate":  vital_values[0],
            "show_spo2":        vital_values[1],
            "show_temperature": vital_values[2],
            "show_weight":      vital_values[3],
            "show_glucose":     vital_values[4],
        }
    finally:
        cur.close()
        conn.close()

@app.patch("/api/user/preferences")
def update_user_preferences(
    user_id: str = Query(...),
    patient_id: Optional[str] = Query(None),
    payload: dict = Body(...),
    household_id: str = Depends(get_household_id),
    caller_user_id: str = Depends(get_own_user_id)
):
    if caller_user_id is not None and caller_user_id != user_id:
        raise HTTPException(status_code=403, detail="Cannot modify another user's preferences")

    theme_present = "theme" in payload
    vital_keys = {
        "show_heart_rate", "show_spo2", "show_temperature",
        "show_weight", "show_glucose",
    }
    vital_updates = {k: v for k, v in payload.items() if k in vital_keys}

    if not theme_present and not vital_updates:
        raise HTTPException(status_code=400, detail="No valid fields to update")

    conn = get_conn()
    cur = conn.cursor()
    try:
        # Theme is still user-scoped.
        if theme_present:
            cur.execute("""
                UPDATE users
                SET theme = %s
                WHERE user_id = %s
                  AND household_id = %s;
            """, (payload["theme"], user_id, household_id))

            if cur.rowcount == 0:
                raise HTTPException(status_code=404, detail="User not found")

        if vital_updates:
            fields = ", ".join(f"{k} = %s" for k in vital_updates)

            if patient_id:
                # Patient-scoped path used by current mobile builds.
                values = list(vital_updates.values()) + [patient_id, household_id]
                cur.execute(f"""
                    UPDATE patients
                    SET {fields}
                    WHERE patient_id = %s
                      AND household_id = %s;
                """, values)

                if cur.rowcount == 0:
                    raise HTTPException(status_code=404, detail="Patient not found")
            else:
                # Backward-compatible path for older clients. This can be
                # removed after all supported builds send patient_id.
                values = list(vital_updates.values()) + [user_id, household_id]
                cur.execute(f"""
                    UPDATE users
                    SET {fields}
                    WHERE user_id = %s
                      AND household_id = %s;
                """, values)

                if cur.rowcount == 0:
                    raise HTTPException(status_code=404, detail="User not found")

        conn.commit()
        return {"status": "updated", "patient_id": patient_id}
    except HTTPException:
        conn.rollback()
        raise
    except Exception:
        conn.rollback()
        raise
    finally:
        cur.close()
        conn.close()

def verify_apple_identity_token(identity_token: str, raw_nonce: str) -> dict:
    """
    Verifies the native Sign in with Apple identity token against Apple's
    current public keys, our explicit app identifier, issuer, expiry, and
    the one-time nonce generated by the iOS client.

    The iOS request sends SHA-256(raw_nonce) to Apple. Apple copies that
    hashed value into the signed token's nonce claim; the raw value never
    leaves our app except over TLS to this endpoint, which lets the API
    reject replayed/stolen identity tokens.
    """
    try:
        header = jose_jwt.get_unverified_header(identity_token)
        kid = header.get("kid")
        alg = header.get("alg")

        if not kid or alg != "RS256":
            raise ValueError("Unexpected Apple token header")

        key_response = requests.get(APPLE_KEYS_URL, timeout=5)
        key_response.raise_for_status()
        keys = key_response.json().get("keys", [])
        apple_key = next((key for key in keys if key.get("kid") == kid), None)
        if apple_key is None:
            raise ValueError("Apple signing key not found")

        public_key = jwk.construct(apple_key, algorithm="RS256").to_pem()
        decoded = jose_jwt.decode(
            identity_token,
            public_key,
            algorithms=["RS256"],
            audience=APPLE_CLIENT_ID,
            issuer=APPLE_ISSUER,
        )

        expected_nonce = hashlib.sha256(raw_nonce.encode("utf-8")).hexdigest()
        token_nonce = decoded.get("nonce") or ""
        if not hmac.compare_digest(token_nonce, expected_nonce):
            raise ValueError("Apple nonce mismatch")

        return decoded
    except Exception as e:
        raise HTTPException(status_code=401, detail=f"Invalid Apple identity token: {e}")


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


@app.post("/api/auth/apple")
def auth_apple(body: AppleAuthRequest):
    decoded = verify_apple_identity_token(body.id_token, body.raw_nonce)

    apple_user_id = decoded.get("sub")
    email = (decoded.get("email") or "").strip().lower()
    provider = "apple.com"

    if not apple_user_id:
        raise HTTPException(status_code=401, detail="Apple identity token is missing a user id")

    # Vitals currently uses email for account contact, household invites,
    # and Settings display. Normal consumer Apple Accounts include either
    # the real address or Apple's private-relay address in the signed token.
    # Managed Apple Accounts can omit email; do not create a half-usable
    # Vitals account in that rare case.
    if not email:
        raise HTTPException(
            status_code=400,
            detail="Apple did not provide an email address for this account."
        )

    requested_display_name = (body.display_name or "").strip()
    fallback_display_name = email.split("@")[0] if "@" in email else "Apple User"
    display_name = requested_display_name or fallback_display_name

    conn = get_conn()
    cur = conn.cursor()

    try:
        # provider_user_id is used for Apple's stable per-developer user id.
        # If the same verified email already belongs to an existing Vitals
        # account (password or Google), attach the Apple id to that account
        # instead of silently creating a duplicate household/account.
        cur.execute("""
            SELECT user_id, household_id, auth_provider, display_name
            FROM users
            WHERE provider_user_id = %s OR email = %s
            LIMIT 1;
        """, (apple_user_id, email))
        existing = cur.fetchone()

        if existing:
            user_id = str(existing[0])
            household_id = str(existing[1]) if existing[1] else None
            auth_provider_out = existing[2]
            existing_display_name = existing[3]

            cur.execute("""
                UPDATE users
                SET provider_user_id = %s,
                    display_name = CASE
                        WHEN (display_name IS NULL OR btrim(display_name) = '')
                             AND %s <> ''
                        THEN %s
                        ELSE display_name
                    END,
                    last_seen_at = now(),
                    has_logged_in = true
                WHERE user_id = %s;
            """, (
                apple_user_id,
                requested_display_name,
                requested_display_name,
                user_id,
            ))

            if existing_display_name and str(existing_display_name).strip():
                display_name = str(existing_display_name)
        else:
            cur.execute("""
                INSERT INTO users (
                    household_id, email, display_name,
                    auth_provider, provider_user_id,
                    email_verified, subscription_status,
                    last_seen_at, has_logged_in
                )
                VALUES (
                    NULL, %s, %s,
                    %s, %s,
                    true, 'trial',
                    now(), true
                )
                RETURNING user_id;
            """, (
                email,
                display_name,
                provider,
                apple_user_id,
            ))
            user_id = str(cur.fetchone()[0])
            household_id = None
            auth_provider_out = provider

        conn.commit()

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
    except HTTPException:
        conn.rollback()
        raise
    except Exception as e:
        conn.rollback()
        raise HTTPException(status_code=500, detail=f"Apple auth error: {e}")
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
        require_household_management(cur, household_id, inviter_user_id)

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
        if (
            patient_limit is not None
            and patient_count + pending_invite_count >= patient_limit
        ):
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
    entitlement = get_household_entitlement(cur, household_id, auth.get("sub"))
    cur.close()
    conn.close()

    is_unlimited = patient_limit is None
    available_slots = (
        None
        if is_unlimited
        else max(0, patient_limit - patient_count - pending_invite_count)
    )

    has_capacity = is_unlimited or available_slots > 0

    return {
        "patient_limit": patient_limit,
        "patient_count": patient_count,
        "pending_invite_count": pending_invite_count,
        "available_slots": available_slots,
        "can_invite": entitlement["can_manage_household"] and has_capacity,
        "can_manage_household": entitlement["can_manage_household"],
        "household_role": entitlement["household_role"],
        "delegated_management_allowed": entitlement["delegated_management_allowed"],
        "is_unlimited": is_unlimited,
        "plan": entitlement["plan"],
        "effective_plan": entitlement["effective_plan"],
        "access_state": entitlement["access_state"],
    }


@app.get("/api/household/entitlement")
def get_household_entitlement_status(
    household_id: str = Depends(get_household_id),
    auth: dict = Depends(get_auth),
):
    """
    Returns the single household-level commercial entitlement snapshot used
    by Phase 7 clients. Over-capacity downgrades now use this state to require
    an explicit active-patient selection before patient-scoped access resumes.
    """
    if auth.get("type") == "api_key":
        raise HTTPException(status_code=401, detail="This requires a signed-in account")

    conn = get_conn()
    cur = conn.cursor()
    try:
        return get_household_entitlement(cur, household_id, auth.get("sub"))
    finally:
        cur.close()
        conn.close()


@app.get("/api/billing/catalog", response_model=BillingCatalogResponse)
def get_billing_catalog(
    provider: Literal["apple", "google"] = Query(...),
    household_id: str = Depends(get_household_id),
    auth: dict = Depends(get_auth),
):
    """
    Returns the store identifiers the signed-in mobile client should use when
    requesting localized subscription products from StoreKit / Google Play.

    This endpoint deliberately exposes no price values and performs no
    purchase mutation. Store verification and entitlement mutation are added
    in the next 0.7.2 slice.
    """
    if auth.get("type") == "api_key":
        raise HTTPException(status_code=401, detail="This requires a signed-in account")

    # get_household_id already validates that the JWT belongs to a household.
    # Keep the dependency here even though the catalog itself is global so
    # unaffiliated/legacy callers cannot use a mobile billing endpoint.
    _ = household_id
    user_id = auth.get("sub")
    if not user_id:
        raise HTTPException(status_code=401, detail="Token missing user id")

    catalog = get_billing_product_catalog(provider)
    catalog["account_binding_token"] = _billing_account_binding_token(
        provider,
        user_id,
    )
    return catalog


@app.post("/api/billing/verify", response_model=BillingVerifyResponse)
def verify_billing_purchase(
    body: BillingVerifyRequest,
    household_id: str = Depends(get_household_id),
    auth: dict = Depends(get_auth),
):
    """
    Server-authoritative purchase verification.

    The client supplies only the provider's opaque purchase reference:
      Apple  -> StoreKit transactionId
      Google -> Play Billing purchaseToken

    Vitals never trusts a client-supplied plan, price, expiration, household
    id, or entitlement. The server resolves the purchase directly with Apple
    or Google, validates its Vitals account binding, maps the verified product
    through the configured catalog, persists the store subscription, and only
    then mutates household entitlement.
    """
    if auth.get("type") == "api_key":
        raise HTTPException(status_code=401, detail="This requires a signed-in account")

    user_id = auth.get("sub")
    if not user_id:
        raise HTTPException(status_code=401, detail="Token missing user id")

    # Permission is checked before the external store call so unauthorized
    # members cannot use Vitals as an arbitrary store-verification proxy.
    conn = get_conn()
    cur = conn.cursor()
    try:
        _require_billing_claim_permission(
            cur,
            household_id,
            user_id,
            body.provider,
        )
    finally:
        cur.close()
        conn.close()

    verified = _verify_store_purchase(body, user_id)

    # Re-check permission inside the write transaction because household role,
    # billing ownership, or provider state may have changed during the network
    # round trip to Apple/Google.
    conn = get_conn()
    cur = conn.cursor()
    try:
        _require_billing_claim_permission(
            cur,
            household_id,
            user_id,
            body.provider,
        )

        prior_paid, prior_expires_at = _prepare_billing_subscription_upsert(
            cur,
            household_id,
            verified,
        )

        billing_subscription_id = _upsert_billing_subscription(
            cur,
            household_id,
            user_id,
            verified,
        )

        entitlement_changed = _apply_verified_billing_entitlement(
            cur,
            household_id,
            user_id,
            verified,
            prior_paid,
            prior_expires_at,
        )

        entitlement = get_household_entitlement(
            cur,
            household_id,
            user_id,
        )
        conn.commit()

        return {
            "verified": True,
            "entitlement_changed": entitlement_changed,
            "provider": verified["provider"],
            "plan": verified["plan"],
            "billing_period": verified["billing_period"],
            "product_id": verified["product_id"],
            "base_plan_id": verified.get("base_plan_id"),
            "store_status": verified["store_status"],
            "auto_renew_enabled": verified.get("auto_renew_enabled"),
            "expires_at": verified.get("expires_at"),
            "billing_subscription_id": billing_subscription_id,
            "entitlement": entitlement,
        }
    except HTTPException:
        conn.rollback()
        raise
    except psycopg2.IntegrityError:
        conn.rollback()
        raise HTTPException(
            status_code=409,
            detail="This store subscription is already linked to another Vitals household."
        )
    except Exception:
        conn.rollback()
        raise
    finally:
        cur.close()
        conn.close()


@app.post("/api/household/patient-access")
def select_household_patient_access(
    body: PatientAccessSelectionRequest,
    household_id: str = Depends(get_household_id),
    auth: dict = Depends(get_auth),
):
    """
    Chooses which patient profiles remain active when a finite plan has fewer
    slots than the household already contains. No patient or clinical data is
    deleted.
    """
    if auth.get("type") == "api_key":
        raise HTTPException(status_code=401, detail="This requires a signed-in account")

    user_id = auth.get("sub")
    conn = get_conn()
    cur = conn.cursor()
    try:
        entitlement = require_household_management(
            cur, household_id, user_id
        )

        patient_limit = entitlement["patient_limit"]
        patient_count = entitlement["patient_count"]
        if patient_limit is None or patient_count <= patient_limit:
            raise HTTPException(
                status_code=400,
                detail="This household does not currently require a patient selection."
            )

        selected_ids = list(dict.fromkeys(
            (patient_id or "").strip()
            for patient_id in body.patient_ids
            if (patient_id or "").strip()
        ))

        if len(selected_ids) != patient_limit:
            raise HTTPException(
                status_code=400,
                detail=f"Choose exactly {patient_limit} patients to keep active."
            )

        try:
            selected_ids = [str(UUID(patient_id)) for patient_id in selected_ids]
        except Exception:
            raise HTTPException(status_code=400, detail="One or more patient ids are invalid.")

        cur.execute(
            """
            SELECT COUNT(*)
            FROM patients
            WHERE household_id = %s
              AND patient_id = ANY(%s::uuid[])
            """,
            (household_id, selected_ids),
        )
        if cur.fetchone()[0] != len(selected_ids):
            raise HTTPException(
                status_code=400,
                detail="One or more selected patients do not belong to this household."
            )

        cur.execute(
            """
            UPDATE patients
            SET entitlement_locked = NOT (patient_id = ANY(%s::uuid[])),
                entitlement_locked_at = CASE
                    WHEN patient_id = ANY(%s::uuid[]) THEN NULL
                    ELSE COALESCE(entitlement_locked_at, now())
                END
            WHERE household_id = %s
            """,
            (selected_ids, selected_ids, household_id),
        )
        conn.commit()

        refreshed = get_household_entitlement(cur, household_id, user_id)
        return {
            "status": "updated",
            "selected_patient_ids": selected_ids,
            "entitlement": refreshed,
        }
    except HTTPException:
        conn.rollback()
        raise
    except Exception as e:
        conn.rollback()
        raise HTTPException(status_code=500, detail=f"Patient access update failed: {e}")
    finally:
        cur.close()
        conn.close()


@app.get("/api/household/invites")
def list_household_invites(
    household_id: str = Depends(get_household_id),
    auth: dict = Depends(get_auth),
):
    """
    Lists pending household invites for authorized household administrators.
    """
    if auth.get("type") == "api_key":
        raise HTTPException(status_code=401, detail="Household invites require a signed-in account")

    conn = get_conn()
    cur = conn.cursor()
    try:
        require_household_management(cur, household_id, auth.get("sub"))

        cur.execute("""
            SELECT invite_id, invited_email, created_at, expires_at
            FROM household_invites
            WHERE household_id = %s AND used_at IS NULL AND expires_at > now()
            ORDER BY created_at DESC;
        """, (household_id,))
        rows = cur.fetchall()
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
    finally:
        cur.close()
        conn.close()


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
        require_household_management(cur, household_id, auth.get("sub"))

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
    Creates the household and attaches it to the caller.

    Phase 7 changes the commercial model: every newly-created household
    receives one ungated 30-day full-access trial with capacity for up to
    five patients, regardless of whether the user expresses Standard or
    Family intent today. Choosing "trial" means "decide later" — it does
    not create a different or reduced trial.

    The tier column is plan intent during the trial. subscription_status
    remains the access-state source of truth and is set to "trial".
    """
    if auth.get("type") == "api_key":
        raise HTTPException(status_code=401, detail="This requires a signed-in account")

    requested_tier = (body.tier or "").strip().lower()
    normalized_tier = {
        "individual": "standard",  # backward compatibility with alpha 0.69
        "free": "trial",           # backward compatibility with alpha 0.69
    }.get(requested_tier, requested_tier)

    if normalized_tier not in ("standard", "family", "trial"):
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

        # All Phase 7 trials expose the full product and therefore allow up
        # to five patients. Paid/Basic limits are enforced only when the
        # household leaves the trial state.
        patient_limit = 5

        household_name = f"{display_name}'s Household" if display_name else "New Household"
        cur.execute("""
            INSERT INTO households (
                name, tier, subscription_status,
                trial_started_at, trial_ends_at,
                patient_limit, owner_user_id,
                entitlement_updated_at, created_at
            )
            VALUES (
                %s, %s, 'trial',
                now(), now() + interval '30 days',
                %s, %s, now(), now()
            )
            RETURNING household_id, trial_ends_at;
        """, (household_name, normalized_tier, patient_limit, user_id))
        created = cur.fetchone()
        household_id = str(created[0])
        trial_ends_at = created[1]

        cur.execute("""
            UPDATE users
            SET household_id = %s,
                household_role = 'owner'
            WHERE user_id = %s
        """, (household_id, user_id))
        conn.commit()

        token = create_jwt(user_id, household_id, email)
        return {
            "status": "created",
            "household_id": household_id,
            "tier": normalized_tier,
            "subscription_status": "trial",
            "trial_ends_at": trial_ends_at,
            "patient_limit": patient_limit,
            "token": token,
        }
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

        cur.execute("""
            UPDATE users
            SET household_id = %s,
                household_role = 'member'
            WHERE user_id = %s
        """, (household_id, user_id))
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
        entitlement = get_household_entitlement(cur, household_id, user_id)
        patient_limit = entitlement["patient_limit"]
        current_count = entitlement["patient_count"]
        can_create_new_patient = (
            patient_limit is None or current_count < patient_limit
        )

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
            other = (
                "Google" if auth_provider == "google.com"
                else "Apple" if auth_provider == "apple.com"
                else "a different sign-in method"
            )
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