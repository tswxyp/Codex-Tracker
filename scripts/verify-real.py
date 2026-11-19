"""Compare a closed, read-only real corpus with the Worker ledger. Prints aggregates only."""
import argparse
import hashlib
import json
import os
import sqlite3
import subprocess
import time
from datetime import date
from collections import Counter, defaultdict
from contextlib import closing
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
FIELDS=('input_tokens','output_tokens','cached_input_tokens','cache_write_input_tokens','reasoning_output_tokens','total_tokens')

def vector(obj):
    return tuple(obj.get(k) for k in FIELDS)

def sample(root):
    closed=lambda p:p.stat().st_mtime<time.time()-900
    # NTFS can defer the mtime of an open rollout until Codex closes its handle.
    today = date.today().isoformat()
    plain=sorted(p for p in (root/'sessions').rglob('*.jsonl') if p.stat().st_size<=16*1024**2 and closed(p) and today not in p.name)
    def evenly(items,n):
        seq=sorted(p for p in items if p.stat().st_size<=512*1024 and closed(p))
        if len(seq)<=n:return seq
        return [seq[round(i*(len(seq)-1)/(n-1))] for i in range(n)]
    return plain+evenly((root/'sessions').rglob('*.jsonl.zst'),16)+evenly((root/'archived_sessions').rglob('*.jsonl.zst'),6)

def raw(path):
    if path.suffix!='.zst':return path.read_bytes()
    program="const f=require('node:fs'),z=require('node:zlib');process.stdout.write(z.zstdDecompressSync(f.readFileSync(process.argv[1]),{maxOutputLength:128*1024*1024}))"
    return subprocess.check_output(['node','-e',program,str(path)])

def main():
    p=argparse.ArgumentParser()
    p.add_argument('--root',type=Path,default=Path(os.environ.get('CODEX_HOME') or Path.home()/'.codex'))
    p.add_argument('--db',type=Path,required=True)
    p.add_argument('--exe',type=Path,default=ROOT/'client/Tracker.Worker/bin/Release/net10.0-windows/CodexTracker.exe')
    a=p.parse_args()
    if a.db.exists():p.error('use an unused database path to keep the reference corpus isolated')
    paths=sample(a.root)
    if not paths:raise RuntimeError('no closed real samples')
    before=[hashlib.sha256(f.read_bytes()).digest() for f in paths]
    unique=defaultdict(set);snapshot=defaultdict(set);models=defaultdict(set);counts=Counter()
    for f in paths:
        owner=turn=None
        for line in raw(f).splitlines():
            if not line.strip():continue
            j=json.loads(line);v=j['payload'];kind=j['type'];counts['rows']+=1
            if kind=='session_meta':
                owner=v.get('id') or v.get('session_id');turn=None
            if kind=='turn_context':
                turn=v.get('turn_id')
                if turn and owner and v.get('model'):models[owner,turn].add(v['model'])
            if kind=='event_msg' and v.get('type')=='task_started':turn=v.get('turn_id')
            if kind=='token_usage_record':
                counts['response_rows']+=1
                unique[v['thread_id'],v['response_id']].add((v.get('turn_id'),vector(v['usage'])))
            if kind=='event_msg' and v.get('type')=='token_count':
                counts['snapshot_rows']+=1
                info=v.get('info') or {}
                if owner and info.get('total_token_usage') and info.get('last_token_usage'):
                    snapshot[owner].add((j['timestamp'],j.get('ordinal') or 0,turn or '',vector(info['total_token_usage']),vector(info['last_token_usage'])))
    command=[str(a.exe),'scan','--db',str(a.db),'--root',str(a.root),'--device','real-sample']
    for f in paths:command+=['--file',str(f)]
    subprocess.run(command,check=True)
    subprocess.run(command,check=True)
    assert before==[hashlib.sha256(f.read_bytes()).digest() for f in paths], 'source changed during test'
    with closing(sqlite3.connect(a.db)) as db:
        db.row_factory=sqlite3.Row
        got={}
        for row in db.execute("""select l.thread_id,json_extract(e.data,'$.Response') rid,l.input,l.output,l.cache_read,l.cache_write,l.reasoning,l.total,l.model
                              from TokenLedger l join Event e on e.id=l.event_id where l.method='response'"""):
            got[row[0],row[1]]=(tuple(row[2:8]),row['model'])
        expect={k:next(iter(v)) for k,v in unique.items() if len(v)==1}
        correct=sum(k in got and got[k][0]==v for k,(_,v) in expect.items())
        comparable=[(k,next(iter(models[k[0],turn]))) for k,(turn,_) in expect.items() if len(models[k[0],turn])==1]
        model_match=sum(k in got and got[k][1]==m for k,m in comparable)
        modern={k[0] for k in unique}
        # An independent adjacent-vector calculation over full legacy-only histories.
        legacy=defaultdict(lambda:[0]*6)
        for tid,snaps in snapshot.items():
            if tid in modern:continue
            prev=None; gap=False
            for stamp,ordinal,turn,tot,recent in sorted(snaps):
                if not all(isinstance(x,int) for x in tot+recent):continue
                if prev==tot:continue
                if prev and any(a<b for a,b in zip(tot,prev)):
                    gap=True;continue
                diff=tuple(a-b for a,b in zip(tot,prev)) if prev else tot
                if not gap and diff==recent and diff[-1]>0:
                    for i,n in enumerate(diff):legacy[tid][i]+=n
                prev=tot;gap=False
        observed={r[0]:list(r[1:]) for r in db.execute("""select thread_id,sum(input),sum(output),sum(cache_read),sum(cache_write),sum(reasoning),sum(total) from TokenLedger where method='snapshot_delta' group by thread_id""")}
        old_keys=set(legacy)|set(observed)
        old_match=sum(legacy.get(k)==observed.get(k) for k in old_keys)
        statuses=dict(db.execute('select status,count(*) from Reconciliation group by status'))
        timing=db.execute('select sum(total_ms),sum(active_ms),sum(paused_ms),sum(unknown_ms) from TaskTiming').fetchone()
        report={'plain_files':sum(x.suffix=='.jsonl' for x in paths),'zstd_files':sum(x.suffix=='.zst' for x in paths),
            **counts,'distinct_responses':len(expect),'confirmed_rows':len(got),'responses_all_six_correct':correct,
            'comparable_model_rows':len(comparable),'model_correct':model_match,
            'legacy_threads_compared':len(old_keys),'legacy_threads_all_six_correct':old_match,
            'reference_response_totals':[sum(v[i] for _,v in expect.values()) for i in range(6)],
            'source_sha256_unchanged':True,'reconciliation':statuses,'timing_ms':list(timing),
            'quality':dict(db.execute('select code,count(*) from DataQuality group by code')),
            'sqlite_integrity':db.execute('pragma integrity_check').fetchone()[0],
            'foreign_key_errors':len(db.execute('pragma foreign_key_check').fetchall())}
        print(json.dumps(report,indent=2))
        assert expect and correct==len(got)==len(expect) and model_match==len(comparable)
        assert old_keys and old_match==len(old_keys)
        assert timing[0]==sum(timing[1:]) and report['sqlite_integrity']=='ok' and not report['foreign_key_errors']
if __name__=='__main__':main()
