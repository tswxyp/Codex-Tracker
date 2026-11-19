"""Extract a small, content-free fixture from real Windows Codex rollouts."""
import copy
import json
import os
from datetime import datetime, timezone
from pathlib import Path

HOME = Path(os.environ.get("CODEX_HOME") or Path.home() / ".codex")
OUT = Path(__file__).resolve().parents[1] / 'tests' / 'fixtures'
OUT.mkdir(parents=True, exist_ok=True)
base = 1_700_000_000_000
def ms(value):
    return round(datetime.fromisoformat(value.replace('Z', '+00:00')).timestamp() * 1000)
def iso(value):
    return datetime.fromtimestamp(value / 1000, timezone.utc).isoformat(timespec='milliseconds').replace('+00:00', 'Z')
def dump(name, rows):
    (OUT / name).write_text(''.join(json.dumps(x, separators=(',', ':')) + '\n' for x in rows), encoding='utf8')
def keep(obj, keys):
    return {k: obj[k] for k in keys.split() if k in obj}
def vector(v):
    return keep(v, 'input_tokens output_tokens cached_input_tokens cache_write_input_tokens reasoning_output_tokens total_tokens')

found = child = False
for path in sorted((HOME / 'sessions').rglob('*.jsonl'), key=lambda f: f.stat().st_size):
    if path.stat().st_size > 32*1024**2:
        continue
    rows = [json.loads(l) for l in path.read_bytes().splitlines() if l.strip()]
    for j in rows:
        p = j.get('payload') or {}
        if j['type']=='session_meta' and p.get('parent_thread_id') and p.get('forked_from_id') and not child:
            q=keep(p,'id session_id parent_thread_id forked_from_id subagent_history_start_ordinal')
            aliases={p['id']:'child-A', p['parent_thread_id']:'thread-A'}
            for key in ('id','session_id','parent_thread_id','forked_from_id'):
                if key in q:q[key]=aliases.setdefault(q[key], 'related-'+str(len(aliases)))
            q['source']={'subagent':{'thread_spawn':{'parent_thread_id':'thread-A'}}}
            dump('child.jsonl',[{'timestamp':iso(base),'type':'session_meta','payload':q}]);child=True
    if found and child:break
    if found:continue
    for ctx in [j for j in rows if j['type']=='turn_context']:
        turn=ctx['payload'].get('turn_id')
        rr=[j for j in rows if j['type']=='token_usage_record' and j['payload'].get('turn_id')==turn]
        if len(rr)<3:continue
        rr=rr[:3]; thread=rr[0]['payload']['thread_id']
        meta=next((j for j in rows if j['type']=='session_meta' and j['payload'].get('id')==thread),None)
        if meta is None:continue
        picked=[meta,ctx]; pairs=0
        for r in rr:
            p=r['payload']
            sn=next((j for j in rows if j['type']=='event_msg' and j['payload'].get('type')=='token_count'
                     and (j['payload'].get('info') or {}).get('last_token_usage')==p['usage']
                     and j['payload']['info'].get('total_token_usage')==p['thread_token_usage']
                     and abs(ms(j['timestamp'])-ms(r['timestamp']))<=2000),None)
            if sn:picked.append(sn);pairs+=1
            picked.append(r)
        if pairs<3:continue
        picked += [j for j in rows if j['type']=='event_msg' and j['payload'].get('turn_id')==turn and
            (j['payload'].get('type') in ('task_started','task_complete') or
             j['payload'].get('type')=='item_completed' and j['payload'].get('item',{}).get('type') in ('Reasoning','AgentMessage')
             and (j['payload'].get('completed_at_ms') or 0)>(j['payload'].get('started_at_ms') or 0)>0)]
        if not any(j['payload'].get('type')=='task_complete' for j in picked):continue
        offset=base-ms(meta['timestamp']); selected_ids={id(x) for x in picked}; output=[]
        for j in rows:
            if id(j) not in selected_ids:continue
            p=j['payload'];kind=j['type'];sub=p.get('type')
            if kind=='session_meta':q={'id':'thread-A','session_id':'thread-A' if p.get('session_id')==thread else 'session-A'}
            elif kind=='turn_context':q={**keep(p,'model effort service_tier'),'turn_id':'turn-A'}
            elif kind=='token_usage_record':
                q={'thread_id':'thread-A','session_id':'thread-A' if p['session_id']==thread else 'session-A','turn_id':'turn-A','root_turn_id':'turn-A' if p.get('root_turn_id')==turn else 'root-A',
                   'response_id':'response-'+str(rr.index(j)+1),**{k:vector(p[k]) for k in ('usage','turn_token_usage','thread_token_usage')}}
            elif sub=='token_count':q={'type':sub,'info':{k:vector(p['info'][k]) for k in ('last_token_usage','total_token_usage')}}
            elif sub=='item_completed':
                q={'type':sub,'thread_id':'thread-A','turn_id':'turn-A','started_at_ms':p['started_at_ms']+offset,'completed_at_ms':p['completed_at_ms']+offset,'item':{'type':p['item']['type'],'id':'item-'+str(len(output))}}
            else:
                q={'type':sub,'turn_id':'turn-A',**keep(p,'duration_ms')}
                for k in ('started_at','completed_at'):
                    if k in p:q[k]=p[k]+offset/1000
            item={'timestamp':iso(ms(j['timestamp'])+offset),'type':kind,'payload':q}
            if 'ordinal' in j:item['ordinal']=j['ordinal']
            output.append(item)
        dump('turn.jsonl',output);found=True;break
if not found or not child:raise SystemExit('No matching real fixture in this data root')
print('Extracted full metering vectors and allowlisted time/lineage metadata.')
