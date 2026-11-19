using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexTracker;

internal sealed record Usage(long? Input, long? Output, long? Read, long? Write, long? Reason, long? Total)
{
    public bool Valid => Input is >= 0 && Output is >= 0 && Total is >= 0 &&
        Input == Total - Output && Read is not < 0 && Write is not < 0 && Reason is not < 0 &&
        !(Read > Input) && !(Reason > Output);
    public bool Complete => Read.HasValue && Write.HasValue && Reason.HasValue;
    public Usage Minus(Usage p) => new(Input-p.Input, Output-p.Output, Read-p.Read, Write-p.Write, Reason-p.Reason, Total-p.Total);
    public bool Regresses(Usage p) => Input<p.Input || Output<p.Output || Read<p.Read || Write<p.Write || Reason<p.Reason || Total<p.Total;
    public static Usage? From(JsonElement e) => e.ValueKind != JsonValueKind.Object ? null :
        new(e.Num("input_tokens"), e.Num("output_tokens"), e.Num("cached_input_tokens"),
            e.Num("cache_write_input_tokens"), e.Num("reasoning_output_tokens"), e.Num("total_tokens"));
}

internal static class Fields
{
    public static JsonElement Get(this JsonElement e, string k) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k,out var v) ? v : default;
    public static string? Str(this JsonElement e,string k) =>
        e.Get(k) is { ValueKind: JsonValueKind.String } v && v.GetString() is {Length: >0 and <=1024} s ? s : null;
    public static long? Num(this JsonElement e,string k) =>
        e.Get(k) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt64(out var n) ? n : null;
    public static long? Time(this JsonElement e,string k)
    {
        if (e.Get(k) is { ValueKind: JsonValueKind.Number } v && v.TryGetDouble(out var n) && n>=0 && n<253402300800)
            return (long)(n*1000);
        return DateTimeOffset.TryParse(e.Str(k),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var d)
            ? d.ToUnixTimeMilliseconds() : null;
    }
}

internal sealed record CursorContext(string? Thread=null,string? Session=null,string? Turn=null,string? Model=null,string? Effort=null,string? Speed=null);
internal sealed record Fact(string Kind,string Thread,long? Time,string? Turn=null,string? Session=null,string? Model=null,
    string? Effort=null,string? Speed=null,string? Response=null,Usage? Delta=null,Usage? Cumulative=null,Usage? TurnTotal=null,
    string? Parent=null,string? Fork=null,bool Subagent=false,long? Start=null,long? End=null,
    string? ItemType=null,string? ItemId=null,long? Ordinal=null,string? RootTurn=null,bool MissingOwner=false,
    [property: JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] Lineage? History=null);

internal sealed record Lineage(string? Thread,long? EndOrdinal,long? EndOffset,long? ChildStart);

internal static class Parser
{
    public static Fact? Read(ReadOnlyMemory<byte> line, ref CursorContext ctx, string unresolved)
    {
        using var doc=JsonDocument.Parse(line,new JsonDocumentOptions { MaxDepth=192 });
        var j=doc.RootElement;
        if (j.ValueKind!=JsonValueKind.Object || j.Str("type") is not { } type) throw new FormatException();
        var p=j.Get("payload");
        var at=j.Time("timestamp");
        var ordinal=j.Num("ordinal");
        var thread=p.Str("thread_id") ?? ctx.Thread ?? unresolved;
        var turn=p.Str("turn_id") ?? ctx.Turn;
        var sid=p.Str("session_id") ?? (thread==ctx.Thread ? ctx.Session : null);
        if (type=="session_meta")
        {
            var id=p.Str("id") ?? p.Str("session_id");
            ctx=new(id,p.Str("session_id") ?? id);
            var spawn=p.Get("source").Get("subagent").Get("thread_spawn");
            var history=p.Get("history_base");
            var childStart=p.Num("subagent_history_start_ordinal");
            return new("meta",id??unresolved,at,Session:ctx.Session,Parent:p.Str("parent_thread_id")??spawn.Str("parent_thread_id"),
                Fork:p.Str("forked_from_id")??p.Get("history_base").Str("thread_id"),
                Subagent:p.Get("source").Get("subagent").ValueKind==JsonValueKind.Object || p.Str("agent_nickname")!=null,
                MissingOwner:id==null,Ordinal:ordinal,History:history.ValueKind==JsonValueKind.Object || childStart!=null?
                    new(history.Str("thread_id"),history.Num("end_ordinal_exclusive"),history.Num("end_byte_offset"),childStart):null);
        }
        if (type=="turn_context")
        {
            ctx=ctx with {Turn=p.Str("turn_id"),Model=p.Str("model"),Effort=p.Str("effort"),Speed=p.Str("service_tier")??p.Str("speed_mode")};
            return new("context",thread,at,ctx.Turn,sid,ctx.Model,ctx.Effort,ctx.Speed,Ordinal:ordinal);
        }
        if (type=="token_usage_record")
            return new("response",thread,at,p.Str("turn_id"),sid,Response:p.Str("response_id"),Delta:Usage.From(p.Get("usage")),
                Cumulative:Usage.From(p.Get("thread_token_usage")),TurnTotal:Usage.From(p.Get("turn_token_usage")),
                Ordinal:ordinal,RootTurn:p.Str("root_turn_id"),MissingOwner:p.Str("thread_id")==null);
        if (type=="compacted") return new("compacted",thread,at,turn,Ordinal:ordinal);
        if (type!="event_msg") return null;
        var kind=p.Str("type");
        if (kind=="token_count")
        {
            var info=p.Get("info");
            return new("snapshot",thread,at,turn,sid,ctx.Model,ctx.Effort,ctx.Speed,Delta:Usage.From(info.Get("last_token_usage")),
                Cumulative:Usage.From(info.Get("total_token_usage")),Ordinal:ordinal,MissingOwner:ctx.Thread==null);
        }
        if (kind=="item_completed")
        {
            var item=p.Get("item");
            if(item.Str("type") is not ("Reasoning" or "AgentMessage")) return null;
            return new("work",thread,at,turn,Start:p.Num("started_at_ms"),End:p.Num("completed_at_ms"),
                ItemType:item.Str("type"),ItemId:item.Str("id"),Ordinal:ordinal);
        }
        var stage=kind switch
        {
            "task_started" or "turn_started" => "start", "task_complete" or "turn_completed" => "finish",
            "turn_aborted" or "turn_interrupted" => "interrupt", "task_paused" or "turn_paused" => "pause",
            "task_resumed" or "turn_resumed" => "resume", _=>null
        };
        if(stage==null) return null;
        if(stage is "start" or "resume" && ctx.Turn!=p.Str("turn_id")) ctx=ctx with {Turn=p.Str("turn_id"),Model=null,Effort=null,Speed=null};
        return new(stage,thread,at,p.Str("turn_id")??ctx.Turn,sid,Start:p.Time("started_at"),End:p.Time("completed_at"),
            Ordinal:ordinal,RootTurn:p.Str("root_turn_id"));
    }
}
