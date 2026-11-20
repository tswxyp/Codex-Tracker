-- NULL means the client did not report activity evidence for this receipt.
ALTER TABLE calibration_samples_v1 ADD COLUMN active_sessions INTEGER CHECK (active_sessions >= 0);
ALTER TABLE calibration_samples_v1 ADD COLUMN task_overlap INTEGER CHECK (task_overlap IN (0,1));
ALTER TABLE calibration_samples_v1 ADD COLUMN unattributed_usage INTEGER CHECK (unattributed_usage IN (0,1))
    CHECK ((active_sessions IS NULL AND task_overlap IS NULL AND unattributed_usage IS NULL) OR
           (active_sessions IS NOT NULL AND task_overlap IS NOT NULL AND unattributed_usage IS NOT NULL));

DROP VIEW calibration_eligible_v1;
DROP VIEW calibration_eligible_v2;
CREATE VIEW calibration_eligible_v2 AS
SELECT receipt, model, effort, speed, input, output, cache_read, cache_write, reasoning,
       quota_points, snapshot_tokens, (input+output)*1.0/quota_points AS tokens_per_point,
       active_sessions, task_overlap, unattributed_usage
FROM calibration_samples_v1 WHERE protocol=2 AND review_status='approved'
    AND active_sessions=1 AND task_overlap=0 AND unattributed_usage=0;

CREATE VIEW calibration_eligible_v1 AS
SELECT receipt, model, effort, speed, input, output, cache_read, cache_write, reasoning
FROM calibration_eligible_v2 WHERE quota_points=1;
