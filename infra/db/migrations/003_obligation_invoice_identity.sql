BEGIN;

ALTER TABLE obligations
    ADD COLUMN IF NOT EXISTS invoice_reference TEXT COLLATE "C";

UPDATE obligations
SET invoice_reference = 'legacy:' || id::TEXT
WHERE invoice_reference IS NULL;

ALTER TABLE obligations
    ALTER COLUMN invoice_reference SET NOT NULL;

ALTER TABLE obligations
    DROP CONSTRAINT IF EXISTS obligations_invoice_reference_valid;

ALTER TABLE obligations
    ADD CONSTRAINT obligations_invoice_reference_valid
    CHECK (
        length(btrim(invoice_reference)) > 0
        AND invoice_reference = btrim(invoice_reference)
    );

CREATE UNIQUE INDEX IF NOT EXISTS obligations_run_invoice_reference_idx 
ON obligations (run_id, invoice_reference);

COMMIT;
