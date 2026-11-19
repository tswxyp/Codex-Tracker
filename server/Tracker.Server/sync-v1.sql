CREATE TABLE tracker_instance(singleton boolean PRIMARY KEY CHECK(singleton), epoch uuid NOT NULL,journal_id uuid NOT NULL);
CREATE TABLE account_stream(
    acct text PRIMARY KEY REFERENCES "AspNetUsers"("Id"), head bigint NOT NULL DEFAULT 0
);
CREATE TABLE device(
    acct text NOT NULL REFERENCES account_stream, id uuid NOT NULL, name text NOT NULL, public_key text NOT NULL,
    registered_at timestamptz NOT NULL DEFAULT now(), last_active timestamptz, last_sync timestamptz, revoked boolean NOT NULL DEFAULT false,
    os text NOT NULL DEFAULT 'Windows', client_version text NOT NULL DEFAULT 'unknown', version bigint NOT NULL DEFAULT 1,
    PRIMARY KEY(acct,id)
);
CREATE TABLE grant_state(
    acct text NOT NULL, id uuid NOT NULL, device_id uuid NOT NULL, epoch uuid NOT NULL, stamp text NOT NULL,
    issued_at timestamptz NOT NULL DEFAULT now(), revoked boolean NOT NULL DEFAULT false,
    PRIMARY KEY(acct,id), FOREIGN KEY(acct,device_id) REFERENCES device(acct,id)
);
CREATE INDEX grant_device ON grant_state(acct,device_id);
CREATE TABLE observation(
    acct text NOT NULL, device_id uuid NOT NULL, id uuid NOT NULL, rev bigint NOT NULL CHECK(rev>0),
    thread_id text, fact jsonb, disputed boolean NOT NULL DEFAULT false,
    PRIMARY KEY(acct,device_id,id), FOREIGN KEY(acct,device_id) REFERENCES device(acct,id)
);
CREATE INDEX obs_thread ON observation(acct,thread_id) WHERE fact IS NOT NULL;
CREATE INDEX obs_turn ON observation(acct,(fact->>'turn_id')) WHERE fact IS NOT NULL;
CREATE INDEX obs_time ON observation(acct,((fact->>'occurred_ms')::bigint)) WHERE fact IS NOT NULL;
CREATE TABLE resource(
    acct text NOT NULL REFERENCES account_stream, kind text NOT NULL, id text NOT NULL, version bigint NOT NULL,
    value jsonb, PRIMARY KEY(acct,kind,id)
);
CREATE UNIQUE INDEX active_member_thread ON resource(acct,(value->>'thread_id')) WHERE kind='member' AND value->>'thread_id' IS NOT NULL;
CREATE UNIQUE INDEX active_member_session ON resource(acct,(value->>'session_id')) WHERE kind='member' AND value->>'session_id' IS NOT NULL;
CREATE TABLE change_log(acct text NOT NULL REFERENCES account_stream, seq bigint NOT NULL, changes jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(), PRIMARY KEY(acct,seq));
CREATE INDEX change_expiry ON change_log(acct,created_at);
CREATE TABLE receipt(
    acct text NOT NULL, epoch uuid NOT NULL, device_id uuid NOT NULL, op_id uuid NOT NULL,
    digest text NOT NULL, result jsonb NOT NULL, PRIMARY KEY(acct,epoch,device_id,op_id),
    FOREIGN KEY(acct,device_id) REFERENCES device(acct,id)
);
CREATE TABLE snapshot(
    acct text NOT NULL, device_id uuid NOT NULL, id uuid NOT NULL, epoch uuid NOT NULL, watermark bigint NOT NULL,
    expires timestamptz NOT NULL, grant_id uuid NOT NULL, PRIMARY KEY(acct,device_id,id), FOREIGN KEY(acct,device_id) REFERENCES device(acct,id),
    FOREIGN KEY(acct,grant_id) REFERENCES grant_state(acct,id)
);
CREATE TABLE snapshot_page(
    acct text NOT NULL, device_id uuid NOT NULL, id uuid NOT NULL, page int NOT NULL, content text NOT NULL,
    PRIMARY KEY(acct,device_id,id,page), FOREIGN KEY(acct,device_id,id) REFERENCES snapshot ON DELETE CASCADE
);
DO $policies$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['account_stream','device','grant_state','observation','resource','change_log','receipt','snapshot','snapshot_page']
    LOOP
        EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY',t);
        EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY',t);
        EXECUTE format('CREATE POLICY tenant ON %I USING (acct = current_setting(''tracker.account'',true)) WITH CHECK (acct = current_setting(''tracker.account'',true))',t);
    END LOOP;
END $policies$;
