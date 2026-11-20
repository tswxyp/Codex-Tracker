-- Historical receipts and new uploads both start outside the calibration set.
ALTER TABLE calibration_samples_v1 ADD COLUMN review_status TEXT NOT NULL DEFAULT 'pending'
    CHECK (review_status IN ('pending','approved','excluded_multitask','excluded_uncertain'));

CREATE VIEW calibration_eligible_v1 AS
SELECT receipt, model, effort, speed, input, output, cache_read, cache_write, reasoning
FROM calibration_samples_v1
WHERE review_status='approved';
