"""Project finance end-to-end acceptance using temporary PostgreSQL, HTTPS and the anonymised Phase 1 fixture."""
import csv
from decimal import Decimal
import io
import os
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET
import zipfile

import test_phase3 as p3
import test_phase4 as p4


class Finance(p4.Costs):
    def form(self, user, action, fields, *, key=None, version=0, expected=302):
        url = self.url + "/finance"
        csrf = p3.fields(user.get(url, params={"section":"projects"}))["__RequestVerificationToken"]
        key = key or p3.ID()
        data = dict(action=action, id=key, version=version, __RequestVerificationToken=csrf, **fields)
        response = user.post(url, data=data, allow_redirects=False)
        self.assertEqual(response.status_code, expected, response.text[:3500])
        return key

    def project(self, user, name="交付项目", **kwargs):
        data = dict(name=name, customer="验收客户", status="ACTIVE", contract="1000", start="2023-01-01",
                    end="", resource_version=1 if kwargs.get("version",0) else 0)
        return self.form(user,"profile",data,**kwargs)

    def cash(self, user, project, category, amount, reference, date="2023-11-15", **kwargs):
        return self.form(user,"cash",dict(project=project,category=category,date=date,
                                         cny=amount,reference=reference),**kwargs)

    def financials(self, user, bill=None):
        params={"handler":"Report"}
        if bill: params["billId"]=bill
        result=user.get(self.url+"/finance",params=params)
        result.raise_for_status()
        return result.json(parse_float=Decimal)

    def test_cash_profit_pool_and_reset(self):
        user,_=self.register()
        dev,state=self.device(user)
        project=self.project(user)
        other=self.project(user)
        group=self.form(user,"group",dict(name="交付任务",project=project))
        ops=self.fixture_ops()
        self.push(dev,state,ops)
        self.form(user,"member",dict(group=group,target_kind="session",target="session-A"))
        self.complete(dev,state)
        bill=self.write(user,"bill",p4.BASE)
        self.write(user,"rule",p4.RATE)
        self.write(user,"credits",dict(bill_id=bill,start="2023-11-01T00:00",through="2023-12-01T00:00",
                                      evidence="SETTLED",source="模拟实扣",total="4",parts="thread-A=3\n@pool=1"))
        reset=dict(bill_id=bill,at="2023-11-15T08:00",type="PAID",reference="支付-R",before="",after="",cash="8.50",currency="CNY")
        rid=self.write(user,"reset",reset)
        self.write(user,"reset",{**reset,"type":"GLOBAL","cash":"0","reference":"免费重置"})
        self.form(user,"reset",dict(project=project,source_version=1,cny="8.50"),key=rid)
        self.cash(user,project,"RECEIPT","300","预付","2023-10-31")
        received=self.cash(user,project,"RECEIPT","600","验收款")
        self.cash(user,project,"RECEIPT","600","验收款",key=received)
        for category,amount in [("LABOR","100"),("SERVER","10"),("OTHER","5"),("AI","20")]:
            self.cash(user,project,category,amount,category)
        self.cash(user,project,"RECEIPT","80","下期款","2023-12-01")
        self.cash(user,other,"LABOR","11","第二项目")
        self.cash(user,project,"OTHER","8.50","支付-R",expected=409)
        r=self.financials(user,bill)
        rows={p["id"]:p for p in r["projects"]}
        a=rows[project]
        self.assertEqual((a["received"],a["outstanding"],a["subscription"],a["reset"],a["cost"],a["profit"]),
                         (600,100,75,Decimal("8.50"),Decimal("218.50"),Decimal("381.50")))
        self.assertEqual(a["api_known"]["USD"],Decimal("0.094166"))
        self.assertEqual((rows[other]["profit"],rows[other].get("margin")),(-11,None))
        self.assertEqual((r["pool"]["subscription"],r["known_cost"],r["net"]),(25,Decimal("254.50"),Decimal("345.50")))
        self.assertEqual((r["bills"][0]["global_resets"],r["bills"][0]["tokens"]),(1,114148))
        self.assertNotIn("per_ten_million",r["bills"][0])
        all_time=self.financials(user)
        self.assertEqual((all_time["received"],all_time["net"]),(980,Decimal("725.50")))

    def test_timing_union_and_effective_rate(self):
        user,_=self.register();dev,state=self.device(user)
        project=self.project(user)
        g=self.form(user,"group",dict(name="并行",project=project))
        start=p4.ms("2023-11-01T00:00:00Z");hour=3600000
        def timing(thread,kind,a,b):
            return dict(kind="observation.upsert",op_id=p3.ID(),observation_id=p3.ID(),source_rev=1,
                        fact=dict(thread_id=thread,type="timing",kind=kind,start_ms=start+a,end_ms=start+b))
        self.push(dev,state,self.fixture_ops()+[timing("thread-A","BOUNDARY",-hour,2*hour),
            timing("thread-A","ACTIVE",0,2*hour),timing("thread-A","PAUSED",hour,2*hour),
            timing("thread-B","BOUNDARY",hour//2,2*hour),timing("thread-B","ACTIVE",hour//2,2*hour)])
        for t in ("thread-A","thread-B"):self.form(user,"member",dict(group=g,target_kind="thread",target=t))
        self.complete(dev,state)
        self.cash(user,project,"RECEIPT",400,"R")
        bill=self.write(user,"bill",p4.BASE)
        self.write(user,"rule",p4.WEIGHT)
        r=self.financials(user,bill)
        a=r["projects"][0]
        self.assertEqual((a["time"]["total_ms"],a["time"]["active_ms"],a["revenue_per_hour"]),(2*hour,2*hour,200))
        self.assertEqual(a["profit"],300)
        avg=r["bills"][0]["per_ten_million"]
        self.assertLess(abs(avg-Decimal(100)*10_000_000/114148),Decimal("0.0000001"))
        duplicate=self.fixture_ops()[0]
        duplicate["fact"].pop("occurred_ms")
        duplicate["fact"]["response_id"]="response-missing-time"
        self.push(dev,state,[duplicate])
        r=self.financials(user,bill)
        self.assertNotIn("per_ten_million",r["bills"][0])
        self.assertNotIn("profit",r["projects"][0])
        self.assertEqual((r["net"],r["pool"]["subscription"]),(300,100))

    def test_revision_conflict_void_and_tenant(self):
        from concurrent.futures import ThreadPoolExecutor
        user,_=self.register();foreign,_=self.register()
        project=self.project(user)
        self.project(user,key=project)
        entry=self.cash(user,project,"OTHER",5,"凭据")
        def rev(amount):
            return user.post(self.url+"/finance",data=dict(action="cash",id=entry,version=1,project=project,
                category="OTHER",date="2023-11-15",cny=amount,reference="凭据",__RequestVerificationToken=token),allow_redirects=False).status_code
        token=p3.fields(user.get(self.url+"/finance?section=cash"))["__RequestVerificationToken"]
        with ThreadPoolExecutor(2) as pool:self.assertEqual(sorted(pool.map(rev,[7,9])),[302,409])
        self.cash(user,project,"OTHER",6,"凭据",expected=409)
        self.cash(foreign,project,"OTHER",5,"侵入",expected=400)
        self.assertEqual(self.financials(foreign)["projects"],[])
        self.project(foreign,"独立",key=project)
        self.assertEqual(self.financials(foreign)["known_cost"],0)
        self.form(user,"cash",dict(project=project,category="OTHER",date="2023-11-15",cny=9,reference="凭据",voided="true"),
                  key=entry,version=2)
        self.assertEqual(self.financials(user)["known_cost"],0)
        self.assertEqual(user.post(self.url+"/finance",data={"action":"cash"}).status_code,400)
        bill=self.write(user,"bill",p4.BASE)
        self.assertEqual(foreign.get(self.url+"/finance",params={"handler":"Export","format":"xlsx","billId":bill}).status_code,404)
        with self.pg.cursor() as cur:
            cur.execute("select count(*) from finance_revision where kind='cash' and id=%s",(entry,))
            self.assertEqual(cur.fetchone()[0],3)
            cur.execute("select relforcerowsecurity from pg_class where oid='finance_revision'::regclass")
            self.assertTrue(cur.fetchone()[0])

    def test_foreign_reset_revision_and_duplicate_reconciliation(self):
        user,_=self.register();project=self.project(user)
        bill=self.write(user,"bill",p4.BASE)
        self.cash(user,project,"AI",60,"card-17")
        reset=dict(bill_id=bill,at="2023-11-15T08:00",type="PAID",reference="card-17",before="",after="",cash="8",currency="USD")
        rid=self.write(user,"reset",reset)
        self.assertNotIn("net",self.financials(user,bill))
        self.form(user,"reset",dict(project=project,cny="58",source_version=1),key=rid)
        r=self.financials(user,bill)
        self.assertEqual((r["projects"][0]["extra_ai"],r["known_cost"],r["net"]),(0,158,-158))
        self.write(user,"reset",{**reset,"cash":"10"},rid,1)
        self.assertNotIn("net",self.financials(user,bill))
        self.form(user,"reset",dict(project=project,cny="59",source_version=1),key=rid,version=1,expected=409)

    def test_task_search_same_name_and_reassignment(self):
        user,_=self.register()
        dev,state=self.device(user)
        other_dev,other_state=self.device(user)
        pid=self.project(user); second=self.project(user)
        g=self.form(user,"group",dict(name="交付",project=pid))
        ops=self.fixture_ops()
        self.push(dev,state,ops)
        self.push(other_dev,other_state,ops)
        self.complete(dev,state);self.complete(other_dev,other_state)
        ref=self.form(user,"member",dict(group=g,target_kind="thread",target="thread-A"))
        bill=self.write(user,"bill",p4.BASE)
        self.write(user,"rule",p4.WEIGHT)
        q=dict(section="tasks",billId=bill,device=other_state["device"],projectId=pid,modelId="gpt-6.2-astra",
               minCost="99",minTokens="114148",sort="tokens",**{"from":"2023-11-14","to":"2023-11-14"})
        def threads(filters):
            response=user.get(self.url+"/finance",params=filters)
            response.raise_for_status()
            return response.text.count("<code>thread-A</code>")
        self.assertEqual(threads(q),1)
        for changed in (dict(minTokens=114149),dict(device=p3.ID()),dict(projectId=second),dict(minHours=1),dict(to="2023-11-13",**{"from":"2023-11-12"})):
            self.assertEqual(threads({**q,**changed}),0)
        self.form(user,"unlink",{},key=ref,version=1)
        self.assertEqual(threads(q),0)
        self.assertEqual(threads({**q,"projectId":"@pool"}),1)
        self.form(user,"group",dict(name="交付",project=second),key=g,version=1)
        self.form(user,"member",dict(group=g,target_kind="session",target="session-A"))
        self.assertEqual(threads({**q,"projectId":second}),1)
        self.assertEqual(self.financials(user,bill)["pool"]["subscription"],0)

    def test_exports_and_device_visibility(self):
        user,_=self.register();dev,state=self.device(user)
        project=self.project(user,'=SUM(1,2)')
        self.cash(user,project,"RECEIPT","26.75",'款项,"一"')
        api=dev.get(self.url+"/api/v1/projects").json()["items"]
        self.assertEqual(api[0]["id"],project)
        self.assertNotIn("customer",api[0]["value"])
        changes=dev.get(self.url+"/api/v1/sync/changes",params=dict(epoch=state["epoch"],after=0)).text
        self.assertNotIn("26.75",changes)
        params=dict(handler="Export",format="csv")
        reader=csv.reader(io.StringIO(user.get(self.url+"/finance",params=params).content.decode("utf-8-sig")))
        rows=list(reader)
        self.assertEqual(rows[1][1],"'=SUM(1,2)")
        self.assertEqual(Decimal(rows[1][14]),Decimal("26.75"))
        result=user.get(self.url+"/finance",params={**params,"format":"xlsx"})
        self.assertEqual(result.status_code,200)
        ns={"x":"http://schemas.openxmlformats.org/spreadsheetml/2006/main"}
        with zipfile.ZipFile(io.BytesIO(result.content)) as z:
            self.assertIsNone(z.testzip())
            sheet=ET.fromstring(z.read("xl/worksheets/sheet1.xml"))
            cells=sheet.findall(".//x:row",ns)[1].findall("x:c",ns)
            self.assertEqual(cells[1].findtext("x:is/x:t",namespaces=ns),"=SUM(1,2)")
            self.assertFalse(sheet.findall(".//x:f",ns))
            self.assertEqual(Decimal(cells[14].findtext("x:v",namespaces=ns)),Decimal("26.75"))
            self.assertEqual(len(ET.fromstring(z.read("xl/workbook.xml")).findall(".//x:sheet",ns)),4)
        reader=os.getenv("TRACKER_XLSX_PYTHON")
        if reader:
            script="""from io import BytesIO
import sys, openpyxl
book = openpyxl.load_workbook(BytesIO(sys.stdin.buffer.read()), read_only=True)
assert book.sheetnames == ["项目利润","订阅账期","项目流水","账号总览"]
assert book.worksheets[0]["B2"].data_type == "s"
assert book.worksheets[0]["B2"].value == "=SUM(1,2)"
assert book.worksheets[0]["O2"].value == 26.75
assert book.worksheets[2]["F2"].value == 26.75
book.close()
"""
            p3.run([reader,"-X","utf8","-B","-c",script],input=result.content)
        self.assertNotIn("验收客户",self.browser().get(self.url+"/finance",allow_redirects=False).text)

    def test_effective_rate_uses_invoice_and_scoped_funding(self):
        user,_=self.register();dev,state=self.device(user)
        project=self.project(user)
        self.push(dev,state,self.fixture_ops());self.complete(dev,state)
        bill=self.write(user,"bill",{**p4.BASE,"plan":"Pro 200","paid":"80","paid_cny":"80"})
        self.write(user,"rule",p4.WEIGHT)
        def rate():
            return self.financials(user,bill)["bills"][0].get("per_ten_million")
        self.assertLess(abs(rate()-Decimal(80)*10_000_000/114148),Decimal("0.000001"))
        self.write(user,"bill",{**p4.BASE,"plan":"Pro 200","paid":"160","paid_cny":"160"},bill,1)
        self.assertLess(abs(rate()-Decimal(160)*10_000_000/114148),Decimal("0.000001"))
        self.cash(user,project,"AI",10,"单独购买")
        self.assertIsNone(rate())

    def test_upgrade_populated_phase4(self):
        self.pg.execute("create database finance_upgrade")
        port=self.pg.info.port
        env={**self.env,"ConnectionStrings__Tracker":f"Host=127.0.0.1;Port={port};Database=finance_upgrade;Username=fixture;Password={self.pw}",
             "Tracker__SecurityDirectory":str(self.dir/"upgrade-security")}
        with tempfile.TemporaryDirectory(dir=self.dir) as tmp:
            zip_path=p3.ROOT/"artifacts/phase4/CodexTracker-Phase4-Server.zip"
            with zipfile.ZipFile(zip_path) as package:package.extractall(tmp)
            p3.run([p3.DOTNET,Path(tmp)/"Tracker.Server.dll","--migrate"],env=env)
        with p3.psycopg.connect(host="127.0.0.1",port=port,dbname="finance_upgrade",user="fixture",password=self.pw,autocommit=True) as db:
            actor=p3.ID()
            db.execute('insert into "AspNetUsers" ("Id","EmailConfirmed","PhoneNumberConfirmed","TwoFactorEnabled","LockoutEnabled","AccessFailedCount") values (%s,false,false,false,false,0)',(actor,))
            db.execute("insert into account_stream(acct) values (%s)",(actor,))
            db.execute("insert into cost_statement(acct,bill,digest,result) values (%s,%s,'prior','{}')",(actor,p3.ID()))
            epoch=db.execute("select epoch from tracker_instance").fetchone()
            for _ in range(2):p3.run([p3.DOTNET,p3.SERVER,"--migrate"],env=env)
            self.assertEqual(db.execute("select epoch from tracker_instance").fetchone(),epoch)
            self.assertEqual(db.execute("select digest from cost_statement").fetchall(),[("prior",)])
            self.assertEqual(db.execute("select version from finance_schema").fetchall(),[(1,)])

    @unittest.skipUnless(os.getenv("TRACKER_TEST_BROWSER"),"set TRACKER_TEST_BROWSER")
    def test_browser_project_receipt_and_download(self):
        from playwright.sync_api import sync_playwright
        _,addr=self.register()
        with sync_playwright() as pw:
            browser=pw.chromium.launch(executable_path=os.environ["TRACKER_TEST_BROWSER"])
            try:
                page=browser.new_page(ignore_https_errors=True,viewport={"width":1280,"height":820})
                page.goto(self.url+"/finance")
                page.locator('[name="Input.Email"]').fill(addr)
                page.locator('[name="Input.Password"]').fill(self.pw)
                page.get_by_role("button",name="登录",exact=True).click()
                page.get_by_role("link",name="客户项目",exact=True).click()
                for key,value in dict(name="浏览器项目",customer="演示客户",contract="200",start="2023-10-01").items():
                    page.locator(f'input[name="{key}"]').fill(value)
                page.get_by_role("button",name="保存项目").click()
                page.get_by_role("link",name="浏览器项目",exact=True).wait_for()
                page.get_by_role("link",name="收支流水").click()
                for key,value in dict(date="2023-11-15",cny="120",reference="浏览器测试回款").items():
                    page.locator(f'input[name="{key}"]').fill(value)
                page.get_by_role("button",name="保存流水").click()
                page.get_by_role("link",name="浏览器测试回款").wait_for()
                page.get_by_role("link",name="利润报表").click()
                page.get_by_text("120.00",exact=True).first.wait_for()
                with page.expect_download() as dl:page.get_by_role("link",name="导出 XLSX 工作簿").click()
                self.assertTrue(dl.value.suggested_filename.endswith(".xlsx"))
                page.set_viewport_size({"width":390,"height":844})
                self.assertTrue(page.evaluate("document.documentElement.scrollWidth <= innerWidth"))
            finally:browser.close()


def load_tests(loader,tests,pattern):
    return unittest.TestSuite(Finance(n) for n in sorted(vars(Finance)) if n.startswith("test_"))


if __name__=="__main__":
    unittest.main(verbosity=2)

