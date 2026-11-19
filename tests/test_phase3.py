"""Black-box HTTPS / PostgreSQL integration. All data and certificates live in a temporary directory."""
import base64
from contextlib import closing
import datetime as dt
import email
import hashlib
import html
import json
import os
import random
from pathlib import Path
import re
import secrets
import shutil
import socket
import sqlite3
import subprocess
import tempfile
import time
import unittest
import uuid

import psycopg
import requests
from requests.adapters import HTTPAdapter
from urllib3.util.retry import Retry
from aiosmtpd.controller import Controller
from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec, rsa, utils
from cryptography.hazmat.primitives.serialization import pkcs12
from cryptography.x509.oid import NameOID

ROOT = Path(__file__).resolve().parents[1]
BUILD = Path(os.environ.get("LOCALAPPDATA", ".")) / "CodexTrackerBuild"
DOTNET = os.getenv("TRACKER_DOTNET", str(BUILD / "sdk/dotnet.exe"))
PG = Path(os.getenv("TRACKER_PGBIN", str(BUILD / "pgsql-17.12/bin")))
PGTOOLS = Path(os.getenv("TRACKER_PGTOOLS", PG if (PG/"pg_dump.exe").exists() else BUILD/"pg-tools-17.12"))
SERVER = Path(os.getenv("TRACKER_SERVER_DLL", ROOT / "server/Tracker.Server/bin/Release/net10.0/Tracker.Server.dll"))
CLIENT = Path(os.getenv("TRACKER_SYNC_DLL", ROOT / "client/Tracker.Sync/bin/Release/net10.0-windows/Tracker.Sync.dll"))
ID = lambda: str(uuid.uuid4())


def port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def run(args, **kwargs):
    p = subprocess.run(args, capture_output=True, timeout=60, **kwargs)
    if p.returncode:
        raise AssertionError((p.stderr or p.stdout).decode("utf-8", "replace")[-2200:])
    return p


class Mailbox:
    def __init__(self):
        self.messages = []

    async def handle_DATA(self, server, session, envelope):
        self.messages.append((envelope.rcpt_tos, email.message_from_bytes(envelope.content)))
        return "250 accepted"


class FrozenSample(unittest.TestCase):
    @unittest.skipUnless((ROOT/"artifacts/phase1/data/tracker.sqlite").exists(),"optional local Phase 1 acceptance database")
    def test_offline_partial_usage(self):
        # Exercise the exporter offline. All three inputs are temporary copies or local fixture settings.
        with tempfile.TemporaryDirectory(prefix="tracker-frozen-") as tmp:
            folder=Path(tmp); source=folder/"source.sqlite"; store=folder/"state"
            origin=(ROOT/"artifacts/phase1/data/tracker.sqlite").resolve().as_uri()+"?mode=ro"
            with closing(sqlite3.connect(origin,uri=True)) as src, closing(sqlite3.connect(source)) as dest:
                src.backup(dest)
                scopes=dest.execute("select device_id,thread_id from Session").fetchall()
            # Initialize the real schema then seed an offline profile with no usable address or credential.
            run([DOTNET,CLIENT,"--state",store,"flush-logout"])
            with closing(sqlite3.connect(store/"sync.sqlite")) as sidecar:
                sidecar.execute("insert into profile values('fixture','https://offline.invalid','fixture','offline','fixture',0)")
                sidecar.executemany("insert into settings values(?,?)",[("active","fixture"),("refresh/fixture","offline-only")])
                sidecar.executemany("insert into scope(p,db,device,thread) values('fixture',?,?,?)",[(str(source),d,t) for d,t in scopes])
                sidecar.commit()
            run([DOTNET,CLIENT,"--state",store,"collect"])
            with closing(sqlite3.connect(store/"sync.sqlite")) as sidecar:
                sums=sidecar.execute("""
                    select json_extract(fact,'$.confidence'),count(*),sum(json_extract(fact,'$.usage.total'))
                    from manifest where json_extract(fact,'$.type') in ('snapshot','response') group by 1
                    """).fetchall()
                self.assertEqual(sorted(sums),[("DERIVED",108,7518825),("OBSERVED",97,7211991),("PARTIAL",755,58814396)])


def certificate(directory, password):
    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    subject = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "localhost")])
    now = dt.datetime.now(dt.timezone.utc)
    cert = (x509.CertificateBuilder().subject_name(subject).issuer_name(subject).public_key(key.public_key())
            .serial_number(x509.random_serial_number()).not_valid_before(now-dt.timedelta(minutes=1))
            .not_valid_after(now+dt.timedelta(days=2))
            .add_extension(x509.SubjectAlternativeName([x509.DNSName("localhost")]), False)
            .add_extension(x509.BasicConstraints(ca=True,path_length=None), True).sign(key,hashes.SHA256()))
    (directory/"root.pem").write_bytes(cert.public_bytes(serialization.Encoding.PEM))
    (directory/"server.pfx").write_bytes(pkcs12.serialize_key_and_certificates(
        b"test",key,cert,None,serialization.BestAvailableEncryption(password.encode())))


def fields(response):
    response.raise_for_status()
    return {html.unescape(k):html.unescape(v) for k,v in re.findall(
        r"""<input[^>]*name=["']([^"']+)["'][^>]*value=["']([^"']*)["']""",response.text,re.I)}


class Phase3(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        tmp=tempfile.TemporaryDirectory(prefix="tracker-sync-integration-")
        cls.addClassCleanup(tmp.cleanup)
        cls.dir=Path(tmp.name)
        cls.pw=secrets.token_urlsafe(32)
        dbport,webport,mailport=port(),port(),port()
        cls.url=f"https://localhost:{webport}"
        cls.ca=str(cls.dir/"root.pem")
        certificate(cls.dir,cls.pw)
        pwfile=cls.dir/"pw"
        pwfile.write_text(cls.pw,encoding="ascii")
        pgdata=cls.dir/"pg"
        run([PG/"initdb","-D",pgdata,"-U","fixture","--pwfile",pwfile,"-A","scram-sha-256","--locale=C","-E","UTF8"])
        pwfile.unlink()
        with open(cls.dir/"pgctl.log","wb") as log:
            subprocess.run([PG/"pg_ctl","-D",pgdata,"-l",cls.dir/"postgres.log","-o",f"-p {dbport} -h 127.0.0.1","-w","start"],
                           stdin=subprocess.DEVNULL,stdout=log,stderr=log,check=True,timeout=60)
        cls.addClassCleanup(run,[PG/"pg_ctl","-D",pgdata,"-m","fast","-w","stop"])
        cls.pg=psycopg.connect(host="127.0.0.1",port=dbport,dbname="postgres",user="fixture",password=cls.pw,autocommit=True)
        cls.addClassCleanup(cls.pg.close)
        cls.pg.execute("CREATE ROLE tracker_api LOGIN PASSWORD '"+cls.pw+"'")
        cls.mail=Mailbox()
        smtp=Controller(cls.mail,hostname="127.0.0.1",port=mailport)
        smtp.start()
        cls.addClassCleanup(smtp.stop)
        def connection(user):
            return f"Host=127.0.0.1;Port={dbport};Database=postgres;Username={user};Password={cls.pw}"
        cls.env={**os.environ,"ASPNETCORE_URLS":cls.url,"Tracker__Issuer":cls.url,
                 "Tracker__Certificate":str(cls.dir/"server.pfx"),"Tracker__CertificatePassword":cls.pw,
                 "Tracker__KeyDirectory":str(cls.dir/"keys"),"Tracker__OpenRegistration":"true",
                 "Tracker__SecurityDirectory":str(cls.dir/"security"),
                 "Tracker__Epoch":ID(),"ConnectionStrings__Tracker":connection("fixture"),
                 "Kestrel__Certificates__Default__Path":str(cls.dir/"server.pfx"),
                 "Kestrel__Certificates__Default__Password":cls.pw,
                 "Smtp__Host":"127.0.0.1","Smtp__Port":str(mailport),"Smtp__StartTls":"false","Smtp__From":"tracker@example.test"}
        run([DOTNET,SERVER,"--migrate"],env=cls.env)
        run([DOTNET,SERVER,"--migrate"],env=cls.env)
        assert cls.pg.execute("select count(*) from tracker_schema").fetchone()[0]==1
        cls.pg.execute("GRANT SELECT,INSERT,UPDATE,DELETE ON ALL TABLES IN SCHEMA public TO tracker_api")
        cls.pg.execute("GRANT USAGE,SELECT ON ALL SEQUENCES IN SCHEMA public TO tracker_api")
        cls.env["ConnectionStrings__Tracker"]=connection("tracker_api")
        cls.log=open(cls.dir/"server.log","w+b")
        cls.addClassCleanup(cls.log.close)
        cls.start_api()
        cls.addClassCleanup(cls.stop_api)

    @classmethod
    def stop_api(cls):
        if getattr(cls,"process",None) and cls.process.poll() is None:
            cls.process.terminate()
            cls.process.wait(timeout=15)

    @classmethod
    def start_api(cls):
        cls.process=subprocess.Popen([DOTNET,SERVER],env=cls.env,stdout=cls.log,stderr=cls.log)
        for _ in range(100):
            try:
                r=requests.get(cls.url+"/api/v1/capabilities",verify=cls.ca,timeout=1)
                if r.ok:return
            except requests.RequestException: pass
            if cls.process.poll() is not None: break
            time.sleep(.1)
        cls.stop_api()
        cls.log.seek(0)
        raise RuntimeError(cls.log.read().decode("utf-8","replace")[-2000:])

    def browser(self):
        s=requests.Session()
        s.mount("https://",HTTPAdapter(max_retries=Retry(total=2, status_forcelist=[429],
            allowed_methods={"GET","POST"},respect_retry_after_header=True,backoff_factor=1)))
        s.verify=self.ca
        return s

    def register(self):
        s=self.browser()
        addr="test-"+ID()+"@example.test"
        reg=self.url+"/Identity/Account/Register"
        f=fields(s.get(reg))
        f.update({"Input.Email":addr,"Input.Password":self.pw,"Input.ConfirmPassword":self.pw})
        r=s.post(reg,data=f)
        self.assertLess(r.status_code,400,r.text[-300:])
        msg=next(m for to,m in self.mail.messages if addr in to)
        body=msg.get_payload(decode=True).decode()
        link=html.unescape(re.search(r"""href=['"]([^'"]+)""",body).group(1))
        self.assertEqual(s.get(link).status_code,200,"mail verification failed")
        self.login(s,addr)
        return s,addr

    def login(self,s,addr):
        url=self.url+"/Identity/Account/Login"
        f=fields(s.get(url))
        f.update({"Input.Email":addr,"Input.Password":self.pw})
        s.post(url,data=f).raise_for_status()

    def approve(self,s,code):
        url=self.url+"/connect/verify"
        r=s.get(url,params={"user_code":code})
        f=fields(r)
        self.assertIn("__RequestVerificationToken",f, r.text[:400])
        f.update(user_code=code,decision="approve")
        s.post(url,data=f).raise_for_status()

    def device(self,s):
        d=self.browser()
        k=ec.generate_private_key(ec.SECP256R1())
        pub=k.public_key().public_bytes(serialization.Encoding.DER,serialization.PublicFormat.SubjectPublicKeyInfo)
        device=ID()
        form=dict(client_id="tracker-windows",scope="tracker offline_access",device_id=device,
                  device_name="Fixture",public_key=base64.b64encode(pub).decode())
        r=d.post(self.url+"/connect/deviceauthorization",data=form)
        self.assertEqual(r.status_code,200,r.text)
        challenge=r.json()
        self.approve(s,challenge["user_code"])
        dc=challenge["device_code"]
        x,y=utils.decode_dss_signature(k.sign(dc.encode(),ec.ECDSA(hashes.SHA256())))
        proof=base64.b64encode(x.to_bytes(32,"big")+y.to_bytes(32,"big")).decode()
        r=d.post(self.url+"/connect/token",data=dict(grant_type="urn:ietf:params:oauth:grant-type:device_code",
                  client_id="tracker-windows",device_code=dc,proof=proof))
        self.assertEqual(r.status_code,200,r.text)
        d.headers["Authorization"]="Bearer "+r.json()["access_token"]
        d.binding=k
        d.refresh_token=r.json()["refresh_token"]
        state=d.get(self.url+"/api/v1/sync/state");self.assertEqual(state.status_code,200,state.text)
        return d,state.json()

    def push(self,d,state,ops):
        r=d.post(self.url+"/api/v1/sync/push",json=dict(protocol=1,epoch=state["epoch"],operations=ops))
        self.assertEqual(r.status_code,200,r.text)
        return r.json()["results"]

    def source_ops(self):
        rows=[json.loads(s) for s in (ROOT/"tests/fixtures/turn.jsonl").read_text().splitlines()]
        ops=[]
        for r in [r for r in rows if r["type"]=="token_usage_record"]:
            p=r["payload"];u=p["usage"]
            f=dict(type="response",thread_id=p["thread_id"],session_id=p["session_id"],turn_id=p["turn_id"],
                   response_id=p["response_id"],ordinal=r["ordinal"],confidence="OBSERVED",model="gpt-6.2-astra",
                   usage=dict(input=u["input_tokens"],output=u["output_tokens"],total=u["total_tokens"],
                              cache_read=u["cached_input_tokens"],cache_write=u["cache_write_input_tokens"],reasoning=u["reasoning_output_tokens"]))
            ops.append(dict(kind="observation.upsert",op_id=ID(),observation_id=ID(),source_rev=1,fact=f))
        return ops

    def test_receipts_overlap_isolation_and_conflicts(self):
        user,_=self.register(); a,sa=self.device(user); b,sb=self.device(user)
        other,_=self.register(); v,sv=self.device(other)
        ops=self.source_ops()
        first=self.push(a,sa,ops[:2])
        self.assertEqual(self.push(a,sa,ops[:2]),first)
        self.assertEqual(self.push(a,sa,[{**ops[0],"op_id":ID()}])[0]["status"],"duplicate")
        self.assertEqual(self.push(a,sa,[{**ops[0],"source_rev":3}])[0]["status"],"OP_ID_REUSE")
        # Force a PostgreSQL serialization retry after projections but before the receipt commits.
        project_op=dict(op_id=ID(),kind="project.create",resource_id=ID(),base_version=0,value=dict(name="commit test"))
        self.pg.execute("""create function fixture_retry() returns trigger language plpgsql as $$
            begin raise serialization_failure; end $$""")
        self.pg.execute("create trigger fixture_retry before insert on receipt for each row execute function fixture_retry()")
        try:
            fail=a.post(self.url+"/api/v1/sync/push",json=dict(protocol=1,epoch=sa["epoch"],operations=[project_op]))
            self.assertEqual((fail.status_code,fail.headers.get("Retry-After")),(503,"5"))
            self.assertEqual(self.pg.execute("select count(*) from resource where acct=%s and id=%s",(sa["account"],project_op["resource_id"])).fetchone()[0],0)
        finally:self.pg.execute("drop trigger fixture_retry on receipt")
        self.assertEqual(self.push(a,sa,[project_op])[0]["status"],"applied")
        from concurrent.futures import ThreadPoolExecutor
        with ThreadPoolExecutor(max_workers=5) as pool:
            jobs=[pool.submit(self.push,a,sa,ops[:1]) for _ in range(10)]
            self.assertTrue(all(j.result()==first[:1] for j in jobs))
        self.push(b,sb,[{**o,"op_id":ID(),"observation_id":ID()} for o in ops[1:]])
        def events(acct):
            return [row[0] for row in self.pg.execute("select value from resource where acct=%s and kind='event' and value is not null",(acct,))]
        confirmed=events(sa["account"])
        self.assertEqual((len(confirmed),sum(i["usage"]["total"] for i in confirmed)),(3,114148))
        self.assertEqual(events(sv["account"]),[])
        self.push(v,sv,ops)
        self.assertEqual(len(events(sv["account"])),3)
        bad={**ops[0],"op_id":ID(),"observation_id":ID(),"fact":{**ops[0]["fact"],"usage":{**ops[0]["fact"]["usage"],"input":35875,"total":35978}}}
        self.push(b,sb,[bad])
        self.assertEqual(sum(i["usage"]["total"] for i in events(sa["account"]) if i["confidence"]=="OBSERVED"),78171)
        self.push(b,sb,[dict(kind="observation.retract",op_id=ID(),observation_id=bad["observation_id"],source_rev=2)])
        self.assertEqual(sum(i["usage"]["total"] for i in events(sa["account"])),114148)
        self.assertEqual(self.push(b,sb,[{**bad,"op_id":ID()}])[0]["status"],"superseded")
        collision={**bad,"op_id":ID(),"observation_id":ops[0]["observation_id"]}
        source_conflict=self.push(a,sa,[collision])[0]
        self.assertEqual(source_conflict["status"],"recorded_conflict")
        self.assertEqual(sum(i["usage"]["total"] for i in events(sa["account"]) if i["confidence"]=="OBSERVED"),78171)
        self.push(a,sa,[{**ops[0],"op_id":ID(),"source_rev":2}])
        self.assertEqual(self.pg.execute("select value from resource where acct=%s and kind='conflict' and id=%s",
                         (sa["account"],source_conflict["conflict_id"])).fetchone(),(None,))
        snap=a.post(self.url+"/api/v1/sync/snapshots").json()
        self.assertEqual(v.get(self.url+f"/api/v1/sync/snapshots/{snap['id']}/pages/0").status_code,404)
        self.assertEqual(hashlib.sha256(a.get(self.url+f"/api/v1/sync/snapshots/{snap['id']}/pages/0").content).hexdigest(),snap["hashes"][0])
        badwire={**ops[0],"fact":{**ops[0]["fact"],"default_title":"PRIVATE_SENTINEL"}}
        r=a.post(self.url+"/api/v1/sync/push",json=dict(protocol=1,epoch=sa["epoch"],operations=[badwire]))
        self.assertIn(r.status_code,(400,422))
        project=ID()
        def edit(kind,version,name):
            return dict(op_id=ID(),kind=kind,resource_id=project,base_version=version,value=dict(name=name))
        self.push(a,sa,[edit("project.create",0,"Project")])
        self.push(a,sa,[edit("project.update",1,"Desktop")])
        conflict=self.push(b,sb,[edit("project.update",1,"Laptop")])[0]
        self.assertEqual(conflict["status"],"recorded_conflict")
        self.assertIsNotNone(conflict["conflict_id"])
        resolution=edit("project.update",2,"Agreed")
        resolution["value"]["conflict_id"]=conflict["conflict_id"]
        self.assertEqual(self.push(b,sb,[resolution])[0]["status"],"applied")
        self.assertEqual(self.pg.execute("select version,value->>'name' from resource where acct=%s and kind='project' and id=%s",
                         (sa["account"],project)).fetchone(),(3,"Agreed"))
        self.assertEqual(self.pg.execute("select value from resource where acct=%s and kind='conflict' and id=%s",
                         (sa["account"],conflict["conflict_id"])).fetchone(),(None,))
        snapshots=a.get(self.url+"/api/v1/sync/changes",params=dict(epoch=sa["epoch"],after=0,limit=1)).json()
        self.assertTrue(snapshots["has_more"]);self.assertEqual(snapshots["next"],1)
        verify=fields(user.get(self.url+"/devices"))
        verify.update(id=sa["device"],version=1,action="revoke")
        user.post(self.url+"/devices",data=verify).raise_for_status()
        self.assertIn(a.get(self.url+"/api/v1/sync/state").status_code,(401,403))
        for path in ("/api/v1/devices/self/heartbeat","/api/v1/sync/snapshots"):
            self.assertIn(a.post(self.url+path).status_code,(401,403))
        self.assertIn(a.post(self.url+"/api/v1/sync/push",json=dict(protocol=1,epoch=sa["epoch"],operations=ops)).status_code,(401,403))
        self.assertIn(a.get(self.url+f"/api/v1/sync/snapshots/{snap['id']}/pages/0").status_code,(401,403))
        x,y=utils.decode_dss_signature(a.binding.sign(a.refresh_token.encode(),ec.ECDSA(hashes.SHA256())))
        rejected=a.post(self.url+"/connect/token",data=dict(client_id="tracker-windows",grant_type="refresh_token",refresh_token=a.refresh_token,
                        proof=base64.b64encode(x.to_bytes(32,"big")+y.to_bytes(32,"big")).decode()))
        self.assertEqual(rejected.json()["error"],"invalid_grant")
        self.assertEqual(b.get(self.url+"/api/v1/sync/state").status_code,200)

    def cli(self,folder,*args):
        return run([DOTNET,CLIENT,"--state",str(folder),*map(str,args)]).stdout.decode("utf-8").strip()

    def cli_pair(self,user,folder,alias="default"):
        from urllib.parse import urlparse,parse_qs
        proc=subprocess.Popen([DOTNET,CLIENT,"--state",str(folder),"--profile",alias,"--ca",self.ca,"--no-browser","login",self.url,"Fixture"],
                              stdout=subprocess.PIPE,stderr=subprocess.PIPE,encoding="utf-8")
        try:
            proc.stdout.readline()
            url=proc.stdout.readline().strip()
            if not url: self.fail(proc.stderr.read()[-400:])
            self.approve(user,parse_qs(urlparse(url).query)["user_code"][0])
            out,err=proc.communicate(timeout=30)
            self.assertEqual(proc.returncode,0,err)
        finally:
            if proc.poll() is None:proc.kill()
            proc.stdout.close();proc.stderr.close()

    def test_released_worker_two_offline_clients(self):
        user,_=self.register()
        peers=[]
        for i in (1,2):
            path=self.dir/("pc-"+str(i));path.mkdir()
            src=path/"codex"; sessions=src/"sessions";sessions.mkdir(parents=True)
            shutil.copyfile(ROOT/"tests/fixtures/turn.jsonl",sessions/"rollout-turn.jsonl")
            source=path/"tracker.sqlite"; profile=path/"profile"
            run([ROOT/"artifacts/phase2/windows-x64/CodexTracker.exe","scan","--root",src,"--device","fixture","--db",source])
            marker="PRIVATE-DO-NOT-SYNC-TEST"
            with closing(sqlite3.connect(source)) as fixture:
                fixture.execute("update Event set data=json_set(data,'$.prompt',?,'$.source_code',?,'$.default_title',?)",(marker,marker,marker))
                fixture.execute("update SourceRecord set raw_metering=?",(marker,))
                fixture.commit()
            self.cli_pair(user,profile)
            self.cli(profile,"allow",source,"fixture","thread-A")
            self.cli(profile,"collect"); self.cli(profile,"collect")
            with closing(sqlite3.connect(profile/"sync.sqlite")) as local:
                self.assertTrue(all(marker not in row[0] for row in local.execute("select body from outbox")))
            self.assertGreater(json.loads(self.cli(profile,"status"))["pending"],2)
            peers.append(profile)
        for profile in peers+peers:self.cli(profile,"sync")
        device_rows=json.loads(self.cli(peers[0],"devices"))
        self.assertEqual(len(device_rows),2)
        self.assertTrue(all(r["os"].startswith("Windows ") and r["client_version"]=="3.0.0" and r["last_sync"] for r in device_rows))
        for profile in peers:
            events=[json.loads(line)["value"] for line in self.cli(profile,"show","event").splitlines()]
            self.assertEqual(len(events),3)
            self.assertEqual(sum(i["usage"]["total"] for i in events),114148)
            self.assertEqual(sum(i["sources"] for i in events),6)
            for field,total in {"input":109911,"output":4237,"cache_read":106368,"cache_write":0,"reasoning":49}.items():
                self.assertEqual(sum(i["usage"][field] for i in events),total)
            self.assertEqual(json.loads(self.cli(profile,"status"))["pending"],0)
        # A corrupt snapshot and an interrupted replica transaction must both preserve the previous replica.
        profile=peers[0]; before=self.cli(profile,"show"); pending_edit=self.cli(profile,"edit","project.create",ID(),0,'{"name":"preserve queue"}')
        with closing(sqlite3.connect(profile/"sync.sqlite")) as local:
            local.execute("update profile set cursor=9000000000");local.commit()
        self.pg.execute("""create function fixture_page() returns trigger language plpgsql as $$
            begin new.content=new.content||' '; return new; end $$""")
        self.pg.execute("create trigger fixture_page before insert on snapshot_page for each row execute function fixture_page()")
        def failed_snapshot():
            result=subprocess.run([DOTNET,CLIENT,"--state",profile,"sync"],capture_output=True,timeout=25)
            self.assertNotEqual(result.returncode,0)
            self.assertEqual(self.cli(profile,"show"),before)
            with closing(sqlite3.connect(profile/"sync.sqlite")) as local:
                self.assertEqual(local.execute("select cursor from profile").fetchone()[0],9000000000)
                self.assertEqual(local.execute("select id from outbox").fetchall(),[(pending_edit,)])
        try:failed_snapshot()
        finally:self.pg.execute("drop trigger fixture_page on snapshot_page")
        with closing(sqlite3.connect(profile/"sync.sqlite")) as local:
            local.execute("create trigger fixture_disk before insert on replica begin select raise(abort,'fixture disk fault'); end")
        try:failed_snapshot()
        finally:
            with closing(sqlite3.connect(profile/"sync.sqlite")) as local:local.execute("drop trigger fixture_disk")
        self.cli(profile,"sync")
        self.assertEqual(json.loads(self.cli(profile,"review"))["op_id"],pending_edit)
        self.cli(profile,"review-dismiss",pending_edit)
        # Replacing the selected input by a different file identity cannot retract its already observed rows.
        original=self.dir/"pc-1/tracker.sqlite"
        original.rename(original.with_suffix(".original"))
        shutil.copyfile(original.with_suffix(".original"),original)
        self.cli(peers[0],"edit","project.create",ID(),0,json.dumps(dict(name="offline edit")))
        result=subprocess.run([DOTNET,CLIENT,"--state",peers[0],"sync"],capture_output=True)
        self.assertEqual(result.returncode,2)
        self.assertIn("offline edit",self.cli(peers[0],"show","project"))
        self.assertTrue(json.loads(self.cli(peers[0],"scopes"))["review"])
        self.assertEqual(json.loads(self.cli(peers[0],"status"))["pending"],0)
        self.cli(peers[0],"logout")
        with closing(sqlite3.connect(peers[0]/"sync.sqlite")) as local:
            self.assertEqual(local.execute("SELECT count(*) FROM settings WHERE k='active' OR k LIKE 'refresh/%'").fetchone()[0],0)
    def test_account_switch_scope_and_offline_queue(self):
        u,_=self.register();v,_=self.register()
        path=self.dir/"switch";path.mkdir()
        src=path/"src"; sessions=src/"sessions";sessions.mkdir(parents=True)
        shutil.copyfile(ROOT/"tests/fixtures/turn.jsonl",sessions/"rollout-switch.jsonl")
        db=path/"tracker.sqlite"; store=path/"state"
        run([ROOT/"artifacts/phase2/windows-x64/CodexTracker.exe","scan","--root",src,"--device","A","--db",db])
        self.cli_pair(u,store)
        with closing(sqlite3.connect(store/"sync.sqlite")) as local:
            old_grant=local.execute("select v from settings where k like 'grant/%'").fetchone()[0]
        self.cli(store,"allow",db,"A","thread-A")
        self.stop_api()
        try:
            self.cli(store,"collect")
            failed=subprocess.run([DOTNET,CLIENT,"--state",str(store),"sync"],capture_output=True,timeout=20)
            self.assertNotEqual(failed.returncode,0)
            self.assertGreater(json.loads(self.cli(store,"status"))["pending"],0)
            offline=subprocess.run([DOTNET,CLIENT,"--state",str(store),"logout"],capture_output=True,timeout=20)
            with closing(sqlite3.connect(store/"sync.sqlite")) as local:
                self.assertEqual(local.execute("select count(*) from settings where k='active'").fetchone()[0],0)
        finally:self.start_api()
        self.cli_pair(v,store,alias="second-user")
        self.assertTrue(self.pg.execute("select revoked from grant_state where id=%s",(old_grant,)).fetchone()[0])
        self.assertEqual(json.loads(self.cli(store,"status"))["pending"],0)
        denied=subprocess.run([DOTNET,CLIENT,"--state",str(store),"allow",str(db),"A","thread-A"],capture_output=True)
        self.assertNotEqual(denied.returncode,0)
        self.cli(store,"sync");self.assertEqual(self.cli(store,"show","event"),"")
        self.cli(store,"logout");self.cli_pair(u,store)
        self.cli(store,"sync")
        self.assertEqual(sum(json.loads(x)["value"]["usage"]["total"] for x in self.cli(store,"show","event").splitlines()),114148)

    def test_timing_legacy_and_concurrent_edit(self):
        from concurrent.futures import ThreadPoolExecutor
        s,_=self.register(); a,sa=self.device(s);b,sb=self.device(s)
        def obs(f):
            return dict(op_id=ID(),kind="observation.upsert",observation_id=ID(),source_rev=1,fact=dict(thread_id="thread-A",**f))
        u=dict(input=30,output=5,cache_read=0,cache_write=0,reasoning=0,total=35)
        boundary=obs(dict(type="timing",kind="BOUNDARY",start_ms=0,end_ms=100))
        legacy=obs(dict(type="snapshot",session_id="S",turn_id="old",ordinal=2,occurred_ms=50,usage=u,thread_cumulative=u,confidence="DERIVED"))
        self.push(a,sa,[obs(dict(type="lineage",session_id="S")),legacy,boundary,
                        obs(dict(type="timing",kind="ACTIVE",start_ms=0,end_ms=20)),
                        obs(dict(type="timing",kind="PAUSED",start_ms=40,end_ms=60))])
        self.push(b,sb,[obs(dict(type="timing",kind="ACTIVE",start_ms=10,end_ms=30))])
        def values(kind):
            return [r[0] for r in self.pg.execute("select value from resource where acct=%s and kind=%s and value is not null",(sa["account"],kind))]
        self.assertEqual(values("event")[0]["confidence"],"DERIVED")
        self.assertEqual([values("timing")[0][k] for k in ("active_ms","paused_ms","unknown_ms")],[30,20,50])
        self.push(b,sb,[obs(dict(type="timing",kind="PAUSED",start_ms=10,end_ms=20)),self.source_ops()[0]])
        self.assertEqual([values("timing")[0][k] for k in ("active_ms","paused_ms","unknown_ms")],[20,20,60])
        self.assertEqual(sorted(e["confidence"] for e in values("event")),["OBSERVED","PARTIAL"])
        project=ID()
        def update(kind,name,base):
            return dict(kind=kind,op_id=ID(),resource_id=project,base_version=base,value=dict(name=name))
        self.push(a,sa,[update("project.create","Test",0)])
        with ThreadPoolExecutor() as pool:
            results=[pool.submit(self.push,d,state,[update("project.update",name,1)]) for d,state,name in [(a,sa,"Alpha"),(b,sb,"Beta")]]
            self.assertEqual(sorted(r.result()[0]["status"] for r in results),["applied","recorded_conflict"])
        self.assertEqual(len(values("project")),1)

    def recover_password(self,addr):
        browser=self.browser()
        url=self.url+"/Identity/Account/ForgotPassword"
        form=fields(browser.get(url));form["Input.Email"]=addr
        browser.post(url,data=form).raise_for_status()
        message=next(msg for recipients,msg in reversed(self.mail.messages) if addr in recipients)
        body=message.get_payload(decode=True).decode()
        link=html.unescape(re.search(r"""href=['"]([^'"]+)""",body).group(1))
        form=fields(browser.get(link));form.update({"Input.Email":addr,"Input.Password":self.pw,"Input.ConfirmPassword":self.pw})
        browser.post(link,data=form).raise_for_status()
        self.login(browser,addr)
        return browser

    def test_z_database_restore_after_ack(self):
        cls=type(self)
        user,addr=self.register(); folder=self.dir/"recovery"; folder.mkdir()
        source=folder/"src";sessions=source/"sessions";sessions.mkdir(parents=True)
        rows=(ROOT/"tests/fixtures/turn.jsonl").read_bytes().splitlines(keepends=True)
        rollout=sessions/"rollout-recovery.jsonl";rollout.write_bytes(b"".join(rows[:6]))
        db=folder/"input.sqlite";store=folder/"state"
        def scan():
            run([ROOT/"artifacts/phase2/windows-x64/CodexTracker.exe","scan","--root",source,"--device","A","--db",db])
        scan();self.cli_pair(user,store);self.cli(store,"allow",db,"A","thread-A");self.cli(store,"sync")
        oldtoken,old=self.device(user)
        conninfo=cls.pg.info.get_parameters()
        def admin(database):
            return psycopg.connect(host="127.0.0.1",port=conninfo["port"],user="fixture",password=cls.pw,dbname=database,autocommit=True)
        archive=folder/"tracker.dump"
        pg_options=["-h","127.0.0.1","-p",conninfo["port"],"-U","fixture"]
        pg_env={**os.environ,"PGPASSWORD":cls.pw}
        run([PGTOOLS/"pg_dump",*pg_options,"-Fc","-f",archive,"postgres"],env=pg_env)
        project_id=ID()
        intent=self.cli(store,"edit","project.create",project_id,0,json.dumps(dict(name="recover intent")))
        rollout.write_bytes(b"".join(rows));scan();self.cli(store,"sync")
        self.assertEqual(json.loads(self.cli(store,"status"))["pending"],0)
        token=fields(user.get(self.url+"/devices"));token.update(id=old["device"],version=1,action="revoke")
        user.post(self.url+"/devices",data=token).raise_for_status()
        cls.stop_api()
        recovery_started=time.monotonic()
        with admin("template1") as ctl:ctl.execute("CREATE DATABASE recovered")
        run([PGTOOLS/"pg_restore",*pg_options,"--exit-on-error","--no-owner","-d","recovered",archive],env=pg_env)
        with admin("recovered") as restored:
            totals=restored.execute("select count(*),sum((value->'usage'->>'total')::bigint) from resource where acct=%s and kind='event' and value is not null",(old["account"],)).fetchone()
            self.assertEqual(totals,(1,35977))
        cls.env["Tracker__Epoch"]=ID()
        cls.env["ConnectionStrings__Tracker"]=cls.env["ConnectionStrings__Tracker"].replace("Database=postgres","Database=recovered")
        migrate={**cls.env,"ConnectionStrings__Tracker":cls.env["ConnectionStrings__Tracker"].replace("Username=tracker_api","Username=fixture")}
        run([DOTNET,SERVER,"--recover"],env=migrate)
        run([DOTNET,SERVER,"--release-account",old["account"],addr],env=migrate)
        cls.start_api()
        self.assertIn(oldtoken.get(self.url+"/api/v1/sync/state").status_code,(401,403))
        self.assertEqual(self.cli(store,"logout"),"已退出。")
        restored_user=self.recover_password(addr)
        self.cli_pair(restored_user,store);self.cli(store,"sync")
        events=[json.loads(l)["value"] for l in self.cli(store,"show","event").splitlines()]
        self.assertEqual(sum(v["usage"]["total"] for v in events),114148)
        print(f"\nRestore drill: {archive.stat().st_size} bytes; 35,977 at restore point -> 114,148 after replay; {time.monotonic()-recovery_started:.1f}s in test environment.",flush=True)
        self.assertEqual(json.loads(self.cli(store,"review"))["op_id"],intent)
        self.assertEqual(self.cli(store,"show","project"),"")
        self.cli(store,"review-apply",intent,0);self.cli(store,"sync")
        self.assertEqual(json.loads(self.cli(store,"show","project"))["value"]["name"],"recover intent")
        self.assertTrue((cls.dir/"security/revoked.log").read_text().find(old["device"])>=0)
        cls.stop_api()
        frozen={**migrate,"Tracker__Epoch":ID(),"Tracker__SecurityDirectory":str(cls.dir/"fresh-security")}
        failed=subprocess.run([DOTNET,SERVER,"--recover"],env=frozen,capture_output=True)
        self.assertNotEqual(failed.returncode,0)
        run([DOTNET,SERVER,"--recover-frozen"],env=frozen)
        with admin("recovered") as restored:
            self.assertEqual(restored.execute("select count(*) from device where not revoked").fetchone()[0],0)
            self.assertEqual(restored.execute('select count(*) from "AspNetUsers" where "EmailConfirmed"').fetchone()[0],0)
    def test_group_membership_same_title_and_partial_lineage(self):
        user,_=self.register();d,state=self.device(user)
        def obs(f):
            return dict(op_id=ID(),kind="observation.upsert",observation_id=ID(),source_rev=1,fact=f)
        group=ID();project=ID()
        def edit(kind,rid,value,base=0):
            return dict(kind=kind,op_id=ID(),resource_id=rid,base_version=base,value=value)
        ops=[]
        for thread,session in [("parent","session-a"),("child","session-b")]:
            ops.append(obs(dict(type="lineage",thread_id=thread,session_id=session,**({"parent_id":"parent","subagent":True} if thread=="child" else {}))))
        self.push(d,state,ops+[edit("project.create",project,dict(name="客户项目")),edit("group.create",group,dict(name="同名任务",project_id=project))])
        self.push(d,state,[edit("member.set",ID(),dict(group_id=group,session_id=s)) for s in ("session-a","session-b")])
        n=self.pg.execute("select count(*) from resource where acct=%s and kind='member' and value is not null",(state["account"],)).fetchone()[0]
        self.assertEqual(n,2)
        u=dict(input=30,output=5,cache_read=0,cache_write=0,reasoning=0,total=35)
        base=dict(type="snapshot",turn_id="turn-A",ordinal=4,occurred_ms=1,usage=u,thread_cumulative=u,confidence="DERIVED")
        self.push(d,state,[obs(dict(**base,thread_id="parent")),obs(dict(**base,thread_id="child"))])
        result={r[0]["thread_id"]:r[0]["confidence"] for r in self.pg.execute("select value from resource where acct=%s and kind='event'",(state["account"],))}
        self.assertEqual(result,{"parent":"DERIVED","child":"PARTIAL"})
        for query in ("", "?group="+group, "?project="+project):
            page=user.get(self.url+"/dashboard"+query)
            page.raise_for_status()
            self.assertIn("<td>35</td><td>1</td>",page.text)
        empty=user.get(self.url+"/dashboard?group="+ID())
        self.assertNotIn("<td>35</td>",empty.text)
        duplicate=edit("member.set",ID(),dict(group_id=group,session_id="session-a"))
        self.assertEqual(self.push(d,state,[duplicate])[0]["status"],"recorded_conflict")
        self.push(d,state,[edit("project.delete",project,None,1)])
        v=self.pg.execute("select value from resource where acct=%s and kind='group'",(state["account"],)).fetchone()[0]
        self.assertNotIn("project_id",v)

    def test_worker_same_titles_and_child(self):
        owner,_=self.register()
        folder=self.dir/"same-titles"; sessions=folder/"codex/sessions";sessions.mkdir(parents=True)
        raw=(ROOT/"tests/fixtures/turn.jsonl").read_text(encoding="utf-8")
        for suffix in ("A","B"):
            rows=[json.loads(line) for line in raw.replace("thread-A","thread-"+suffix).replace("session-A","session-"+suffix).replace("turn-A","turn-"+suffix).splitlines()]
            rows[0]["payload"]["title"]="PRIVATE-DUPLICATE-TITLE"
            (sessions/("rollout-"+suffix+".jsonl")).write_text("\n".join(map(json.dumps,rows))+"\n",encoding="utf-8")
        shutil.copyfile(ROOT/"tests/fixtures/child.jsonl",sessions/"rollout-child.jsonl")
        db=folder/"source.sqlite"; profile=folder/"sync"
        run([ROOT/"artifacts/phase2/windows-x64/CodexTracker.exe","scan","--root",folder/"codex","--device","desk","--db",db])
        self.cli_pair(owner,profile)
        for tid in ("thread-A","thread-B","child-A"):self.cli(profile,"allow",db,"desk",tid)
        self.cli(profile,"sync")
        self.assertEqual(self.cli(profile,"show","member"),"")
        threads=[json.loads(line)["value"] for line in self.cli(profile,"show","thread").splitlines()]
        self.assertEqual({r["thread_id"] for r in threads},{"thread-A","thread-B","child-A"})
        child=next(r for r in threads if r["thread_id"]=="child-A")
        self.assertTrue(child["subagent"]);self.assertIn("thread-A",child["parents"])
        events=[json.loads(line)["value"] for line in self.cli(profile,"show","event").splitlines()]
        self.assertEqual((len(events),sum(r["usage"]["total"] for r in events)),(6,228296))
        self.assertNotIn("PRIVATE-DUPLICATE-TITLE",self.cli(profile,"show"))

    def test_seeded_replay_and_retraction(self):
        owner,_=self.register();a,sa=self.device(owner);b,sb=self.device(owner)
        for seed in (13,71,113):
            tid="replay-"+str(seed)
            one,two,three=self.source_ops()
            for item in (one,two,three):item["fact"]["thread_id"]=tid
            clone={**two,"observation_id":ID(),"op_id":ID()}
            redacted={**one,"fact":{**one["fact"],"confidence":"PARTIAL"},"observation_id":ID(),"op_id":ID()}
            retract=dict(kind="observation.retract",observation_id=redacted["observation_id"],source_rev=2,op_id=ID())
            jobs=[(a,sa,one),(a,sa,two),(b,sb,clone),(b,sb,three),(a,sa,redacted),(a,sa,retract)]*2
            random.Random(seed).shuffle(jobs)
            for peer,state,op in jobs:self.push(peer,state,[op])
            events=[r[0] for r in self.pg.execute("select value from resource where acct=%s and kind='event' and value->>'thread_id'=%s",(sa["account"],tid))]
            self.assertEqual(sorted((e["usage"]["total"],e["sources"],e["confidence"]) for e in events),
                             [(35977,1,"OBSERVED"),(37963,2,"OBSERVED"),(40208,1,"OBSERVED")],seed)

    def test_row_policy_digest_and_privacy(self):
        owner,_=self.register();peer,_=self.register();a,sa=self.device(owner);b,sb=self.device(peer)
        ops=self.source_ops()
        for s,st in ((a,sa),(b,sb)):self.push(s,st,ops[:1])
        def key(parts):
            return hashlib.sha256(b"".join(len(p.encode()).to_bytes(4,"big")+p.encode() for p in parts)).hexdigest()
        keycheck=key(["ct.response.v1","00000000-0000-4000-8000-000000000001","thread-A","response-1"])
        self.assertEqual(keycheck,"c937a11d7a7f50b7f4a635941ddff2f7175bb9f8db09dca0d8910e3bafad3764")
        with psycopg.connect(host="127.0.0.1",port=self.pg.info.port,dbname="postgres",user="tracker_api",password=self.pw) as runtime:
            self.assertEqual(runtime.execute("SELECT count(*) FROM resource").fetchone()[0],0)
            runtime.execute("select set_config('tracker.account',%s,true)",(sa["account"],))
            entries=runtime.execute("select acct,id from resource where kind='event'").fetchall()
            self.assertEqual(entries,[(sa["account"],key(["ct.response.v1",sa["account"],"thread-A","response-1"]))])
        op=dict(op_id=ID(),kind="project.create",resource_id=ID(),base_version=0,value=dict(name="名称"))
        self.push(a,sa,[op])
        digest=self.pg.execute("select digest from receipt where op_id=%s",(op["op_id"],)).fetchone()[0]
        canonical=json.dumps(op,sort_keys=True,ensure_ascii=False,separators=(",",":")).encode()
        self.assertEqual(digest,hashlib.sha256(canonical).hexdigest())
        for keyname in ("prompt","source_code","openai_key","raw_metering","default_title","response_text","path"):
            bad={**ops[0],"fact":{**ops[0]["fact"],keyname:"NOT_FOR_SYNC"}}
            r=a.post(self.url+"/api/v1/sync/push",json=dict(protocol=1,epoch=sa["epoch"],operations=[bad]))
            self.assertIn(r.status_code,(400,422))
            self.assertNotIn("NOT_FOR_SYNC",r.text)
        self.assertEqual(self.pg.execute("select count(*) from observation where fact::text like '%NOT_FOR_SYNC%'").fetchone()[0],0)
        # A gap cannot silently move a client past a pruned change group.
        self.pg.execute("delete from change_log where acct=%s and seq=1",(sa["account"],))
        gap=a.get(self.url+"/api/v1/sync/changes",params={"epoch":sa["epoch"],"after":0})
        self.assertEqual(gap.status_code,410)
    def test_browser_and_device(self):
        s,_=self.register()
        d,state=self.device(s)
        self.assertEqual(state["head"],0)
        self.assertEqual(d.get(self.url+"/api/v1/devices").json()[0]["status"],"online")
        second,other=self.device(s)
        manage=fields(s.get(self.url+"/devices"));manage.update(id=state["device"],version=1,action="rename",name="Desk")
        self.assertEqual(s.post(self.url+"/devices",data={k:v for k,v in manage.items() if k!="__RequestVerificationToken"}).status_code,400)
        s.post(self.url+"/devices",data=manage).raise_for_status()
        self.pg.execute("update device set last_active=now()-interval '4 minutes' where id=%s",(state["device"],))
        rows={r["device_id"]:r for r in second.get(self.url+"/api/v1/devices").json()}
        self.assertEqual((rows[state["device"]]["name"],rows[state["device"]]["status"],rows[state["device"]]["last_sync"]),("Desk","offline",None))
        page=s.get(self.url+"/devices")
        self.assertIn("最近完整同步",page.text)
        s.post(self.url+"/account/logout-all",data=fields(page)).raise_for_status()
        for peer in (d,second):
            self.assertIn(peer.get(self.url+"/api/v1/sync/state").status_code,(401,403))


if __name__=="__main__":
    unittest.main(verbosity=2)
