-- One row per voluntarily submitted one-percentage-point observation.
CREATE TABLE calibration_samples_v1 (
    receipt TEXT PRIMARY KEY,
    model TEXT NOT NULL,
    effort TEXT NOT NULL,
    speed TEXT NOT NULL,
    input INTEGER NOT NULL CHECK (input >= 0),
    output INTEGER NOT NULL CHECK (output >= 0 AND input + output > 0),
    cache_read INTEGER NOT NULL CHECK (cache_read BETWEEN 0 AND input),
    cache_write INTEGER NOT NULL CHECK (cache_write >= 0),
    reasoning INTEGER NOT NULL CHECK (reasoning BETWEEN 0 AND output)
) STRICT, WITHOUT ROWID;
