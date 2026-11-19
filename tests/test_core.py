"""Black-box acceptance tests against the built Worker and real-structure fixtures."""
import copy
import json
import os
import shutil
import sqlite3
import subprocess
import tempfile
import time
import unittest
from contextlib import closing
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
EXE = Path(os.environ.get('TRACKER_EXE', REPO/'client/Tracker.Worker/bin/Release/net10.0-windows/CodexTracker.exe'))
FIX = REPO/'tests/fixtures'
ROWS = [json.loads(l) for l in (FIX/'turn.jsonl').read_text().splitlines()]
RESP = [r for r in ROWS if r['type']=='token_usage_record']
SNAP = [r for r in ROWS if r['payload'].get('type')=='token_count']
META, CTX = ROWS[0], next(r for r in ROWS if r['type']=='turn_context')
EXPECTED = sum(r['payload']['usage']['total_tokens'] for r in RESP)

def encode(rows):
    return b''.join(json.dumps(r,separators=(',',':')).encode()+b'\n' for r in rows)

class Core(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        folder=Path(self.temp.name)
        self.root=folder/'input'
        (self.root/'sessions').mkdir(parents=True)
        self.file=self.root/'sessions'/'rollout-a.jsonl'
        self.db=folder/'tracker.sqlite'

    def command(self, name='scan', *args):
        defaults = {'--db': str(self.db), '--root': str(self.root), '--device': 'A'}
        prefix = [part for key,val in defaults.items() if key not in args for part in (key,val)]
        return [str(EXE),name,*prefix,*map(str,args)]
    def run_cli(self,name='scan',*args,expected=0):
        p=subprocess.run(self.command(name,*args),capture_output=True)
        self.assertEqual(p.returncode,expected,p.stderr.decode('utf8','replace'))
        return p.stdout
    def sql(self,sql):
        with closing(sqlite3.connect(self.db)) as c:return c.execute(sql).fetchall()
    def val(self,sql):
        return self.sql(sql)[0][0]
    def scan(self,rows):
        self.file.write_bytes(encode(rows));self.run_cli()
    def total(self):
        return self.val('select coalesce(sum(total),0) from TokenLedger')

    def test_repeated_scan_reparse_and_raw_evidence(self):
        self.scan(ROWS); count=self.val('select count(*) from Event')
        self.run_cli();self.run_cli('scan','--replay');self.run_cli('rebuild')
        self.assertEqual(self.total(),EXPECTED)
        self.assertEqual(self.val('select count(*) from Event'),count)
        self.assertEqual(self.val("select count(*) from Reconciliation where status='MATCHED'"),3)
        raw=self.val("select raw_metering from SourceRecord where raw_metering like '%token_usage_record%' order by offset limit 1")
        self.assertEqual(json.loads(raw),RESP[0])
        self.assertEqual(self.sql('pragma foreign_key_check'),[])
        self.assertEqual(self.val('pragma integrity_check'),'ok')
        self.assertEqual(self.val('pragma user_version'),2)
        self.assertEqual(json.loads(self.run_cli('list','--json'))[0]['Total'],EXPECTED)

    def test_append_and_independent_restart(self):
        self.scan(ROWS[:6]);base=self.file.stat().st_size
        self.assertEqual(self.total(),RESP[0]['payload']['usage']['total_tokens'])
        with self.file.open('ab') as f:f.write(encode(ROWS[6:]))
        self.run_cli()
        self.assertEqual(self.total(),EXPECTED)
        self.assertEqual(self.val('select generation from SourceFile'),1)
        self.assertEqual(self.val('select offset from SourceFile'),self.file.stat().st_size)
        self.assertTrue(self.val('select count(*) from SourceRecord where offset >= '+str(base))>0)

    def test_v1_database_upgrades_in_place(self):
        with closing(sqlite3.connect(self.db)) as c:
            c.executescript((REPO/'database/001-local.sql').read_text())
            c.execute('pragma application_id=0x43545831');c.execute('pragma user_version=1')
            c.execute("insert into Project values('existing','preserve')");c.commit()
        self.scan(ROWS)
        self.assertEqual(self.val('pragma user_version'),2)
        self.assertEqual(self.val('select name from Project'),'preserve')
        self.assertEqual(self.val('select count(distinct source_record_id) from EventSource'),len(ROWS))

    def test_truncate_and_replacement_preserve_history(self):
        self.scan(ROWS)
        self.file.write_bytes(encode(ROWS[:3]));self.run_cli()
        self.assertEqual(self.val('select generation from SourceFile'),2)
        self.assertEqual(self.total(),EXPECTED)
        new=copy.deepcopy(RESP[0]);new['payload']['response_id']='new-response'
        with self.file.open('ab') as f:f.write(encode([new]))
        self.run_cli()
        self.assertEqual(self.total(),EXPECTED+new['payload']['usage']['total_tokens'])

    def test_incomplete_and_bad_line(self):
        half=encode([RESP[0]])[:-15];prefix=encode([META,CTX])
        self.file.write_bytes(prefix+half);self.run_cli()
        self.assertEqual(self.val('select offset from SourceFile'),len(prefix))
        self.assertEqual(self.val("select count(*) from DataQuality where code='INCOMPLETE_TAIL'"),1)
        with self.file.open('ab') as f:f.write(encode([RESP[0]])[-15:]+b'broken\n'+encode([RESP[1]]))
        self.run_cli()
        self.assertEqual(self.total(),sum(r['payload']['usage']['total_tokens'] for r in RESP[:2]))
        self.assertEqual(self.val("select count(*) from DataQuality where code='INCOMPLETE_TAIL'"),0)
        self.assertEqual(self.val("select count(*) from DataQuality where code='MALFORMED_LINE'"),1)

    def test_snapshot_difference_and_primary_conflict(self):
        bad=copy.deepcopy(SNAP[1]);bad['payload']['info']['last_token_usage']=RESP[0]['payload']['usage']
        self.scan([META,CTX,*RESP,bad])
        self.assertEqual(self.total(),EXPECTED)
        self.assertEqual(self.val("select count(*) from Reconciliation where status='UNMATCHED_SNAPSHOT'"),1)
        conflict=copy.deepcopy(RESP[1]);conflict['payload']['usage']=RESP[0]['payload']['usage']
        with self.file.open('ab') as f:f.write(encode([conflict]))
        self.run_cli()
        self.assertEqual(self.total(),EXPECTED-RESP[1]['payload']['usage']['total_tokens'])
        self.assertEqual(self.val("select count(*) from DataQuality where code='RESPONSE_CONFLICT'"),2)

    def test_legacy_repeat_regression_and_late_primary(self):
        repeat=copy.deepcopy(SNAP[1]);repeat['timestamp']='2023-11-14T22:14:46Z'
        regress=copy.deepcopy(SNAP[0]);regress['timestamp']='2023-11-14T22:14:47Z'
        self.scan([META,CTX,SNAP[0],SNAP[1],repeat,regress,SNAP[2]])
        self.assertEqual(self.total(),sum(r['payload']['usage']['total_tokens'] for r in RESP[:2]))
        self.assertEqual(set(x[0] for x in self.sql('select status from Reconciliation')),
                         {'VERIFIED_DELTA','REPEATED','COUNTER_REGRESSION','DELTA_UNCONFIRMED'})
        with self.file.open('ab') as f:f.write(encode(RESP))
        self.run_cli()
        self.assertEqual(self.total(),EXPECTED)
        self.assertEqual(self.val("select count(*) from TokenLedger where method!='response'"),0)

    def test_models_cache_and_unknown_field(self):
        c=copy.deepcopy(CTX);c['payload'].update(turn_id='turn-B',model='another-model')
        r=copy.deepcopy(RESP[1]);r['payload']['turn_id']='turn-B'
        r['payload']['usage'].pop('cache_write_input_tokens')
        self.scan([META,CTX,RESP[0],c,r])
        rows=self.sql('select model,total,cache_read,cache_write from ModelUsage order by model')
        self.assertEqual(len(rows),2)
        self.assertEqual(rows[0],('another-model',r['payload']['usage']['total_tokens'],r['payload']['usage']['cached_input_tokens'],None))
        self.assertEqual(self.val('select sum(input+output-total) from TokenLedger'),0)
        self.assertEqual(self.val("select count(*) from TokenLedger where confidence='PARTIAL'"),1)
        self.assertEqual(len(json.loads(self.run_cli('models','--json'))),2)
        self.assertTrue(json.loads(self.run_cli('quality','--json')))

    def test_intra_turn_model_change(self):
        changed=copy.deepcopy(CTX);changed['timestamp']='2023-11-14T22:14:00Z'
        changed['payload']['model']='replacement-model'
        self.scan([META,CTX,RESP[0],changed,RESP[1]])
        self.assertEqual(self.sql('select model,sum(total) from TokenLedger group by model order by model'),
            sorted([(CTX['payload']['model'],RESP[0]['payload']['usage']['total_tokens']),
                    ('replacement-model',RESP[1]['payload']['usage']['total_tokens'])]))

    def test_nonzero_cache_write_does_not_inflate_total(self):
        response=copy.deepcopy(RESP[0])
        for field in ('usage','thread_token_usage','turn_token_usage'):
            response['payload'][field]['cache_write_input_tokens']=17
        self.scan([META,CTX,response])
        self.assertEqual(self.sql('select cache_write,total,input+output from ModelUsage'),
            [(17,response['payload']['usage']['total_tokens'],response['payload']['usage']['total_tokens'])])

    def test_watch_discovers_new_log(self):
        p=subprocess.Popen(self.command('watch','--interval','1'),stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
        try:
            deadline=time.monotonic()+8
            while not self.db.exists() and time.monotonic()<deadline:time.sleep(.02)
            self.file.write_bytes(encode(ROWS))
            while time.monotonic()<deadline:
                try:
                    if self.total()==EXPECTED:break
                except sqlite3.OperationalError:pass
                time.sleep(.04)
            self.assertEqual(self.total(),EXPECTED)
        finally:
            p.kill();p.wait(timeout=5)

    def test_child_fork_archived_copy(self):
        self.scan(ROWS)
        arc=self.root/'archived_sessions';arc.mkdir()
        child=json.loads((FIX/'child.jsonl').read_text())
        cr=copy.deepcopy(RESP[0]);cr['payload'].update(thread_id='child-A',session_id='thread-A')
        (arc/'child.jsonl').write_bytes(encode([*ROWS,child,cr]))
        self.run_cli()
        self.assertEqual(self.val("select sum(total) from TokenLedger where thread_id='thread-A'"),EXPECTED)
        self.assertEqual(self.sql("select parent_id,fork_id,is_subagent from Session where thread_id='child-A'"),[('thread-A','thread-A',1)])
        self.assertEqual(self.val('select count(*) from SourceFile'),2)

    def test_cross_thread_snapshot_before_primary(self):
        src=copy.deepcopy(META);src['payload']['id']='inherited-owner'
        self.scan([src,CTX,SNAP[0]])
        self.assertEqual(self.total(),RESP[0]['payload']['usage']['total_tokens'])
        with self.file.open('ab') as f:f.write(encode([RESP[0]]))
        self.run_cli()
        self.assertEqual(self.sql('select thread_id,method from TokenLedger'),[('thread-A','response')])
        self.assertEqual(self.val('select status from Reconciliation'),'CROSS_THREAD_MATCH')

    def test_fork_inherits_legacy_snapshots_parent_arrives_late(self):
        child=json.loads((FIX/'child.jsonl').read_text())
        self.scan([child,CTX,*SNAP])
        (self.root/'sessions'/'parent.jsonl').write_bytes(encode([META,CTX,*SNAP]))
        self.run_cli()
        self.assertEqual(self.total(),EXPECTED)
        self.assertEqual(self.val("select count(*) from Reconciliation where status='INHERITED_SNAPSHOT'"),3)
        self.assertEqual(self.val("select count(*) from TokenLedger where thread_id='child-A'"),0)

    def test_proven_legacy_prefix_survives_new_format(self):
        later=copy.deepcopy(RESP[2]);later['payload']['turn_id']='turn-later'
        self.scan([META,CTX,*SNAP[:2],later])
        self.assertEqual(self.total(),EXPECTED)
        self.assertEqual(self.val("select count(*) from TokenLedger where method='snapshot_delta'"),2)

    def test_system_sleep_clips_short_explicit_work(self):
        self.scan(ROWS)
        work=next(r['payload'] for r in ROWS if r['payload'].get('type')=='item_completed')
        a=work['started_at_ms'];before=self.val('select active_ms from TaskTiming')
        with closing(sqlite3.connect(self.db)) as c:
            c.execute("insert into PowerGap values('A',?,?)",(a,a+250));c.commit()
        self.run_cli('rebuild')
        self.assertEqual(self.val('select active_ms from TaskTiming'),before-250)
        self.assertEqual(self.val('select paused_ms from TaskTiming'),250)

    def test_pause_resume_and_unconfirmed_time(self):
        start=copy.deepcopy(ROWS[1]);p=start['payload']
        pause={'timestamp':'2023-11-14T22:14:00Z','type':'event_msg','payload':{'type':'turn_aborted','turn_id':p['turn_id']}}
        resume=copy.deepcopy(start);resume['timestamp']='2023-11-14T22:15:00Z'
        resume['payload'].update(turn_id='turn-B',started_at=1700000100)
        finish=copy.deepcopy(ROWS[-1]);finish['payload']['turn_id']='turn-B'
        work=copy.deepcopy(next(r for r in ROWS if r['payload'].get('type')=='item_completed'))
        sleep=copy.deepcopy(work);sleep['payload'].update(started_at_ms=1700000011942,completed_at_ms=1700000171942)
        self.scan([META,start,work,work,pause]);self.assertEqual(self.val('select state from TaskTiming'),'PAUSED')
        with self.file.open('ab') as f:f.write(encode([resume,sleep,finish]))
        self.run_cli()
        t,a,p,u,st=self.sql('select total_ms,active_ms,paused_ms,unknown_ms,state from TaskTiming')[0]
        self.assertEqual(a,work['payload']['completed_at_ms']-work['payload']['started_at_ms'])
        self.assertEqual(p,60_000);self.assertEqual(t,a+p+u);self.assertGreater(u,120_000)
        self.assertEqual(st,'COMPLETED')

    def test_device_root_isolation(self):
        self.scan(ROWS)
        other=self.root.parent/'other';shutil.copytree(self.root,other)
        self.run_cli('scan','--root',other,'--device','B')
        self.assertEqual(self.sql('select device_id,sum(total) from TokenLedger group by device_id'),[('A',EXPECTED),('B',EXPECTED)])
        self.run_cli('scan','--root',other,expected=1)

    def test_zstandard_and_bad_frame(self):
        self.scan(ROWS);arc=self.root/'archived_sessions';arc.mkdir()
        zfile=arc/'copy.jsonl.zst'
        js="const fs=require('node:fs'),z=require('node:zlib');fs.writeFileSync(process.argv[2],z.zstdCompressSync(fs.readFileSync(process.argv[1])))"
        subprocess.run(['node','-e',js,str(self.file),str(zfile)],check=True)
        self.run_cli();self.assertEqual(self.total(),EXPECTED)
        self.assertEqual(self.val('select count(distinct source_id) from SourceRecord'),2)
        zfile.write_bytes(zfile.read_bytes()[:-8]);self.run_cli(expected=2)
        self.assertEqual(self.val("select count(*) from DataQuality where code='READ_FAILED'"),1)
        self.assertEqual(self.total(),EXPECTED)

    def test_kill_mid_transaction_then_recover(self):
        self.scan(ROWS);base=self.file.stat().st_size
        # Repeated physical observations force several transaction boundaries.
        with self.file.open('ab') as f:f.write(encode([SNAP[0]])*18000)
        p=subprocess.Popen(self.command(),stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
        self.addCleanup(lambda:p.kill() if p.poll() is None else None)
        deadline=time.monotonic()+20;interrupted=False
        with closing(sqlite3.connect(self.db,timeout=0)) as c:
            while time.monotonic()<deadline and p.poll() is None:
                committed=c.execute('select offset from SourceFile').fetchone()[0]
                if committed>base:
                    try:c.execute('begin immediate');c.rollback()
                    except sqlite3.OperationalError:
                        p.kill();p.wait(timeout=5);interrupted=True;break
                time.sleep(.003)
        self.assertTrue(interrupted,'worker must be killed during an active write after a committed checkpoint')
        self.run_cli();self.assertEqual(self.total(),EXPECTED);self.assertEqual(self.val('pragma integrity_check'),'ok')
        previous=self.sql('select count(*),count(distinct event_id) from SourceRecord')
        db2=self.db.with_name('fresh.sqlite');self.run_cli('scan','--db',db2)
        with closing(sqlite3.connect(db2)) as c:
            self.assertEqual(c.execute('select count(*),count(distinct event_id) from SourceRecord').fetchall(),previous)
            self.assertEqual(c.execute('select sum(total) from TokenLedger').fetchone()[0],EXPECTED)

if __name__=='__main__':unittest.main(verbosity=2)
