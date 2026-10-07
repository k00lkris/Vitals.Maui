BEGIN;

-- Phase 7 / 0.7.1b
-- Household entitlement-state foundation.
--
-- The household is the source of truth for commercial access.
-- "tier" describes the household plan/type:
--   trial | basic | standard | family | founder
--
-- "subscription_status" describes the current billing/access state:
--   trial | active | grace | basic
--
-- Founder households are permanent full-access households:
--   tier='founder', subscription_status='active',
--   patient_limit=NULL, and no trial/subscription/grace expiration.
--
-- Data-specific founder assignments are intentionally NOT hard-coded into
-- source control. Apply the founder UPDATE shown at the bottom manually to
-- the approved production households during deployment.

ALTER TABLE public.households
    ADD COLUMN IF NOT EXISTS owner_user_id uuid,
    ADD COLUMN IF NOT EXISTS billing_owner_user_id uuid,
    ADD COLUMN IF NOT EXISTS billing_provider text,
    ADD COLUMN IF NOT EXISTS billing_product_id text,
    ADD COLUMN IF NOT EXISTS subscription_started_at timestamp with time zone,
    ADD COLUMN IF NOT EXISTS subscription_ends_at timestamp with time zone,
    ADD COLUMN IF NOT EXISTS grace_ends_at timestamp with time zone,
    ADD COLUMN IF NOT EXISTS cancel_at_period_end boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS entitlement_updated_at timestamp with time zone NOT NULL DEFAULT now();

ALTER TABLE public.users
    ADD COLUMN IF NOT EXISTS household_role text NOT NULL DEFAULT 'member';

-- NULL patient_limit means unlimited. This is reserved for Founder.
ALTER TABLE public.households
    ALTER COLUMN patient_limit DROP NOT NULL;

-- Backfill household ownership to the earliest-created user in each
-- household. For existing alpha households this represents the account
-- that originally created the household.
WITH ranked_users AS (
    SELECT
        user_id,
        household_id,
        ROW_NUMBER() OVER (
            PARTITION BY household_id
            ORDER BY created_at ASC NULLS LAST, user_id ASC
        ) AS rn
    FROM public.users
    WHERE household_id IS NOT NULL
)
UPDATE public.households h
SET owner_user_id = r.user_id
FROM ranked_users r
WHERE r.household_id = h.household_id
  AND r.rn = 1
  AND h.owner_user_id IS NULL;

UPDATE public.users u
SET household_role = CASE
    WHEN h.owner_user_id = u.user_id THEN 'owner'
    ELSE 'member'
END
FROM public.households h
WHERE h.household_id = u.household_id;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'households_owner_user_id_fkey'
    ) THEN
        ALTER TABLE public.households
            ADD CONSTRAINT households_owner_user_id_fkey
            FOREIGN KEY (owner_user_id)
            REFERENCES public.users (user_id)
            ON DELETE SET NULL;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'households_billing_owner_user_id_fkey'
    ) THEN
        ALTER TABLE public.households
            ADD CONSTRAINT households_billing_owner_user_id_fkey
            FOREIGN KEY (billing_owner_user_id)
            REFERENCES public.users (user_id)
            ON DELETE SET NULL;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'households_tier_check'
    ) THEN
        ALTER TABLE public.households
            ADD CONSTRAINT households_tier_check
            CHECK (
                tier IS NULL OR
                tier IN ('trial', 'basic', 'standard', 'family', 'founder')
            ) NOT VALID;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'households_subscription_status_check'
    ) THEN
        ALTER TABLE public.households
            ADD CONSTRAINT households_subscription_status_check
            CHECK (
                subscription_status IN ('trial', 'active', 'grace', 'basic')
            ) NOT VALID;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'households_founder_unlimited_check'
    ) THEN
        ALTER TABLE public.households
            ADD CONSTRAINT households_founder_unlimited_check
            CHECK (
                patient_limit IS NOT NULL OR tier = 'founder'
            ) NOT VALID;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'households_billing_provider_check'
    ) THEN
        ALTER TABLE public.households
            ADD CONSTRAINT households_billing_provider_check
            CHECK (
                billing_provider IS NULL OR
                billing_provider IN ('apple', 'google')
            ) NOT VALID;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'users_household_role_check'
    ) THEN
        ALTER TABLE public.users
            ADD CONSTRAINT users_household_role_check
            CHECK (household_role IN ('owner', 'manager', 'member')) NOT VALID;
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_households_owner_user_id
    ON public.households (owner_user_id);

CREATE INDEX IF NOT EXISTS idx_households_billing_owner_user_id
    ON public.households (billing_owner_user_id);

CREATE INDEX IF NOT EXISTS idx_users_household_role
    ON public.users (household_id, household_role);

COMMIT;

-- One-time production founder assignment template.
-- Run once for EACH approved founder household after replacing the placeholder:
--
-- UPDATE public.households
-- SET tier = 'founder',
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
