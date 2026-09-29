-- Move optional-vital visibility/tracking settings from user scope to patient scope.
-- Blood pressure remains always enabled and therefore has no preference column.
--
-- Existing households previously had one preference set on users, so seed each
-- patient's initial values from the earliest account in that household. After
-- this migration, the mobile/API paths read and write the patient columns.

ALTER TABLE public.patients
    ADD COLUMN IF NOT EXISTS show_heart_rate boolean NOT NULL DEFAULT true,
    ADD COLUMN IF NOT EXISTS show_spo2 boolean NOT NULL DEFAULT true,
    ADD COLUMN IF NOT EXISTS show_temperature boolean NOT NULL DEFAULT true,
    ADD COLUMN IF NOT EXISTS show_weight boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS show_glucose boolean NOT NULL DEFAULT false;

WITH household_defaults AS (
    SELECT DISTINCT ON (u.household_id)
           u.household_id,
           COALESCE(u.show_heart_rate, true) AS show_heart_rate,
           COALESCE(u.show_spo2, true) AS show_spo2,
           COALESCE(u.show_temperature, true) AS show_temperature,
           COALESCE(u.show_weight, false) AS show_weight,
           COALESCE(u.show_glucose, false) AS show_glucose
    FROM public.users u
    WHERE u.household_id IS NOT NULL
    ORDER BY u.household_id, u.created_at, u.user_id
)
UPDATE public.patients p
SET show_heart_rate = d.show_heart_rate,
    show_spo2 = d.show_spo2,
    show_temperature = d.show_temperature,
    show_weight = d.show_weight,
    show_glucose = d.show_glucose
FROM household_defaults d
WHERE d.household_id = p.household_id;
