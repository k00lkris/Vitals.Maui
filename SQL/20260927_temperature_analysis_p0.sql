-- Vitals Temperature Analysis P0 migration
-- Date: 2026-09-27
-- Purpose: preserve temperature measurement site so the dedicated
-- temperature analysis engine can compare like-with-like readings.

BEGIN;

ALTER TABLE public.vitals
    ADD COLUMN IF NOT EXISTS temperature_site text;

-- Existing temperature rows predate site capture. Preserve them as usable
-- history, but mark the site as unknown so site-sensitive comparisons can
-- explicitly capability-gate rather than silently assuming oral/temporal.
UPDATE public.vitals
SET temperature_site = 'unknown'
WHERE temperature IS NOT NULL
  AND temperature_site IS NULL;

ALTER TABLE public.vitals
    DROP CONSTRAINT IF EXISTS chk_temperature_site;

ALTER TABLE public.vitals
    ADD CONSTRAINT chk_temperature_site CHECK (
        temperature_site IS NULL OR temperature_site = ANY (
            ARRAY['oral'::text, 'rectal'::text, 'axillary'::text, 'tympanic'::text,
                  'temporal'::text, 'other'::text, 'unknown'::text]
        )
    );

COMMENT ON COLUMN public.vitals.temperature_site IS
    'Temperature measurement site: oral, rectal, axillary, tympanic, temporal, other, or unknown. NULL means no temperature/site was supplied.';

COMMIT;
