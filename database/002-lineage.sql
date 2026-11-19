ALTER TABLE Session ADD COLUMN history_thread_id TEXT;
ALTER TABLE Session ADD COLUMN history_end_ordinal INTEGER;
ALTER TABLE Session ADD COLUMN history_end_offset INTEGER;
ALTER TABLE Session ADD COLUMN subagent_start_ordinal INTEGER;
CREATE INDEX IF NOT EXISTS event_shared_turn ON Event(device_id,kind,turn_id);
CREATE INDEX IF NOT EXISTS event_nearby ON Event(device_id,kind,time_ms);
CREATE VIEW EventSource AS
SELECT printf('%lld:%lld:%lld',source_id,generation,offset) AS source_record_id, r.*
FROM SourceRecord r;
