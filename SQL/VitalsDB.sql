-- Table: public.allergies

-- DROP TABLE IF EXISTS public.allergies;

CREATE TABLE IF NOT EXISTS public.allergies
(
    allergy_id uuid NOT NULL DEFAULT gen_random_uuid(),
    patient_id uuid NOT NULL,
    household_id uuid NOT NULL,
    allergen text COLLATE pg_catalog."default" NOT NULL,
    allergy_type text COLLATE pg_catalog."default" NOT NULL,
    reaction text COLLATE pg_catalog."default",
    severity text COLLATE pg_catalog."default",
    notes text COLLATE pg_catalog."default",
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT allergies_pkey PRIMARY KEY (allergy_id),
    CONSTRAINT allergies_patient_id_fkey FOREIGN KEY (patient_id)
        REFERENCES public.patients (patient_id) MATCH SIMPLE
        ON UPDATE NO ACTION
        ON DELETE NO ACTION,
    CONSTRAINT allergies_allergy_type_check CHECK (allergy_type = ANY (ARRAY['medication'::text, 'food'::text, 'environmental'::text, 'other'::text])),
    CONSTRAINT allergies_severity_check CHECK (severity = ANY (ARRAY['mild'::text, 'moderate'::text, 'severe'::text]))
)

TABLESPACE pg_default;

ALTER TABLE IF EXISTS public.allergies
    OWNER to postgres;

GRANT ALL ON TABLE public.allergies TO postgres;

GRANT ALL ON TABLE public.allergies TO vitals_user;
-- Index: idx_allergies_household_id

-- DROP INDEX IF EXISTS public.idx_allergies_household_id;

CREATE INDEX IF NOT EXISTS idx_allergies_household_id
    ON public.allergies USING btree
    (household_id ASC NULLS LAST)
    TABLESPACE pg_default;
-- Index: idx_allergies_patient_id

-- DROP INDEX IF EXISTS public.idx_allergies_patient_id;

CREATE INDEX IF NOT EXISTS idx_allergies_patient_id
    ON public.allergies USING btree
    (patient_id ASC NULLS LAST)
    TABLESPACE pg_default;

-- Table: public.doctor_visits

-- DROP TABLE IF EXISTS public.doctor_visits;

CREATE TABLE IF NOT EXISTS public.doctor_visits
(
    visit_id uuid NOT NULL DEFAULT gen_random_uuid(),
    patient_id uuid,
    visit_date date NOT NULL,
    doctor_name text COLLATE pg_catalog."default",
    reason text COLLATE pg_catalog."default",
    notes text COLLATE pg_catalog."default",
    created_at timestamp with time zone DEFAULT now(),
    household_id uuid NOT NULL,
    CONSTRAINT doctor_visits_pkey PRIMARY KEY (visit_id),
    CONSTRAINT doctor_visits_patient_id_fkey FOREIGN KEY (patient_id)
        REFERENCES public.patients (patient_id) MATCH SIMPLE
        ON UPDATE NO ACTION
        ON DELETE NO ACTION,
    CONSTRAINT fk_visits_household FOREIGN KEY (household_id)
        REFERENCES public.households (household_id) MATCH SIMPLE
        ON UPDATE NO ACTION
        ON DELETE NO ACTION
)

TABLESPACE pg_default;

ALTER TABLE IF EXISTS public.doctor_visits
    OWNER to postgres;

GRANT ALL ON TABLE public.doctor_visits TO postgres;

GRANT ALL ON TABLE public.doctor_visits TO vitals_user;

-- Table: public.patient_doctors

-- DROP TABLE IF EXISTS public.patient_doctors;

CREATE TABLE IF NOT EXISTS public.patient_doctors
(
    patient_id uuid NOT NULL,
    doctor_id uuid NOT NULL,
    is_primary boolean DEFAULT false,
    relationship_notes text COLLATE pg_catalog."default",
    created_at timestamp with time zone DEFAULT now(),
    CONSTRAINT patient_doctors_pkey PRIMARY KEY (patient_id, doctor_id),
    CONSTRAINT patient_doctors_doctor_id_fkey FOREIGN KEY (doctor_id)
        REFERENCES public.doctors (doctor_id) MATCH SIMPLE
        ON UPDATE NO ACTION
        ON DELETE CASCADE,
    CONSTRAINT patient_doctors_patient_id_fkey FOREIGN KEY (patient_id)
        REFERENCES public.patients (patient_id) MATCH SIMPLE
        ON UPDATE NO ACTION
        ON DELETE CASCADE
)

TABLESPACE pg_default;

ALTER TABLE IF EXISTS public.patient_doctors
    OWNER to postgres;
-- Index: unique_primary_per_patient

-- DROP INDEX IF EXISTS public.unique_primary_per_patient;

CREATE UNIQUE INDEX IF NOT EXISTS unique_primary_per_patient
    ON public.patient_doctors USING btree
    (patient_id ASC NULLS LAST)
    TABLESPACE pg_default
    WHERE is_primary = true;


-- Table: public.heart_rate_context

-- DROP TABLE IF EXISTS public.heart_rate_context;

CREATE TABLE IF NOT EXISTS public.heart_rate_context
(
    vital_id uuid NOT NULL,
    activity_context text COLLATE pg_catalog."default",
    posture text COLLATE pg_catalog."default",
    symptom_tags text[] COLLATE pg_catalog."default",
    source_type text COLLATE pg_catalog."default",
    device_irregular_pulse_flag boolean,
    is_invalidated boolean NOT NULL DEFAULT false,
    notes text COLLATE pg_catalog."default",
    CONSTRAINT heart_rate_context_pkey PRIMARY KEY (vital_id),
    CONSTRAINT heart_rate_context_vital_id_fkey FOREIGN KEY (vital_id)
        REFERENCES public.vitals (vital_id) MATCH SIMPLE
        ON UPDATE NO ACTION
        ON DELETE NO ACTION
)

TABLESPACE pg_default;

ALTER TABLE IF EXISTS public.heart_rate_context
    OWNER to bcbauser;

REVOKE ALL ON TABLE public.heart_rate_context FROM vitals_user;

GRANT ALL ON TABLE public.heart_rate_context TO bcbauser;

GRANT INSERT, DELETE, SELECT, UPDATE ON TABLE public.heart_rate_context TO vitals_user;

-- Table: public.household_invites

-- DROP TABLE IF EXISTS public.household_invites;

CREATE TABLE IF NOT EXISTS public.household_invites
(
    invite_id uuid NOT NULL DEFAULT gen_random_uuid(),
    household_id uuid NOT NULL,
    code text COLLATE pg_catalog."default" NOT NULL,
    invited_email text COLLATE pg_catalog."default" NOT NULL,
    created_by uuid NOT NULL,
    expires_at timestamp with time zone NOT NULL,
    used_at timestamp with time zone,
    created_at timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT household_invites_pkey PRIMARY KEY (invite_id),
    CONSTRAINT household_invites_code_key UNIQUE (code),
    CONSTRAINT household_invites_created_by_fkey FOREIGN KEY (created_by)
        REFERENCES public.users (user_id) MATCH SIMPLE
        ON UPDATE NO ACTION
        ON DELETE NO ACTION,
    CONSTRAINT household_invites_household_id_fkey FOREIGN KEY (household_id)
        REFERENCES public.households (household_id) MATCH SIMPLE
        ON UPDATE NO ACTION
        ON DELETE NO ACTION
)

TABLESPACE pg_default;

ALTER TABLE IF EXISTS public.household_invites
    OWNER to bcbauser;

REVOKE ALL ON TABLE public.household_invites FROM vitals_user;

GRANT ALL ON TABLE public.household_invites TO bcbauser;

GRANT INSERT, DELETE, SELECT, UPDATE ON TABLE public.household_invites TO vitals_user;

-- Table: public.households

-- DROP TABLE IF EXISTS public.households;

CREATE TABLE IF NOT EXISTS public.households
(
    household_id uuid NOT NULL DEFAULT gen_random_uuid(),
    name text COLLATE pg_catalog."default" NOT NULL,
    created_at timestamp with time zone DEFAULT now(),
    tier text COLLATE pg_catalog."default",
    subscription_status text COLLATE pg_catalog."default" NOT NULL DEFAULT 'trial'::text,
    trial_started_at timestamp with time zone,
    patient_limit integer NOT NULL DEFAULT 2,
    CONSTRAINT households_pkey PRIMARY KEY (household_id)
)

TABLESPACE pg_default;

ALTER TABLE IF EXISTS public.households
    OWNER to postgres;

GRANT ALL ON TABLE public.households TO postgres;

GRANT ALL ON TABLE public.households TO vitals_user;

-- Table: public.incident_logs

-- DROP TABLE IF EXISTS public.incident_logs;

CREATE TABLE IF NOT EXISTS public.incident_logs
(
    incident_id uuid NOT NULL DEFAULT gen_random_uuid(),
    patient_id uuid NOT NULL,
    household_id uuid NOT NULL,
    incident_date timestamp with time zone NOT NULL DEFAULT now(),
    severity text COLLATE pg_catalog."default" NOT NULL DEFAULT 'medium'::text,
    incident_type text COLLATE pg_catalog."default",
    location text COLLATE pg_catalog."default",
    description text COLLATE pg_catalog."default",
    outcome text COLLATE pg_catalog."default",
    follow_up_needed boolean DEFAULT false,
    follow_up_notes text COLLATE pg_catalog."default",
    created_at timestamp with time zone DEFAULT now(),
    updated_at timestamp with time zone DEFAULT now(),
    is_active boolean NOT NULL DEFAULT true,
    CONSTRAINT incident_logs_pkey PRIMARY KEY (incident_id),
    CONSTRAINT fk_incident_household FOREIGN KEY (household_id)
        REFERENCES public.households (household_id) MATCH SIMPLE
        ON UPDATE NO ACTION
        ON DELETE CASCADE,
    CONSTRAINT fk_incident_patient FOREIGN KEY (patient_id)
        REFERENCES public.patients (patient_id) MATCH SIMPLE
        ON UPDATE NO ACTION
        ON DELETE CASCADE,
    CONSTRAINT chk_severity CHECK (severity = ANY (ARRAY['low'::text, 'medium'::text, 'high'::text, 'critical'::text]))
)

TABLESPACE pg_default;

ALTER TABLE IF EXISTS public.incident_logs
    OWNER to postgres;

GRANT ALL ON TABLE public.incident_logs TO postgres;

GRANT ALL ON TABLE public.incident_logs TO vitals_user;

-- Table: public.incidents

-- DROP TABLE IF EXISTS public.incidents;

CREATE TABLE IF NOT EXISTS public.incidents
(
    incident_id uuid NOT NULL DEFAULT gen_random_uuid(),
    patient_id uuid,
    reported_by uuid,
    incident_time timestamp with time zone NOT NULL,
    description text COLLATE pg_catalog."default" NOT NULL,
    severity text COLLATE pg_catalog."default",
    created_at timestamp with time zone DEFAULT now(),
    household_id uuid NOT NULL,
    CONSTRAINT incidents_pkey PRIMARY KEY (incident_id),
    CONSTRAINT fk_incidents_household FOREIGN KEY (household_id)
        REFERENCES public.households (household_id) MATCH SIMPLE
        ON UPDATE NO ACTION
        ON DELETE NO ACTION,
    CONSTRAINT incidents_patient_id_fkey FOREIGN KEY (patient_id)
        REFERENCES public.patients (patient_id) MATCH SIMPLE
        ON UPDATE NO ACTION
        ON DELETE NO ACTION
)

TABLESPACE pg_default;

ALTER TABLE IF EXISTS public.incidents
    OWNER to postgres;

GRANT ALL ON TABLE public.incidents TO postgres;

GRANT ALL ON TABLE public.incidents TO vitals_user;

-- Table: public.medication_changes

-- DROP TABLE IF EXISTS public.medication_changes;

CREATE TABLE IF NOT EXISTS public.medication_changes
(
    change_id uuid NOT NULL DEFAULT gen_random_uuid(),
    medication_id uuid NOT NULL,
    patient_id uuid NOT NULL,
    household_id uuid NOT NULL,
    change_type text COLLATE pg_catalog."default" NOT NULL,
    field_changed text COLLATE pg_catalog."default",
    old_value text COLLATE pg_catalog."default",
    new_value text COLLATE pg_catalog."default",
    effective_date date NOT NULL,
    logged_at timestamp with time zone NOT NULL DEFAULT now(),
    changed_by_user_id uuid,
    notes text COLLATE pg_catalog."default",
    CONSTRAINT medication_changes_pkey PRIMARY KEY (change_id),
    CONSTRAINT medication_changes_changed_by_user_id_fkey FOREIGN KEY (changed_by_user_id)
        REFERENCES public.users (user_id) MATCH SIMPLE
        ON UPDATE NO ACTION
        ON DELETE NO ACTION,
    CONSTRAINT medication_changes_household_id_fkey FOREIGN KEY (household_id)
        REFERENCES public.households (household_id) MATCH SIMPLE
        ON UPDATE NO ACTION
        ON DELETE NO ACTION,
    CONSTRAINT medication_changes_medication_id_fkey FOREIGN KEY (medication_id)
        REFERENCES public.medications (medication_id) MATCH SIMPLE
        ON UPDATE NO ACTION
        ON DELETE NO ACTION,
    CONSTRAINT medication_changes_patient_id_fkey FOREIGN KEY (patient_id)
        REFERENCES public.patients (patient_id) MATCH SIMPLE
        ON UPDATE NO ACTION
        ON DELETE NO ACTION
)

TABLESPACE pg_default;

ALTER TABLE IF EXISTS public.medication_changes
    OWNER to bcbauser;

REVOKE ALL ON TABLE public.medication_changes FROM vitals_user;

GRANT ALL ON TABLE public.medication_changes TO bcbauser;

GRANT INSERT, DELETE, SELECT, UPDATE ON TABLE public.medication_changes TO vitals_user;

-- Table: public.medication_logs

-- DROP TABLE IF EXISTS public.medication_logs;

CREATE TABLE IF NOT EXISTS public.medication_logs
(
    medication_log_id uuid NOT NULL DEFAULT gen_random_uuid(),
    medication_id uuid,
    administered_by uuid,
    administered_at timestamp with time zone NOT NULL,
    notes text COLLATE pg_catalog."default",
    created_at timestamp with time zone DEFAULT now(),
    household_id uuid NOT NULL,
    CONSTRAINT medication_logs_pkey PRIMARY KEY (medication_log_id),
    CONSTRAINT fk_medlogs_household FOREIGN KEY (household_id)
        REFERENCES public.households (household_id) MATCH SIMPLE
        ON UPDATE NO ACTION
        ON DELETE NO ACTION,
    CONSTRAINT medication_logs_medication_id_fkey FOREIGN KEY (medication_id)
        REFERENCES public.medications (medication_id) MATCH SIMPLE
        ON UPDATE NO ACTION
        ON DELETE NO ACTION
)

TABLESPACE pg_default;

ALTER TABLE IF EXISTS public.medication_logs
    OWNER to postgres;

GRANT ALL ON TABLE public.medication_logs TO postgres;

GRANT ALL ON TABLE public.medication_logs TO vitals_user;

-- Table: public.medications

-- DROP TABLE IF EXISTS public.medications;

CREATE TABLE IF NOT EXISTS public.medications
(
    medication_id uuid NOT NULL DEFAULT gen_random_uuid(),
    patient_id uuid,
    name text COLLATE pg_catalog."default" NOT NULL,
    dosage text COLLATE pg_catalog."default",
    schedule text COLLATE pg_catalog."default",
    prescribing_doctor text COLLATE pg_catalog."default",
    created_at timestamp with time zone DEFAULT now(),
    household_id uuid NOT NULL,
    qty integer,
    days_supply integer,
    fill_date date,
    discontinued boolean DEFAULT false,
    last_taken_at timestamp with time zone,
    est_refill date,
    time_of_day text[] COLLATE pg_catalog."default",
    is_active boolean NOT NULL DEFAULT true,
    rxotc text COLLATE pg_catalog."default" NOT NULL DEFAULT 'rx'::text,
    purpose text COLLATE pg_catalog."default",
    prescribing_doctor_id uuid,
    start_date date,
    discontinued_date date,
    CONSTRAINT medications_pkey PRIMARY KEY (medication_id),
    CONSTRAINT fk_medications_household FOREIGN KEY (household_id)
        REFERENCES public.households (household_id) MATCH SIMPLE
        ON UPDATE NO ACTION
        ON DELETE NO ACTION,
    CONSTRAINT fk_prescribing_doctor FOREIGN KEY (prescribing_doctor_id)
        REFERENCES public.doctors (doctor_id) MATCH SIMPLE
        ON UPDATE NO ACTION
        ON DELETE SET NULL,
    CONSTRAINT medications_patient_id_fkey FOREIGN KEY (patient_id)
        REFERENCES public.patients (patient_id) MATCH SIMPLE
        ON UPDATE NO ACTION
        ON DELETE NO ACTION,
    CONSTRAINT medications_rxotc_check CHECK (rxotc = ANY (ARRAY['rx'::text, 'otc'::text]))
)

TABLESPACE pg_default;

ALTER TABLE IF EXISTS public.medications
    OWNER to postgres;

GRANT ALL ON TABLE public.medications TO postgres;

GRANT ALL ON TABLE public.medications TO vitals_user;

-- Table: public.patient_doctors

-- DROP TABLE IF EXISTS public.patient_doctors;

CREATE TABLE IF NOT EXISTS public.patient_doctors
(
    patient_id uuid NOT NULL,
    doctor_id uuid NOT NULL,
    is_primary boolean DEFAULT false,
    relationship_notes text COLLATE pg_catalog."default",
    created_at timestamp with time zone DEFAULT now(),
    CONSTRAINT patient_doctors_pkey PRIMARY KEY (patient_id, doctor_id),
    CONSTRAINT patient_doctors_doctor_id_fkey FOREIGN KEY (doctor_id)
        REFERENCES public.doctors (doctor_id) MATCH SIMPLE
        ON UPDATE NO ACTION
        ON DELETE CASCADE,
    CONSTRAINT patient_doctors_patient_id_fkey FOREIGN KEY (patient_id)
        REFERENCES public.patients (patient_id) MATCH SIMPLE
        ON UPDATE NO ACTION
        ON DELETE CASCADE
)

TABLESPACE pg_default;

ALTER TABLE IF EXISTS public.patient_doctors
    OWNER to postgres;

GRANT ALL ON TABLE public.patient_doctors TO postgres;

GRANT ALL ON TABLE public.patient_doctors TO vitals_user;
-- Index: unique_primary_per_patient

-- DROP INDEX IF EXISTS public.unique_primary_per_patient;

CREATE UNIQUE INDEX IF NOT EXISTS unique_primary_per_patient
    ON public.patient_doctors USING btree
    (patient_id ASC NULLS LAST)
    TABLESPACE pg_default
    WHERE is_primary = true;

-- Table: public.patient_notes

-- DROP TABLE IF EXISTS public.patient_notes;

CREATE TABLE IF NOT EXISTS public.patient_notes
(
    note_id uuid NOT NULL DEFAULT gen_random_uuid(),
    patient_id uuid NOT NULL,
    household_id uuid NOT NULL,
    note_type text COLLATE pg_catalog."default" NOT NULL DEFAULT 'general'::text,
    title text COLLATE pg_catalog."default",
    body text COLLATE pg_catalog."default",
    created_at timestamp with time zone DEFAULT now(),
    updated_at timestamp with time zone DEFAULT now(),
    is_active boolean NOT NULL DEFAULT true,
    CONSTRAINT patient_notes_pkey PRIMARY KEY (note_id),
    CONSTRAINT fk_note_household FOREIGN KEY (household_id)
        REFERENCES public.households (household_id) MATCH SIMPLE
        ON UPDATE NO ACTION
        ON DELETE CASCADE,
    CONSTRAINT fk_note_patient FOREIGN KEY (patient_id)
        REFERENCES public.patients (patient_id) MATCH SIMPLE
        ON UPDATE NO ACTION
        ON DELETE CASCADE,
    CONSTRAINT chk_note_type CHECK (note_type = ANY (ARRAY['general'::text, 'medication_change'::text, 'behavioral_observation'::text, 'caregiver_handoff'::text, 'family_communication'::text]))
)

TABLESPACE pg_default;

ALTER TABLE IF EXISTS public.patient_notes
    OWNER to postgres;

GRANT ALL ON TABLE public.patient_notes TO postgres;

GRANT ALL ON TABLE public.patient_notes TO vitals_user;

-- Table: public.patient_users

-- DROP TABLE IF EXISTS public.patient_users;

CREATE TABLE IF NOT EXISTS public.patient_users
(
    patient_user_id uuid NOT NULL DEFAULT gen_random_uuid(),
    patient_id uuid,
    user_id uuid,
    relationship text COLLATE pg_catalog."default",
    created_at timestamp with time zone DEFAULT now(),
    CONSTRAINT patient_users_pkey PRIMARY KEY (patient_user_id),
    CONSTRAINT patient_users_patient_id_fkey FOREIGN KEY (patient_id)
        REFERENCES public.patients (patient_id) MATCH SIMPLE
        ON UPDATE NO ACTION
        ON DELETE NO ACTION
)

TABLESPACE pg_default;

ALTER TABLE IF EXISTS public.patient_users
    OWNER to postgres;

GRANT ALL ON TABLE public.patient_users TO postgres;

GRANT ALL ON TABLE public.patient_users TO vitals_user;

-- Table: public.patients

-- DROP TABLE IF EXISTS public.patients;

CREATE TABLE IF NOT EXISTS public.patients
(
    patient_id uuid NOT NULL DEFAULT gen_random_uuid(),
    first_name text COLLATE pg_catalog."default" NOT NULL,
    last_name text COLLATE pg_catalog."default" NOT NULL,
    dob date,
    gender text COLLATE pg_catalog."default",
    created_at timestamp with time zone DEFAULT now(),
    household_id uuid NOT NULL,
    CONSTRAINT patients_pkey PRIMARY KEY (patient_id),
    CONSTRAINT fk_patients_household FOREIGN KEY (household_id)
        REFERENCES public.households (household_id) MATCH SIMPLE
        ON UPDATE NO ACTION
        ON DELETE NO ACTION
)

TABLESPACE pg_default;

ALTER TABLE IF EXISTS public.patients
    OWNER to postgres;

GRANT ALL ON TABLE public.patients TO postgres;

GRANT ALL ON TABLE public.patients TO vitals_user;

-- Table: public.users

-- DROP TABLE IF EXISTS public.users;

CREATE TABLE IF NOT EXISTS public.users
(
    user_id uuid NOT NULL DEFAULT gen_random_uuid(),
    household_id uuid,
    email text COLLATE pg_catalog."default",
    display_name text COLLATE pg_catalog."default",
    avatar_url text COLLATE pg_catalog."default",
    created_at timestamp with time zone DEFAULT now(),
    last_seen_at timestamp with time zone,
    auth_provider text COLLATE pg_catalog."default" DEFAULT 'email'::text,
    provider_user_id text COLLATE pg_catalog."default",
    stripe_customer_id text COLLATE pg_catalog."default",
    subscription_status text COLLATE pg_catalog."default" DEFAULT 'trial'::text,
    subscription_ends_at timestamp with time zone,
    theme text COLLATE pg_catalog."default" DEFAULT 'dark'::text,
    show_heart_rate boolean DEFAULT true,
    show_spo2 boolean DEFAULT true,
    show_temperature boolean DEFAULT true,
    show_weight boolean DEFAULT false,
    show_glucose boolean DEFAULT false,
    firebase_uid text COLLATE pg_catalog."default",
    password_hash text COLLATE pg_catalog."default",
    email_verified boolean NOT NULL DEFAULT false,
    verification_token text COLLATE pg_catalog."default",
    verification_token_expires_at timestamp with time zone,
    has_logged_in boolean NOT NULL DEFAULT false,
    CONSTRAINT users_pkey PRIMARY KEY (user_id),
    CONSTRAINT users_email_key UNIQUE (email),
    CONSTRAINT users_firebase_uid_key UNIQUE (firebase_uid),
    CONSTRAINT users_household_id_fkey FOREIGN KEY (household_id)
        REFERENCES public.households (household_id) MATCH SIMPLE
        ON UPDATE NO ACTION
        ON DELETE NO ACTION
)

TABLESPACE pg_default;

ALTER TABLE IF EXISTS public.users
    OWNER to bcbauser;

GRANT ALL ON TABLE public.users TO bcbauser;

GRANT ALL ON TABLE public.users TO vitals_user;

-- Table: public.visit_logs

-- DROP TABLE IF EXISTS public.visit_logs;

CREATE TABLE IF NOT EXISTS public.visit_logs
(
    visit_id uuid NOT NULL DEFAULT gen_random_uuid(),
    patient_id uuid NOT NULL,
    doctor_id uuid,
    household_id uuid NOT NULL,
    visit_date timestamp with time zone NOT NULL DEFAULT now(),
    reason text COLLATE pg_catalog."default",
    notes text COLLATE pg_catalog."default",
    follow_up_date date,
    created_at timestamp with time zone DEFAULT now(),
    updated_at timestamp with time zone DEFAULT now(),
    is_active boolean NOT NULL DEFAULT true,
    CONSTRAINT visit_logs_pkey PRIMARY KEY (visit_id),
    CONSTRAINT fk_visit_doctor FOREIGN KEY (doctor_id)
        REFERENCES public.doctors (doctor_id) MATCH SIMPLE
        ON UPDATE NO ACTION
        ON DELETE SET NULL,
    CONSTRAINT fk_visit_household FOREIGN KEY (household_id)
        REFERENCES public.households (household_id) MATCH SIMPLE
        ON UPDATE NO ACTION
        ON DELETE CASCADE,
    CONSTRAINT fk_visit_patient FOREIGN KEY (patient_id)
        REFERENCES public.patients (patient_id) MATCH SIMPLE
        ON UPDATE NO ACTION
        ON DELETE CASCADE
)

TABLESPACE pg_default;

ALTER TABLE IF EXISTS public.visit_logs
    OWNER to postgres;

GRANT ALL ON TABLE public.visit_logs TO postgres;

GRANT ALL ON TABLE public.visit_logs TO vitals_user;

-- Table: public.vitals

-- DROP TABLE IF EXISTS public.vitals;

CREATE TABLE IF NOT EXISTS public.vitals
(
    vital_id uuid NOT NULL DEFAULT gen_random_uuid(),
    patient_id uuid,
    recorded_by uuid,
    recorded_at timestamp with time zone NOT NULL DEFAULT now(),
    systolic integer,
    diastolic integer,
    heart_rate integer,
    temperature numeric(4,1),
    oxygen_saturation integer,
    blood_glucose integer,
    notes text COLLATE pg_catalog."default",
    created_at timestamp with time zone DEFAULT now(),
    household_id uuid NOT NULL,
    source text COLLATE pg_catalog."default",
    weight numeric(5,1),
    local_offset_minutes integer,
    CONSTRAINT vitals_pkey PRIMARY KEY (vital_id),
    CONSTRAINT fk_vitals_household FOREIGN KEY (household_id)
        REFERENCES public.households (household_id) MATCH SIMPLE
        ON UPDATE NO ACTION
        ON DELETE NO ACTION,
    CONSTRAINT vitals_patient_id_fkey FOREIGN KEY (patient_id)
        REFERENCES public.patients (patient_id) MATCH SIMPLE
        ON UPDATE NO ACTION
        ON DELETE NO ACTION,
    CONSTRAINT chk_bp CHECK (systolic IS NULL OR systolic >= 50 AND systolic <= 250),
    CONSTRAINT chk_diastolic CHECK (diastolic IS NULL OR diastolic >= 30 AND diastolic <= 150),
    CONSTRAINT chk_hr CHECK (heart_rate IS NULL OR heart_rate >= 30 AND heart_rate <= 220),
    CONSTRAINT chk_spo2 CHECK (oxygen_saturation IS NULL OR oxygen_saturation >= 50 AND oxygen_saturation <= 100),
    CONSTRAINT chk_temp CHECK (temperature IS NULL OR temperature >= 90::numeric AND temperature <= 110::numeric),
    CONSTRAINT chk_weight CHECK (weight IS NULL OR weight >= 50::numeric AND weight <= 700::numeric)
)

TABLESPACE pg_default;

ALTER TABLE IF EXISTS public.vitals
    OWNER to postgres;

GRANT ALL ON TABLE public.vitals TO postgres;

GRANT ALL ON TABLE public.vitals TO vitals_user;
-- Index: idx_vitals_patient_recorded

-- DROP INDEX IF EXISTS public.idx_vitals_patient_recorded;

CREATE INDEX IF NOT EXISTS idx_vitals_patient_recorded
    ON public.vitals USING btree
    (patient_id ASC NULLS LAST, recorded_at ASC NULLS LAST)
    TABLESPACE pg_default;

-- Table: public.vitals_analysis_cache

-- DROP TABLE IF EXISTS public.vitals_analysis_cache;

CREATE TABLE IF NOT EXISTS public.vitals_analysis_cache
(
    cache_id uuid NOT NULL DEFAULT gen_random_uuid(),
    patient_id uuid NOT NULL,
    vital_type text COLLATE pg_catalog."default" NOT NULL,
    window_days integer NOT NULL,
    result jsonb NOT NULL,
    computed_at timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT vitals_analysis_cache_pkey PRIMARY KEY (cache_id),
    CONSTRAINT vitals_analysis_cache_patient_id_vital_type_window_days_key UNIQUE (patient_id, vital_type, window_days),
    CONSTRAINT vitals_analysis_cache_patient_id_fkey FOREIGN KEY (patient_id)
        REFERENCES public.patients (patient_id) MATCH SIMPLE
        ON UPDATE NO ACTION
        ON DELETE NO ACTION
)

TABLESPACE pg_default;

ALTER TABLE IF EXISTS public.vitals_analysis_cache
    OWNER to bcbauser;

REVOKE ALL ON TABLE public.vitals_analysis_cache FROM vitals_user;

GRANT ALL ON TABLE public.vitals_analysis_cache TO bcbauser;

GRANT INSERT, DELETE, SELECT, UPDATE ON TABLE public.vitals_analysis_cache TO vitals_user;