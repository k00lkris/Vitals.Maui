BEGIN;

-- Phase 7 / 0.7.2
-- Native Apple App Store / Google Play billing persistence foundation.
--
-- Household entitlement remains the source of truth for access. This table
-- records the verified store subscription that feeds that entitlement state.
--
-- provider subscription reference semantics:
--   apple  -> StoreKit/App Store originalTransactionId
--   google -> Google Play purchaseToken
--
-- The provider/reference uniqueness constraint is the database guard for the
-- product rule that one store subscription may map to only one Vitals
-- household unless support performs an explicit migration.

CREATE TABLE IF NOT EXISTS public.billing_subscriptions
(
    billing_subscription_id uuid NOT NULL DEFAULT gen_random_uuid(),
    household_id uuid NOT NULL,
    billing_owner_user_id uuid,
    provider text NOT NULL,
    plan text NOT NULL,
    billing_period text NOT NULL,
    product_id text NOT NULL,
    base_plan_id text,
    external_subscription_ref text NOT NULL,
    linked_external_subscription_ref text,
    latest_transaction_id text,
    store_environment text NOT NULL DEFAULT 'production',
    store_status text NOT NULL DEFAULT 'unknown',
    auto_renew_enabled boolean,
    purchased_at timestamp with time zone,
    expires_at timestamp with time zone,
    last_verified_at timestamp with time zone,
    created_at timestamp with time zone NOT NULL DEFAULT now(),
    updated_at timestamp with time zone NOT NULL DEFAULT now(),

    CONSTRAINT billing_subscriptions_pkey
        PRIMARY KEY (billing_subscription_id),

    CONSTRAINT billing_subscriptions_household_fkey
        FOREIGN KEY (household_id)
        REFERENCES public.households (household_id)
        ON DELETE CASCADE,

    CONSTRAINT billing_subscriptions_billing_owner_fkey
        FOREIGN KEY (billing_owner_user_id)
        REFERENCES public.users (user_id)
        ON DELETE SET NULL,

    CONSTRAINT billing_subscriptions_provider_check
        CHECK (provider IN ('apple', 'google')),

    CONSTRAINT billing_subscriptions_plan_check
        CHECK (plan IN ('standard', 'family')),

    CONSTRAINT billing_subscriptions_period_check
        CHECK (billing_period IN ('monthly', 'annual')),

    CONSTRAINT billing_subscriptions_environment_check
        CHECK (store_environment IN ('production', 'sandbox', 'test')),

    CONSTRAINT billing_subscriptions_provider_ref_key
        UNIQUE (provider, external_subscription_ref)
);

CREATE INDEX IF NOT EXISTS idx_billing_subscriptions_household
    ON public.billing_subscriptions (household_id, updated_at DESC);

CREATE INDEX IF NOT EXISTS idx_billing_subscriptions_billing_owner
    ON public.billing_subscriptions (billing_owner_user_id);

CREATE INDEX IF NOT EXISTS idx_billing_subscriptions_product
    ON public.billing_subscriptions (provider, product_id, base_plan_id);

GRANT ALL ON TABLE public.billing_subscriptions TO postgres;
GRANT INSERT, DELETE, SELECT, UPDATE
    ON TABLE public.billing_subscriptions TO vitals_user;

COMMIT;
