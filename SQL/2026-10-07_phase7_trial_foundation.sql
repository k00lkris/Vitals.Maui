BEGIN;

-- Phase 7 / 0.7.1a
-- Establish the household-level 30-day trial clock and normalize the
-- pre-Phase-7 onboarding tier labels. Existing alpha households are NOT
-- retroactively assigned a trial_ends_at value here; enforcement for those
-- households will be handled explicitly when entitlement gating is introduced.

ALTER TABLE public.households
    ADD COLUMN IF NOT EXISTS trial_ends_at timestamp with time zone;

UPDATE public.households
SET tier = 'standard'
WHERE tier = 'individual';

UPDATE public.households
SET tier = 'trial'
WHERE tier = 'free'
  AND subscription_status = 'trial';

COMMIT;
