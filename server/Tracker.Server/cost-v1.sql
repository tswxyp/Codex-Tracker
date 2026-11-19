CREATE TABLE cost_schema(version integer PRIMARY KEY);
INSERT INTO cost_schema VALUES(1);
CREATE TABLE cost_revision(
    acct text NOT NULL REFERENCES account_stream,
    kind text NOT NULL CHECK(kind IN ('bill','rule','credits','reset')),
    id uuid NOT NULL, version bigint NOT NULL CHECK(version>0),
    body jsonb NOT NULL, recorded_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY(acct,kind,id,version)
);
CREATE INDEX cost_latest ON cost_revision(acct,kind,id,version DESC);
CREATE TABLE cost_speed(
    acct text NOT NULL, device_id uuid NOT NULL, id uuid NOT NULL, rev bigint NOT NULL, speed text NOT NULL,
    PRIMARY KEY(acct,device_id,id), FOREIGN KEY(acct,device_id,id) REFERENCES observation(acct,device_id,id)
);
CREATE TABLE cost_coverage(
    acct text NOT NULL, device_id uuid NOT NULL, pending boolean NOT NULL,
    PRIMARY KEY(acct,device_id), FOREIGN KEY(acct,device_id) REFERENCES device(acct,id)
);
CREATE TABLE cost_statement(
    acct text NOT NULL REFERENCES account_stream, bill uuid NOT NULL,
    digest text NOT NULL, result jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(), PRIMARY KEY(acct,bill,digest)
);
DO $rls$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['cost_revision','cost_statement','cost_speed','cost_coverage'] LOOP
        EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY',t);
        EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY',t);
        EXECUTE format('CREATE POLICY tenant ON %I USING (acct=current_setting(''tracker.account'',true)) WITH CHECK (acct=current_setting(''tracker.account'',true))',t);
    END LOOP;
END $rls$;
