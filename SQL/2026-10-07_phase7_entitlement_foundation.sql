BEGIN;

-- Phase 7 / 0.7.1b
-- Household entitlement-state foundation.
--
-- Goals:
--   * Make household entitlement explicit and independent from store billing.
--   * Add first-class Founder access.
--   * Add household owner/manager/member governance.
--   * Track the billing owner without conflating that user with household ownership.
--   * Add paid-through and grace-period state needed by Apple/Google subscription lifecycle work.
--   * Represent unlimited Founder capacity explicitly with patient_limit = NULL.
--
-- IMPORTANT:
--   * This migration does not hard-code production Founder household IDs.
--   * Existing alpha households are backfilled conservatively from their current
--     household tier/subscription state. Approved Founder households must be
--     explicitly reclassified during deployment before entitlement enforcement
--     is enabled.
--   * Legacy users.subscription_status / users.subscription_ends_at /
--     users.stripe_customer_id are intentionally left untouched in 0.7.1b.
--     Household entitlement is the new source of truth; legacy cleanup can occur
--     separately after the Phase 7 access model is proven.

ALTER TABLE public.households
    ADD COLUMN IF NOT EXISTS entitlement_type text,
    ADD COLUMN IF NOT EXISTS entitlement_status text,
    ADD COLUMN IF NOT EXISTS trial_consumed boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS paid_through_at timestamp with time zone,
    ADD COLUMN IF NOT EXISTS grace_started_at timestamp with time zone,
    ADD COLUMN IF NOT EXISTS grace_ends_at timestamp with time zone,
    ADD COLUMN IF NOT EXISTS owner_user_id uuid,
    ADD COLUMN IF NOT EXISTS billing_owner_user_id uuid,
    ADD COLUMN IF NOT EXISTS entitlement_updated_at timestamp with time zone NOT NULL DEFAULT now();

ALTER TABLE public.users
    ADD COLUMN IF NOT EXISTS household_role text NOT NULL DEFAULT 'member';

-- NULL is a real semantic value: unlimited household patient capacity.
-- 0.7.1b reserves unlimited capacity for Founder households.
ALTER TABLE public.households
    ALTER COLUMN patient_limit DROP NOT NULL;

-- Existing Phase 7 trial households already have trial_started_at/trial_ends_at.
-- Mark a household's one-time trial as consumed as soon as it has ever started,
-- not only after it expires, so the same household can never restart a trial.
UPDATE public.households
SET trial_consumed = true
WHERE trial_started_at IS NOT NULL
   OR trial_ends_at IS NOT NULL
   OR subscription_status = 'trial';

-- Backfill the new entitlement contract from the pre-0.7.1b household state.
-- There are no paid Apple/Google subscriptions yet on main, but retaining the
-- active/grace mapping makes the migration safe if a manually maintained alpha
-- row already uses one of those states.
UPDATE public.households
SET entitlement_type = CASE
        WHEN tier = 'founder' THEN 'founder'
        WHEN subscription_status = 'trial' THEN 'trial'
        WHEN subscription_status IN ('active', 'grace', 'past_due') THEN 'paid'
        ELSE 'basic'
    END,
    entitlement_status = CASE
        WHEN tier = 'founder' THEN 'active'
        WHEN subscription_status = 'grace' THEN 'grace'
        WHEN subscription_status IN ('expired', 'revoked', 'inactive') THEN 'inactive'
        ELSE 'active'
    END,
    entitlement_updated_at = now()
WHERE entitlement_type IS NULL
   OR entitlement_status IS NULL;

-- Establish one canonical household owner for existing households.
-- The earliest-created household user is the best deterministic representation
-- of the original creator available in the current schema.
WITH ranked_users AS (
    SELECT
        u.user_id,
        u.household_id,
        ROW_NUMBER() OVER (
            PARTITION BY u.household_id
            ORDER BY u.created_at ASC NULLS LAST, u.user_id ASC
        ) AS rn
    FROM public.users u
    WHERE u.household_id IS NOT NULL
)
UPDATE public.households h
SET owner_user_id = r.user_id
FROM ranked_users r
WHERE r.household_id = h.household_id
  AND r.rn = 1
  AND h.owner_user_id IS NULL;

-- Existing household users become member by default; the canonical owner is
-- promoted to owner. Manager delegation will be explicit and plan-aware in API
-- business rules rather than inferred from patient relationships.
UPDATE public.users u
SET household_role = CASE
        WHEN h.owner_user_id = u.user_id THEN 'owner'
        ELSE 'member'
    END
FROM public.households h
WHERE h.household_id = u.household_id;

-- New households created after this migration should receive a complete
-- entitlement row even before the 0.7.1b API changes are deployed.
ALTER TABLE public.households
    ALTER COLUMN entitlement_type SET DEFAULT 'trial',
    ALTER COLUMN entitlement_type SET NOT NULL,
    ALTER COLUMN entitlement_status SET DEFAULT 'active',
    ALTER COLUMN entitlement_status SET NOT NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'households_owner_user_id_fkey'
          AND conrelid = 'public.households'::regclass
    ) THEN
        ALTER TABLE public.households
            ADD CONSTRAINT households_owner_user_id_fkey
            FOREIGN KEY (owner_user_id)
            REFERENCES public.users (user_id)
            ON DELETE SET NULL;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'households_billing_owner_user_id_fkey'
          AND conrelid = 'public.households'::regclass
    ) THEN
        ALTER TABLE public.households
            ADD CONSTRAINT households_billing_owner_user_id_fkey
            FOREIGN KEY (billing_owner_user_id)
            REFERENCES public.users (user_id)
            ON DELETE SET NULL;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'households_entitlement_type_check'
          AND conrelid = 'public.households'::regclass
    ) THEN
        ALTER TABLE public.households
            ADD CONSTRAINT households_entitlement_type_check
            CHECK (entitlement_type IN ('basic', 'trial', 'paid', 'founder'));
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'households_entitlement_status_check'
          AND conrelid = 'public.households'::regclass
    ) THEN
        ALTER TABLE public.households
            ADD CONSTRAINT households_entitlement_status_check
            CHECK (entitlement_status IN ('active', 'grace', 'inactive'));
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'households_patient_limit_entitlement_check'
          AND conrelid = 'public.households'::regclass
    ) THEN
        ALTER TABLE public.households
            ADD CONSTRAINT households_patient_limit_entitlement_check
            CHECK (
                (entitlement_type = 'founder' AND patient_limit IS NULL)
                OR
                (
                    entitlement_type <> 'founder'
                    AND patient_limit IS NOT NULL
                    AND patient_limit > 0
                )
            );
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'households_founder_state_check'
          AND conrelid = 'public.households'::regclass
    ) THEN
        ALTER TABLE public.households
            ADD CONSTRAINT households_founder_state_check
            CHECK (
                entitlement_type <> 'founder'
                OR (
                    entitlement_status = 'active'
                    AND patient_limit IS NULL
                    AND billing_owner_user_id IS NULL
                    AND paid_through_at IS NULL
                    AND grace_started_at IS NULL
                    AND grace_ends_at IS NULL
                    AND trial_ends_at IS NULL
                )
            );
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'users_household_role_check'
          AND conrelid = 'public.users'::regclass
    ) THEN
        ALTER TABLE public.users
            ADD CONSTRAINT users_household_role_check
            CHECK (household_role IN ('owner', 'manager', 'member'));
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_households_owner_user_id
    ON public.households (owner_user_id);

CREATE INDEX IF NOT EXISTS idx_households_billing_owner_user_id
    ON public.households (billing_owner_user_id);

CREATE INDEX IF NOT EXISTS idx_households_entitlement
    ON public.households (entitlement_type, entitlement_status);

CREATE INDEX IF NOT EXISTS idx_users_household_role
    ON public.users (household_id, household_role);

COMMIT;

-- Deployment-only Founder assignment template.
-- Run once for EACH approved Founder household before entitlement enforcement
-- is enabled. Replace <HOUSEHOLD_UUID> deliberately; do not commit production
-- household IDs into application business logic.
--
-- UPDATE public.households
-- SET tier = 'founder',
--     entitlement_type = 'founder',
--     entitlement_status = 'active',
--     subscription_status = 'active',
--     patient_limit = NULL,
--     trial_ends_at = NULL,
--     paid_through_at = NULL,
--     grace_started_at = NULL,
--     grace_ends_at = NULL,
--     billing_owner_user_id = NULL,
--     entitlement_updated_at = now()
-- WHERE household_id = '<HOUSEHOLD_UUID>';
