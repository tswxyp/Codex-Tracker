ALTER TABLE calibration_samples_v1 ADD COLUMN quota_points INTEGER NOT NULL DEFAULT 1
    CHECK (quota_points BETWEEN 1 AND 3);
ALTER TABLE calibration_samples_v1 ADD COLUMN snapshot_tokens INTEGER;
ALTER TABLE calibration_samples_v1 ADD COLUMN protocol INTEGER NOT NULL DEFAULT 1 CHECK (protocol IN (1,2));

-- Keep the original one-point reporting contract intact.
DROP VIEW calibration_eligible_v1;
CREATE VIEW calibration_eligible_v1 AS
SELECT receipt, model, effort, speed, input, output, cache_read, cache_write, reasoning
FROM calibration_samples_v1 WHERE review_status='approved' AND quota_points=1;

CREATE VIEW calibration_eligible_v2 AS
SELECT receipt, model, effort, speed, input, output, cache_read, cache_write, reasoning,
       quota_points, snapshot_tokens, (input+output)*1.0/quota_points AS tokens_per_point
FROM calibration_samples_v1 WHERE review_status='approved' AND protocol=2;
