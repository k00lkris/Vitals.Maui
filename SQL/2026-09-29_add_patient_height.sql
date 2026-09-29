-- Add canonical patient height storage.
-- The mobile app displays feet/inches, but persists one total-inch value so
-- BMI/weight calculations have one unambiguous source of truth.

ALTER TABLE public.patients
    ADD COLUMN IF NOT EXISTS height_inches smallint;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'patients_height_inches_check'
          AND conrelid = 'public.patients'::regclass
    ) THEN
        ALTER TABLE public.patients
            ADD CONSTRAINT patients_height_inches_check
            CHECK (height_inches IS NULL OR height_inches BETWEEN 12 AND 107);
    END IF;
END $$;
