-- Dated height history for auditable BMI/weight analysis.
--
-- patients.height_inches remains the current profile-height cache for
-- compatibility and fast current-BMI calculation. This table is the dated
-- source of truth for height observations going forward.
--
-- Existing scalar heights are backfilled as profile snapshots effective on
-- the migration date. We intentionally do NOT pretend those heights were
-- measured on an earlier date.

CREATE TABLE IF NOT EXISTS public.patient_height_history
(
    height_id uuid NOT NULL DEFAULT gen_random_uuid(),
    patient_id uuid NOT NULL,
    household_id uuid NOT NULL,
    height_inches smallint NOT NULL,
    effective_date date NOT NULL,
    entry_type text NOT NULL DEFAULT 'measurement',
    source text NOT NULL DEFAULT 'manual_profile',
    supersedes_height_id uuid,
    is_active boolean NOT NULL DEFAULT true,
    created_by uuid,
    created_at timestamp with time zone NOT NULL DEFAULT now(),

    CONSTRAINT patient_height_history_pkey PRIMARY KEY (height_id),
    CONSTRAINT patient_height_history_height_check
        CHECK (height_inches BETWEEN 12 AND 107),
    CONSTRAINT patient_height_history_entry_type_check
        CHECK (entry_type IN ('measurement', 'correction', 'profile_backfill')),
    CONSTRAINT patient_height_history_patient_fkey
        FOREIGN KEY (patient_id)
        REFERENCES public.patients (patient_id)
        ON DELETE CASCADE,
    CONSTRAINT patient_height_history_household_fkey
        FOREIGN KEY (household_id)
        REFERENCES public.households (household_id)
        ON DELETE CASCADE,
    CONSTRAINT patient_height_history_created_by_fkey
        FOREIGN KEY (created_by)
        REFERENCES public.users (user_id)
        ON DELETE SET NULL,
    CONSTRAINT patient_height_history_supersedes_fkey
        FOREIGN KEY (supersedes_height_id)
        REFERENCES public.patient_height_history (height_id)
        ON DELETE SET NULL
);

CREATE INDEX IF NOT EXISTS idx_patient_height_history_patient_date
    ON public.patient_height_history
    (patient_id, effective_date DESC, created_at DESC);

CREATE INDEX IF NOT EXISTS idx_patient_height_history_household
    ON public.patient_height_history
    (household_id);

INSERT INTO public.patient_height_history
(
    patient_id,
    household_id,
    height_inches,
    effective_date,
    entry_type,
    source,
    is_active
)
SELECT
    p.patient_id,
    p.household_id,
    p.height_inches,
    current_date,
    'profile_backfill',
    'profile_backfill',
    true
FROM public.patients p
WHERE p.height_inches IS NOT NULL
  AND NOT EXISTS (
      SELECT 1
      FROM public.patient_height_history h
      WHERE h.patient_id = p.patient_id
  );

ALTER TABLE IF EXISTS public.patient_height_history
    OWNER TO postgres;

GRANT ALL ON TABLE public.patient_height_history TO postgres;
GRANT ALL ON TABLE public.patient_height_history TO vitals_user;
