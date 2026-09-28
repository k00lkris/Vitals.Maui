-- Make blood pressure a user-selectable vital while preserving existing behavior.
-- Existing and new users default to tracking BP unless they explicitly disable it.
ALTER TABLE public.users
    ADD COLUMN IF NOT EXISTS show_blood_pressure boolean NOT NULL DEFAULT true;

-- Data invariant: at least one vital preference must remain enabled.
-- The API enforces this for normal writes; this constraint protects direct DB writes too.
ALTER TABLE public.users
    DROP CONSTRAINT IF EXISTS users_at_least_one_vital_enabled;

ALTER TABLE public.users
    ADD CONSTRAINT users_at_least_one_vital_enabled CHECK (
        COALESCE(show_blood_pressure, false)
        OR COALESCE(show_heart_rate, false)
        OR COALESCE(show_spo2, false)
        OR COALESCE(show_temperature, false)
        OR COALESCE(show_weight, false)
        OR COALESCE(show_glucose, false)
    );
