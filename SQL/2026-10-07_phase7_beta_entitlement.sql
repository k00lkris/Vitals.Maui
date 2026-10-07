BEGIN;

-- Phase 7 / 0.7.1b follow-up
-- Permanent Beta tester complimentary entitlement.
--
-- Founder and Beta are intentionally distinct household tiers, but both receive
-- permanent full Premium access, unlimited patient capacity, no billing
-- requirement, and immunity from Trial -> Basic downgrade transitions.
--
-- No production households are converted to Beta by this migration. Award Beta
-- access explicitly to approved testing households using the template below.

-- The original 0.7.1b tier constraint predates the Beta thank-you tier.
ALTER TABLE public.households
    DROP CONSTRAINT IF EXISTS households_tier_check;

ALTER TABLE public.households
    ADD CONSTRAINT households_tier_check
    CHECK (
        tier IS NULL OR
        tier IN ('trial', 'basic', 'standard', 'family', 'founder', 'beta')
    ) NOT VALID;

-- Replace the Founder-only unlimited rule with a permanent-complimentary rule.
-- NULL patient_limit remains an explicit unlimited value; it is never a magic
-- numeric sentinel and is valid only for Founder/Beta households.
ALTER TABLE public.households
    DROP CONSTRAINT IF EXISTS households_founder_unlimited_check;

ALTER TABLE public.households
    DROP CONSTRAINT IF EXISTS households_complimentary_unlimited_check;

ALTER TABLE public.households
    ADD CONSTRAINT households_complimentary_unlimited_check
    CHECK (
        patient_limit IS NOT NULL OR
        tier IN ('founder', 'beta')
    ) NOT VALID;

-- Permanent complimentary households are never tied to a store purchase,
-- expiration timestamp, grace period, or scheduled cancellation.
ALTER TABLE public.households
    DROP CONSTRAINT IF EXISTS households_complimentary_state_check;

ALTER TABLE public.households
    ADD CONSTRAINT households_complimentary_state_check
    CHECK (
        tier NOT IN ('founder', 'beta')
        OR (
            subscription_status = 'active'
            AND patient_limit IS NULL
            AND trial_ends_at IS NULL
            AND subscription_started_at IS NULL
            AND subscription_ends_at IS NULL
            AND grace_ends_at IS NULL
            AND cancel_at_period_end = false
            AND billing_owner_user_id IS NULL
            AND billing_provider IS NULL
            AND billing_product_id IS NULL
        )
    ) NOT VALID;

COMMIT;

-- Deployment/admin template for awarding permanent Beta tester access.
-- Replace <HOUSEHOLD_UUID> deliberately for an approved beta-testing household.
--
-- UPDATE public.households
-- SET tier = 'beta',
--     subscription_status = 'active',
--     patient_limit = NULL,
--     trial_ends_at = NULL,
--     subscription_started_at = NULL,
--     subscription_ends_at = NULL,
--     grace_ends_at = NULL,
--     cancel_at_period_end = false,
--     billing_owner_user_id = NULL,
--     billing_provider = NULL,
--     billing_product_id = NULL,
--     entitlement_updated_at = now()
-- WHERE household_id = '<HOUSEHOLD_UUID>';
