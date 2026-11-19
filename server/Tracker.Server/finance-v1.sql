CREATE TABLE finance_schema(version integer PRIMARY KEY);
INSERT INTO finance_schema VALUES(1);

CREATE TABLE finance_revision(
    acct text NOT NULL REFERENCES account_stream,
    kind text NOT NULL CHECK (kind IN ('profile','cash','reset')),
    id uuid NOT NULL,
    version bigint NOT NULL CHECK (version > 0),
    body jsonb NOT NULL,
    recorded_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY(acct,kind,id,version)
);
CREATE INDEX finance_latest ON finance_revision(acct,kind,id,version DESC);
ALTER TABLE finance_revision ENABLE ROW LEVEL SECURITY;
ALTER TABLE finance_revision FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant ON finance_revision USING(acct=current_setting('tracker.account',true))
    WITH CHECK(acct=current_setting('tracker.account',true));
