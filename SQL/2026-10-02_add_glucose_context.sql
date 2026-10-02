-- Glucose context foundation for patient-recorded glucose.
-- Existing vitals.blood_glucose remains the normalized mg/dL value.
-- Context is stored one-to-one with the vitals row so legacy readings remain valid
-- with no context row and can be treated as "unknown" by the analysis engine.

CREATE TABLE IF NOT EXISTS public.glucose_context
(
    vital_id uuid NOT NULL,
    measurement_context text COLLATE pg_catalog."default" NOT NULL DEFAULT 'unknown'::text,
    meal_type text COLLATE pg_catalog."default",
    minutes_after_meal integer,
    meal_event_id uuid,
    source_type text COLLATE pg_catalog."default" NOT NULL DEFAULT 'manual_bgm'::text,
    original_value numeric(8,3),
    original_unit text COLLATE pg_catalog."default",
    source_device text COLLATE pg_catalog."default",
    is_invalidated boolean NOT NULL DEFAULT false,
    notes text COLLATE pg_catalog."default",
    created_at timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT glucose_context_pkey PRIMARY KEY (vital_id),
    CONSTRAINT glucose_context_vital_id_fkey FOREIGN KEY (vital_id)
        REFERENCES public.vitals (vital_id)
        ON UPDATE NO ACTION
        ON DELETE CASCADE,
    CONSTRAINT glucose_context_measurement_context_check CHECK (
        measurement_context = ANY (
            ARRAY['fasting'::text, 'pre_meal'::text, 'post_meal'::text,
                  'bedtime'::text, 'random'::text, 'other'::text, 'unknown'::text]
        )
    ),
    CONSTRAINT glucose_context_meal_type_check CHECK (
        meal_type IS NULL OR meal_type = ANY (
            ARRAY['breakfast'::text, 'lunch'::text, 'dinner'::text,
                  'snack'::text, 'other'::text]
        )
    ),
    CONSTRAINT glucose_context_minutes_after_meal_check CHECK (
        minutes_after_meal IS NULL OR minutes_after_meal BETWEEN 0 AND 720
    ),
    CONSTRAINT glucose_context_source_type_check CHECK (
        source_type = ANY (
            ARRAY['manual_bgm'::text, 'cgm'::text, 'import'::text, 'other'::text]
        )
    ),
    CONSTRAINT glucose_context_original_unit_check CHECK (
        original_unit IS NULL OR original_unit = ANY (
            ARRAY['mg/dL'::text, 'mmol/L'::text]
        )
    )
);

ALTER TABLE IF EXISTS public.glucose_context
    OWNER to bcbauser;

REVOKE ALL ON TABLE public.glucose_context FROM vitals_user;
GRANT ALL ON TABLE public.glucose_context TO bcbauser;
GRANT INSERT, DELETE, SELECT, UPDATE ON TABLE public.glucose_context TO vitals_user;

CREATE INDEX IF NOT EXISTS idx_glucose_context_meal_event
    ON public.glucose_context USING btree (meal_event_id)
    WHERE meal_event_id IS NOT NULL;
