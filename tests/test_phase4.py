"""Cost acceptance through the published HTTP forms, instrumented with the Phase 3 local test environment."""
import datetime as dt
from decimal import Decimal
import json
import os
import sqlite3
import unittest
from urllib.parse import parse_qs, urlparse

import test_phase3 as p3

UTC = dt.timezone.utc
BASE = dict(plan="个人订阅", start="2023-11-01", end="2023-12-01", zone="UTC", currency="CNY", paid="100",
            paid_cny="100", payment_date="2023-11-01", api_currency="USD", weight_set="w1", covered_through="2023-12-01T00:00")
RATE = dict(type="PRICE", unit="USD", model="gpt-6.2-astra", speed="any", **{"from":"2023-01-01T00:00"},
            input="2", read="0.5", write="", output="8", write_basis="unknown", source="https://fixture.example/prices")
WEIGHT = {**RATE,"type":"WEIGHT","unit":"w1","input":"1","read":"1","write":"1","output":"1","source":"fixture uniform weighting"}
FIXTURE = p3.ROOT/"tests/fixtures/turn.jsonl"
def ms(stamp):
    return int(dt.datetime.fromisoformat(stamp.replace("Z","+00:00")).timestamp()*1000)


class Costs(p3.Phase3):
    def write(self, user, kind, data, key=None, version=0, expect=302):
        page=user.get(self.url+"/costs")
        form={"__RequestVerificationToken":p3.fields(page)["__RequestVerificationToken"],
              "kind":kind,"id":key or p3.ID(),"version":version,**data}
        result=user.post(self.url+"/costs",data=form,allow_redirects=False)
        self.assertEqual(result.status_code,expect,result.text[-350:])
        if expect==302:return parse_qs(urlparse(result.headers["Location"]).query)["edit"][0]

    def report(self,user,bill,digest=None):
        q=dict(handler="Report",billId=bill)
        if digest:q["digest"]=digest
        r=user.get(self.url+"/costs",params=q)
        r.raise_for_status()
        return r.json(parse_float=Decimal)

    def complete(self,device,state):
        now=device.get(self.url+"/api/v1/sync/state").json()
        r=device.post(self.url+"/api/v1/sync/complete",params=dict(epoch=state["epoch"],cursor=now["head"],until=now["head"]))
        r.raise_for_status()

    def fixture_ops(self):
        rows=[json.loads(line) for line in FIXTURE.read_text(encoding="utf8").splitlines()]
        stamps=[ms(r["timestamp"]) for r in rows if r["type"]=="token_usage_record"]
        ops=self.source_ops()
        for item,stamp in zip(ops,stamps,strict=True):item["fact"]["occurred_ms"]=stamp
        return ops

    def test_fixture_pricing_and_revision(self):
        user,_=self.register();a,sa=self.device(user);b,sb=self.device(user)
        bill=self.write(user,"bill",BASE)
        rate=self.write(user,"rule",RATE)
        self.write(user,"rule",WEIGHT)
        ops=self.fixture_ops()
        for peer,state in ((a,sa),(b,sb)):
            self.push(peer,state,ops)
        incomplete=self.report(user,bill)
        self.assertIn("DEVICE_SYNC_PENDING",incomplete["reasons"])
        for peer,state in ((a,sa),(b,sb)):self.complete(peer,state)
        calc=self.report(user,bill)
        self.assertEqual(calc["known_api"],Decimal("0.094166"))
        self.assertEqual({k:sum(row["api"]["components"][k] for row in calc["evidence"]) for k in ("INPUT","CACHE_READ","CACHE_WRITE","OUTPUT")},
                         {"INPUT":Decimal("0.007086"),"CACHE_READ":Decimal("0.053184"),"CACHE_WRITE":0,"OUTPUT":Decimal("0.033896")})
        self.assertEqual((len(calc["evidence"]),calc["lines"][0]["share"],calc["lines"][0]["allocation"]),(3,100,100))
        self.assertEqual((calc["basis"],calc["api_status"],calc["reasons"]),("ESTIMATED","ESTIMATED",[]))
        page=user.get(self.url+"/costs",params={"billId":bill})
        self.assertIn("0.094166",page.text)
        store=user.post(self.url+"/costs?handler=Archive",data=dict(billId=bill,__RequestVerificationToken=p3.fields(page)["__RequestVerificationToken"]))
        store.raise_for_status()
        digest=self.pg.execute("select digest from cost_statement where bill=%s",(bill,)).fetchone()[0]
        boundary=dt.datetime.fromtimestamp(ops[1]["fact"]["occurred_ms"]/1000,UTC).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3]
        self.write(user,"rule",{**RATE,"until":boundary},rate,1)
        self.write(user,"rule",{**RATE,"from":boundary,"output":"16"})
        revised=self.report(user,bill)
        self.assertEqual(revised["known_api"],Decimal("0.094166")+Decimal(4237-103)*8/1000000)
        self.assertEqual(self.report(user,bill,digest)["known_api"],Decimal("0.094166"))
        for peer,state in ((a,sa),(b,sb)):
            self.push(peer,state,[dict(op_id=p3.ID(),observation_id=ops[0]["observation_id"],source_rev=2,kind="observation.retract")])
        self.assertEqual(len(self.report(user,bill)["evidence"]),2)
        a.post(self.url+"/api/v1/costs/current?complete=false").raise_for_status()
        self.assertIn("DEVICE_SYNC_PENDING",self.report(user,bill)["reasons"])

    def test_official_allocation_and_cash(self):
        user,_=self.register()
        bill=self.write(user,"bill",BASE)
        self.assertEqual(self.write(user,"bill",BASE),bill)
        self.write(user,"bill",{**BASE,"paid":"110","paid_cny":"110"},expect=409)
        sheet=dict(bill_id=bill,start="2023-11-01T00:00",through="2023-12-01T00:00",evidence="SETTLED",source="receipt-1",
                   total="4",parts="thread-A=2\nthread-B=1\n@pool=1")
        sid=self.write(user,"credits",sheet)
        result=self.report(user,bill)
        self.assertEqual(result["basis"],"OFFICIAL")
        self.assertEqual({l["thread"]:l["allocation_cny"] for l in result["lines"]},{"thread-A":50,"thread-B":25,"@pool":25})
        self.assertEqual(sum(l["share"] for l in result["lines"]),100)
        reset=dict(bill_id=bill,at="2023-11-15T08:00",type="GLOBAL",reference="reset-proof",before="2",after="20",cash="0",currency="CNY")
        rid=self.write(user,"reset",reset)
        self.assertEqual(self.write(user,"reset",reset),rid)
        self.write(user,"reset",{**reset,"cash":"10"},expect=400)
        self.write(user,"reset",{**reset,"type":"PAID","reference":"paid-receipt","cash":"8.50"})
        self.write(user,"reset",{**reset,"type":"UNKNOWN","reference":"unknown","cash":""})
        current=self.report(user,bill)
        self.assertEqual((current["bill"]["paid"],len(current["resets"]),sum(l["allocation"] for l in current["lines"])),(100,3,100))
        self.write(user,"bill",{**BASE,"paid":"0.01","paid_cny":"0.01"},bill,1)
        self.write(user,"credits",{**sheet,"total":"3","parts":"thread-A=1\nthread-B=1\n@pool=1"},sid,1)
        lines=self.report(user,bill)["lines"]
        self.assertEqual(sum(l["allocation"] for l in lines),Decimal("0.01"))
        self.assertEqual(next(l["thread"] for l in lines if l["allocation"]>0),"@pool")
        self.write(user,"credits",{**sheet,"parts":"thread-A=1"},sid,2)
        self.assertEqual(self.report(user,bill)["basis"],"ESTIMATED")
        for observation in ("BALANCE", "LIFETIME_ESTIMATE"):
            with self.subTest(observation=observation):
                self.write(user,"credits",{**sheet,"evidence":observation})
                report = self.report(user,bill)
                self.assertEqual(report["basis"], "ESTIMATED")
                self.assertIn("ZERO_DENOMINATOR", report["reasons"])
                self.assertFalse(any("allocation" in line for line in report["lines"]))

    def test_unknown_source_and_price(self):
        user,_=self.register(); device,state=self.device(user)
        bill=self.write(user,"bill",BASE); ops=self.fixture_ops()
        self.write(user,"rule",RATE); self.write(user,"rule",WEIGHT)
        changed=ops[0]["fact"]["usage"]
        changed["cache_write"]=100
        ops[1]["fact"]["model"]="UNKNOWN"
        del ops[2]["fact"]["occurred_ms"]
        self.push(device,state,ops);self.complete(device,state)
        result=self.report(user,bill)
        self.assertEqual(result["api_status"],"PARTIAL")
        self.assertEqual(result["known_api"],(Decimal(34560)*Decimal(".5")+103*8)/1000000)
        self.assertIn("INCOMPLETE_WEIGHT",result["reasons"])
        self.assertNotIn("allocation",result["lines"][0])
        missing={reason for row in result["evidence"] for reason in row["api"]["missing"]}
        self.assertEqual(missing,{"INPUT","CACHE_WRITE","MISSING_RULE","MISSING_TIME"})
        self.write(user,"rule",{**RATE,"speed":"fast"})
        self.assertIn("MISSING_SPEED",{s for row in self.report(user,bill)["evidence"] for s in row["api"]["missing"]})

    def test_account_versions_and_boundaries(self):
        from concurrent.futures import ThreadPoolExecutor
        u,_=self.register();v,_=self.register()
        bill=self.write(u,"bill",BASE)
        self.assertEqual(v.get(self.url+"/costs",params=dict(handler="Report",billId=bill)).status_code,404)
        self.write(v,"bill",{**BASE,"paid":"150","paid_cny":"150"},bill,0)
        self.assertEqual(self.report(u,bill)["bill"]["paid"],100)
        page=u.get(self.url+"/costs")
        token=p3.fields(page)["__RequestVerificationToken"]
        def edit(n):
            from requests import post
            body=dict(BASE,paid=n,paid_cny=n,id=bill,version=1,kind="bill",__RequestVerificationToken=token)
            return post(self.url+"/costs",data=body,cookies=u.cookies,verify=self.ca,allow_redirects=False).status_code
        with ThreadPoolExecutor(2) as pool:self.assertEqual(sorted(pool.map(edit,["99","101"])),[302,409])
        self.assertEqual(u.post(self.url+"/costs",data=dict(kind="bill",id=bill,version=2,**BASE)).status_code,400)
        z={**BASE,"start":"2024-03-10","end":"2024-03-11","zone":"America/New_York","covered_through":"2024-03-11T04:00"}
        daylight=self.write(u,"bill",z)
        result=self.report(u,daylight)
        self.assertEqual(result["end"]-result["start"],23*3600*1000)
        self.write(u,"bill",{**z,"zone":"NoSuch/Zone"},expect=400)
        self.write(u,"bill",{**z,"plan":"另一订阅","start":"2024-03-09"})
        self.assertIn("OVERLAPPING_BILLS",self.report(u,daylight)["reasons"])

    def test_current_sidebar_cache(self):
        user,_=self.register(); today=dt.datetime.now(UTC); yesterday=(today-dt.timedelta(days=1)).date()
        until=(today+dt.timedelta(days=2)).date()
        bill=self.write(user,"bill",{**BASE,"start":str(yesterday),"end":str(until),"covered_through":today.strftime("%Y-%m-%dT%H:%M")})
        self.write(user,"rule",{**RATE,"speed":"fast"})
        self.write(user,"rule",WEIGHT)
        path=self.dir/"current"; sessions=path/"codex/sessions";sessions.mkdir(parents=True)
        rows=[json.loads(l) for l in FIXTURE.read_text(encoding="utf-8").splitlines()]
        shift=(today-dt.timedelta(hours=1)).timestamp()-ms(rows[0]["timestamp"])/1000
        for row in rows:
            row["timestamp"]=dt.datetime.fromtimestamp(ms(row["timestamp"])/1000+shift,UTC).isoformat()
            if row["type"]=="turn_context":row["payload"]["service_tier"]="fast"
        (sessions/"rollout-cost.jsonl").write_text("\n".join(map(json.dumps,rows))+"\n",encoding="utf-8")
        db=path/"local.sqlite";store=path/"sync"
        worker=os.getenv("TRACKER_EXE",p3.ROOT/"artifacts/phase2/windows-x64/CodexTracker.exe")
        p3.run([worker,"scan","--root",path/"codex","--device","desk","--db",db])
        self.cli_pair(user,store);self.cli(store,"allow",db,"desk","thread-A");self.cli(store,"sync")
        self.assertEqual(self.cli(store,"--no-browser","costs"),self.url+"/costs")
        result=self.report(user,bill)
        self.assertEqual(result["known_api"],Decimal("0.094166"))
        with sqlite3.connect(store/"sync.sqlite") as local:
            data=local.execute("select line from cost_cache where thread='thread-A'").fetchone()
            self.assertEqual(json.loads(data[0])["allocation"],100)
        driver=os.getenv("TRACKER_COST_DISPLAY_DRIVER")
        if driver:
            checked=p3.run([driver,"cost-cache",str(store/"sync.sqlite"),str(db),"desk"])
            self.assertIn(b"0.094166",checked.stdout)
        self.cli(store,"logout")
        if driver:
            hidden=p3.run([driver,"cost-cache",str(store/"sync.sqlite"),str(db),"desk"])
            self.assertNotIn(b"0.094166",hidden.stdout)

    def test_cache_writes_and_speed_disagreement(self):
        user,_=self.register();a,sa=self.device(user);b,sb=self.device(user)
        bill=self.write(user,"bill",BASE)
        rate=self.write(user,"rule",{**RATE,"speed":"fast","write":"4","write_basis":"included"})
        op=self.fixture_ops()[0];op["fact"]["usage"]["cache_write"]=100
        for dev,st in ((a,sa),(b,sb)):
            self.push(dev,st,[op]);self.complete(dev,st)
            dev.post(self.url+"/api/v1/costs/speeds",json=[dict(id=op["observation_id"],rev=1,speed="fast")]).raise_for_status()
        inc=((35874-34560-100)*2+34560*Decimal(".5")+100*4+103*8)/1000000
        self.assertEqual(self.report(user,bill)["known_api"],inc)
        self.write(user,"rule",{**RATE,"speed":"fast","write":"4","write_basis":"separate"},rate,1)
        self.assertEqual(self.report(user,bill)["known_api"],inc+Decimal("0.0002"))
        b.post(self.url+"/api/v1/costs/speeds",json=[dict(id=op["observation_id"],rev=1,speed="standard")]).raise_for_status()
        self.assertEqual(self.report(user,bill)["api_status"],"UNKNOWN")
        # A retracted observation can no longer supply speed evidence for the surviving response.
        self.push(b,sb,[dict(op_id=p3.ID(),observation_id=op["observation_id"],source_rev=2,kind="observation.retract")])
        self.assertEqual(self.report(user,bill)["known_api"],inc+Decimal("0.0002"))
        op["fact"]["usage"]["cache_write"]=2000
        self.push(a,sa,[{**op,"source_rev":2,"op_id":p3.ID()}])
        a.post(self.url+"/api/v1/costs/speeds",json=[dict(id=op["observation_id"],rev=2,speed="fast")]).raise_for_status()
        self.write(user,"rule",{**RATE,"speed":"fast","write":"4","write_basis":"included"},rate,2)
        bad=self.report(user,bill)
        self.assertEqual(bad["known_api"],Decimal(103*8)/1000000)
        self.assertIn("OVERLAPPING_INPUT",bad["evidence"][0]["api"]["missing"])

    def test_project_pool_and_period_cut(self):
        user,_=self.register();dev,st=self.device(user);bill=self.write(user,"bill",BASE)
        self.write(user,"rule",RATE);self.write(user,"rule",WEIGHT)
        ops=self.fixture_ops()
        ops[0]["fact"]["occurred_ms"]=ms("2023-11-01T00:00:00Z")
        ops[1]["fact"]["thread_id"]="B"
        ops[2]["fact"]["occurred_ms"]=ms("2023-12-01T00:00:00Z")
        self.push(dev,st,ops);self.complete(dev,st)
        project=p3.ID();group=p3.ID()
        def edit(kind,rid,value):
            return dict(kind=kind,op_id=p3.ID(),resource_id=rid,base_version=0,value=value)
        self.push(dev,st,[edit("project.create",project,dict(name="核算项目")),edit("group.create",group,dict(name="开发",project_id=project)),
                          edit("member.set",p3.ID(),dict(thread_id="thread-A",group_id=group))])
        r=self.report(user,bill)
        self.assertEqual(len(r["evidence"]),2)
        rows={l["thread"]:l for l in r["lines"]}
        self.assertEqual(rows["thread-A"]["project"],project)
        self.assertEqual((rows["thread-A"]["task_group"],rows["thread-A"]["group_name"],rows["thread-A"]["project_name"]),(group,"开发","核算项目"))
        self.assertNotIn("project",rows["B"])
        self.assertEqual(rows["thread-A"]["allocation"]+rows["B"]["allocation"],100)

    @unittest.skipUnless((p3.ROOT/"artifacts/phase3/server/Tracker.Server.dll").exists(),"requires Phase 3 release")
    def test_upgrade_phase3_database(self):
        self.pg.execute("create database cost_upgrade")
        pgport=self.pg.info.port
        env={**self.env,"ConnectionStrings__Tracker":f"Host=127.0.0.1;Port={pgport};Database=cost_upgrade;Username=fixture;Password={self.pw}",
             "Tracker__SecurityDirectory":str(self.dir/"upgraded-security")}
        old=p3.ROOT/"artifacts/phase3/server/Tracker.Server.dll"
        p3.run([p3.DOTNET,old,"--migrate"],env=env)
        with p3.psycopg.connect(host="127.0.0.1",port=pgport,dbname="cost_upgrade",user="fixture",password=self.pw,autocommit=True) as db:
            baseline=db.execute("select * from tracker_instance").fetchone()
            self.assertIsNone(db.execute("select to_regclass('cost_revision')").fetchone()[0])
            for _ in range(2):p3.run([p3.DOTNET,p3.SERVER,"--migrate"],env=env)
            self.assertEqual(db.execute("select version from cost_schema").fetchall(),[(1,)])
            self.assertEqual(db.execute("select * from tracker_instance").fetchone(),baseline)
            self.assertTrue(db.execute("select relforcerowsecurity from pg_class where oid='cost_revision'::regclass").fetchone()[0])

    @unittest.skipUnless(os.getenv("TRACKER_TEST_BROWSER"),"set TRACKER_TEST_BROWSER to a test browser executable")
    def test_rendered_browser(self):
        from playwright.sync_api import sync_playwright
        _,address=self.register()
        with sync_playwright() as pw:
            browser=pw.chromium.launch(executable_path=os.environ["TRACKER_TEST_BROWSER"],headless=True)
            try:
                page=browser.new_page(ignore_https_errors=True,viewport={"width":1280,"height":800})
                page.goto(self.url+"/costs")
                page.locator('[name="Input.Email"]').fill(address)
                page.locator('[name="Input.Password"]').fill(self.pw)
                page.get_by_role("button",name="登录",exact=True).click()
                page.get_by_role("heading",name="成本核算",exact=True).wait_for()
                for key,value in BASE.items():page.locator(f'input[name="{key}"]').fill(value)
                page.get_by_role("button",name="保存订阅账单").click()
                page.get_by_text("人民币 100 元",exact=False).first.wait_for()
                page.get_by_role("link",name="价格与权重",exact=True).click()
                page.get_by_role("link",name="载入 GPT-4.1",exact=False).click()
                self.assertEqual(page.locator('[name="input"]').input_value(),"2")
                self.assertEqual(page.locator('[name="speed"]').input_value(),"standard")
                page.get_by_role("button",name="保存价格与权重").click()
                page.get_by_role("heading",name="修订价格与权重").wait_for()
                page.set_viewport_size({"width":390,"height":844})
                self.assertTrue(page.evaluate("document.documentElement.scrollWidth <= innerWidth"))
            finally:browser.close()

def load_tests(loader,tests,pattern):
    return unittest.TestSuite(Costs(n) for n in sorted(vars(Costs)) if n.startswith("test_"))

if __name__=="__main__":
    unittest.main(verbosity=2)
