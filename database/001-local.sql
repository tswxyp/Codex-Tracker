CREATE TABLE Device (
    id TEXT PRIMARY KEY, root TEXT NOT NULL COLLATE NOCASE UNIQUE, created_ms INTEGER NOT NULL
);
CREATE TABLE Project (id TEXT PRIMARY KEY, name TEXT NOT NULL);
CREATE TABLE PowerGap (
    device_id TEXT NOT NULL REFERENCES Device(id),start_ms INTEGER NOT NULL,end_ms INTEGER NOT NULL,
    PRIMARY KEY(device_id,start_ms,end_ms),CHECK(end_ms>start_ms)
);
CREATE TABLE Session (
    device_id TEXT NOT NULL REFERENCES Device(id), thread_id TEXT NOT NULL,
    session_id TEXT, parent_id TEXT, fork_id TEXT, is_subagent INTEGER NOT NULL DEFAULT 0,
    project_id TEXT REFERENCES Project(id), PRIMARY KEY(device_id, thread_id)
);
CREATE TABLE SourceFile (
    id INTEGER PRIMARY KEY, device_id TEXT NOT NULL REFERENCES Device(id), path TEXT NOT NULL COLLATE NOCASE,
    generation INTEGER NOT NULL DEFAULT 1, offset INTEGER NOT NULL DEFAULT 0,
    disk_length INTEGER NOT NULL DEFAULT -1, modified INTEGER NOT NULL DEFAULT 0,
    head TEXT NOT NULL DEFAULT '', anchor TEXT NOT NULL DEFAULT '', context TEXT NOT NULL DEFAULT '{}',
    complete INTEGER NOT NULL DEFAULT 0, UNIQUE(device_id,path)
);
CREATE TABLE Event (
    id TEXT PRIMARY KEY, device_id TEXT NOT NULL, thread_id TEXT NOT NULL, logical_id TEXT NOT NULL,
    kind TEXT NOT NULL, turn_id TEXT, time_ms INTEGER, data TEXT NOT NULL,
    FOREIGN KEY(device_id,thread_id) REFERENCES Session(device_id,thread_id)
);
CREATE INDEX event_session ON Event(device_id,thread_id,time_ms);
CREATE INDEX event_logical ON Event(device_id,logical_id);
CREATE INDEX event_turn ON Event(device_id,thread_id,turn_id,kind);
CREATE INDEX event_shared_turn ON Event(device_id,kind,turn_id);
CREATE INDEX event_nearby ON Event(device_id,kind,time_ms);
CREATE TABLE SourceRecord (
    source_id INTEGER NOT NULL REFERENCES SourceFile(id), generation INTEGER NOT NULL, offset INTEGER NOT NULL,
    end_offset INTEGER NOT NULL, event_id TEXT REFERENCES Event(id), raw_hash TEXT,
    raw_metering TEXT, PRIMARY KEY(source_id,generation,offset)
);
CREATE INDEX source_record_event ON SourceRecord(event_id);
CREATE TABLE TokenLedger (
    device_id TEXT NOT NULL, thread_id TEXT NOT NULL, entry_id TEXT NOT NULL, event_id TEXT NOT NULL REFERENCES Event(id),
    turn_id TEXT, model TEXT NOT NULL, effort TEXT, speed TEXT, method TEXT NOT NULL, confidence TEXT NOT NULL,
    input INTEGER NOT NULL, output INTEGER NOT NULL, cache_read INTEGER, cache_write INTEGER, reasoning INTEGER, total INTEGER NOT NULL,
    PRIMARY KEY(device_id,thread_id,entry_id), FOREIGN KEY(device_id,thread_id) REFERENCES Session(device_id,thread_id)
);
CREATE VIEW ModelUsage AS
SELECT device_id,thread_id,model,effort,speed,count(*) AS entries,sum(input) AS input,sum(output) AS output,
       CASE WHEN count(cache_read)=count(*) THEN sum(cache_read) END AS cache_read,
       CASE WHEN count(cache_write)=count(*) THEN sum(cache_write) END AS cache_write,
       CASE WHEN count(reasoning)=count(*) THEN sum(reasoning) END AS reasoning,sum(total) AS total
FROM TokenLedger GROUP BY device_id,thread_id,model,effort,speed;
CREATE TABLE TaskTiming (
    device_id TEXT NOT NULL,thread_id TEXT NOT NULL,first_ms INTEGER,last_ms INTEGER,completed_ms INTEGER,
    total_ms INTEGER NOT NULL,active_ms INTEGER NOT NULL,paused_ms INTEGER NOT NULL,unknown_ms INTEGER NOT NULL,
    state TEXT NOT NULL,confidence TEXT NOT NULL,PRIMARY KEY(device_id,thread_id),
    FOREIGN KEY(device_id,thread_id) REFERENCES Session(device_id,thread_id),
    CHECK(total_ms=active_ms+paused_ms+unknown_ms)
);
CREATE TABLE TimingInterval (
    device_id TEXT NOT NULL,thread_id TEXT NOT NULL,start_ms INTEGER NOT NULL,end_ms INTEGER NOT NULL,
    kind TEXT NOT NULL, evidence TEXT NOT NULL, FOREIGN KEY(device_id,thread_id) REFERENCES Session(device_id,thread_id)
);
CREATE INDEX intervals_thread ON TimingInterval(device_id,thread_id,start_ms);
CREATE TABLE DataQuality (
    id TEXT PRIMARY KEY, device_id TEXT NOT NULL REFERENCES Device(id), thread_id TEXT,
    source_id INTEGER REFERENCES SourceFile(id), event_id TEXT REFERENCES Event(id),
    code TEXT NOT NULL, confidence TEXT NOT NULL, detail TEXT NOT NULL
);
CREATE INDEX quality_session ON DataQuality(device_id,thread_id);
CREATE TABLE Reconciliation (
    event_id TEXT PRIMARY KEY REFERENCES Event(id),device_id TEXT NOT NULL,thread_id TEXT NOT NULL,
    status TEXT NOT NULL,response_event_id TEXT REFERENCES Event(id)
);
CREATE INDEX reconciliation_thread ON Reconciliation(device_id,thread_id);
