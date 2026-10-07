BEGIN;

-- Phase 7 downgrade protection
-- Preserve every patient and all clinical data when a household falls below
-- its current patient capacity. Unselected profiles are entitlement-locked
-- rather than deleted. Locks are only enforced while the household is above
-- its effective plan capacity; an upgrade restores access automatically.

ALTER TABLE public.patients
    ADD COLUMN IF NOT EXISTS entitlement_locked boolean NOT NULL DEFAULT false;

ALTER TABLE public.patients
    ADD COLUMN IF NOT EXISTS entitlement_locked_at timestamp with time zone;

COMMENT ON COLUMN public.patients.entitlement_locked IS
    'True when this patient was not selected to remain active during an over-capacity entitlement downgrade.';

COMMENT ON COLUMN public.patients.entitlement_locked_at IS
    'Timestamp when the current entitlement lock was first applied; NULL while selected/active.';

CREATE INDEX IF NOT EXISTS idx_patients_household_entitlement_locked
    ON public.patients (household_id, entitlement_locked);

COMMIT;
