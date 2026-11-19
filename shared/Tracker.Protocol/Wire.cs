using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Tracker.Protocol;

public sealed record Usage([property:JsonRequired]long Input, [property:JsonRequired]long Output, long? CacheRead, long? CacheWrite, long? Reasoning, [property:JsonRequired]long Total)
{
    [JsonIgnore] public bool Valid => Input is >= 0 and <= Wire.MaxInt && Output is >= 0 and <= Wire.MaxInt && Total is >= 0 and <= Wire.MaxInt && Total - Output == Input
        && CacheRead is not < 0 && !(CacheRead > Input) && CacheWrite is not < 0 && !(CacheWrite > Wire.MaxInt)
        && Reasoning is not < 0 && !(Reasoning > Output);
    [JsonIgnore] public bool Complete => CacheRead.HasValue && CacheWrite.HasValue && Reasoning.HasValue;
}

public sealed record Fact(string Type, string ThreadId, string? SessionId = null, string? TurnId = null,
    string? ResponseId = null, long? OccurredMs = null, string? Model = null, string? Confidence = null,
    Usage? Usage = null, Usage? ThreadCumulative = null, long? Ordinal = null, string? ParentId = null,
    string? ForkId = null, bool? Subagent = null, long? StartMs = null, long? EndMs = null,
    string? Kind = null, string RuleVersion = "sqlite-v2");

public sealed record Edit(string? Name = null, Guid? ProjectId = null, Guid? GroupId = null, string? ThreadId = null,
    string? SessionId = null, Guid? ConflictId = null);
public sealed record Operation(Guid OpId, string Kind, Guid? ObservationId = null, long? SourceRev = null,
    Fact? Fact = null, Guid? ResourceId = null, long? BaseVersion = null, Edit? Value = null);
public sealed record Push(int Protocol, Guid Epoch, Operation[] Operations);
public sealed record Receipt(Guid OpId, string Status, long Seq, long? SourceRev = null, string? ConflictId = null);
public sealed record Resource(string Kind, string Id, long Version, bool Deleted, JsonElement? Value);
public sealed record Change(long Seq, Resource[] Resources);
public sealed record Page(Guid Epoch, long Until, long Next, bool HasMore, Change[] Changes);
public sealed record State(Guid Epoch, long Head, string Account, Guid Device, Guid Grant);

public static partial class Wire
{
    public const long MaxInt = 9_007_199_254_740_991;
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false, MaxDepth = 16
    };

    public static string Key(params string[] parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> size = stackalloc byte[4];
        foreach (var part in parts)
        {
            var bytes = Encoding.UTF8.GetBytes(part);
            BinaryPrimitives.WriteInt32BigEndian(size, bytes.Length);
            hash.AppendData(size);
            hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Json);
    // Protocol v1 has only safe integers. Sort object properties by UTF-16 ordinal as required by JCS.
    public static string Digest(Operation op){
        var text=new StringBuilder();
        void Quote(string s){
            text.Append('"');
            foreach(char c in s){
                text.Append(c switch {
                    '"' => "\\\"", '\\' => "\\\\", '\b' => "\\b", '\t' => "\\t", '\n' => "\\n",
                    '\f' => "\\f", '\r' => "\\r", < ' ' => "\\u"+((int)c).ToString("x4"), _ => c.ToString()
                });
            }
            text.Append('"');
        }
        void Write(JsonElement el){
            switch(el.ValueKind){
                case JsonValueKind.Object:
                    text.Append('{');bool first=true;
                    foreach(var prop in el.EnumerateObject().OrderBy(x=>x.Name,StringComparer.Ordinal)){
                        if(!first)text.Append(',');first=false;Quote(prop.Name);text.Append(':');Write(prop.Value);
                    }
                    text.Append('}');break;
                case JsonValueKind.String:Quote(el.GetString()!);break;
                default:text.Append(el.GetRawText());break;
            }
        }
        Write(Element(op));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
    public static string Pack<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Json) ?? throw new JsonException();
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9_.:-]{0,159}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
    public static bool Id(string? value) => value != null && Identifier().IsMatch(value)
        && !value.StartsWith("unknown-",StringComparison.OrdinalIgnoreCase)
        && !value.StartsWith("unresolved-",StringComparison.OrdinalIgnoreCase)
        && !value.StartsWith("sk-",StringComparison.OrdinalIgnoreCase);
    private static readonly HashSet<string> Models = new(StringComparer.Ordinal)
    { "UNKNOWN","gpt-6.2-astra","gpt-6.2-sol","gpt-6.1-terra","gpt-6.1-sol","gpt-6-astra",
      "gpt-5","gpt-5-codex","gpt-5.1","gpt-5.1-codex","gpt-5.1-codex-max","gpt-5.2","gpt-5.2-codex",
      "gpt-5.3-codex","gpt-5.4","gpt-5.4-mini","gpt-5.5","gpt-4.1","o3","o4-mini" };
    public static string Model(string? value) => value != null && Models.Contains(value) ? value : "UNKNOWN";
    public static bool Name(string? s) => s != null && s.Trim().Length > 0 && s.Length <= 100 && !s.Any(char.IsControl);
    public static bool Valid(Fact f)
    {
        if (!Id(f.ThreadId) || new[]{f.SessionId,f.TurnId,f.ResponseId,f.ParentId,f.ForkId}.Any(x => x != null && !Id(x))
            || f.RuleVersion != "sqlite-v2" || f.Ordinal is < 0 or > MaxInt
            || new[]{f.StartMs,f.EndMs,f.OccurredMs}.Any(x => x is < 0 or > 253402300799999)
            || f.Model != null && Model(f.Model) != f.Model || f.ThreadCumulative is { Valid:false }
            || f.Usage is { Valid:false } || f.Confidence is not (null or "OBSERVED" or "DERIVED" or "PARTIAL" or "UNKNOWN"))
            return false;
        return f.Type switch
        {
            "response" => f.ResponseId != null && f.Usage != null && f.Confidence is "OBSERVED" or "PARTIAL"
                && f.Kind == null && f.ParentId == null && f.ForkId == null && f.Subagent == null && f.StartMs == null && f.EndMs == null,
            "snapshot" => f.Usage != null && f.ThreadCumulative != null && f.ResponseId == null && f.Kind == null
                && f.Confidence is "DERIVED" or "PARTIAL" && f.StartMs == null && f.EndMs == null && f.ParentId == null && f.ForkId == null,
            "lineage" => f.Usage == null && f.ThreadCumulative == null && f.ResponseId == null && f.Kind == null && f.StartMs == null && f.EndMs == null,
            "quality" => f.Kind is "RESPONSE_CONFLICT" or "INVALID_RESPONSE" && f.ResponseId != null && f.Usage == null && f.ThreadCumulative == null,
            "timing" => f.Kind is "BOUNDARY" or "ACTIVE" or "PAUSED" && f.StartMs.HasValue && f.EndMs >= f.StartMs && f.Usage == null && f.ThreadCumulative == null,
            _ => false
        };
    }

    public static bool Valid(Operation o)
    {
        if (o.OpId == Guid.Empty) return false;
        if (o.Kind is "observation.upsert" or "observation.retract")
            return o.ObservationId is Guid g && g != Guid.Empty && o.SourceRev is > 0 and <= MaxInt
                && o.ResourceId == null && o.BaseVersion == null && o.Value == null
                && (o.Kind == "observation.retract" ? o.Fact == null : o.Fact != null && Valid(o.Fact));
        if (o.ResourceId is not Guid id || id == Guid.Empty || o.BaseVersion is not (>= 0 and <= MaxInt)
            || o.ObservationId != null || o.SourceRev != null || o.Fact != null) return false;
        var v = o.Value;
        return o.Kind switch
        {
            "project.create" or "project.update" => v != null && Name(v.Name) && v == new Edit(Name:v.Name,ConflictId:v.ConflictId),
            "group.create" or "group.update" => v != null && Name(v.Name) && v == new Edit(v.Name,v.ProjectId,ConflictId:v.ConflictId),
            "member.set" => v?.GroupId != null && (Id(v.ThreadId) ^ Id(v.SessionId))
                && (v.ThreadId == null || Id(v.ThreadId)) && (v.SessionId == null || Id(v.SessionId))
                && v == new Edit(GroupId:v.GroupId,ThreadId:v.ThreadId,SessionId:v.SessionId,ConflictId:v.ConflictId),
            "project.delete" or "group.delete" or "member.remove" => v == null || (v.ConflictId!=null && v==new Edit(ConflictId:v.ConflictId)),
            "conflict.resolve" => v == null,
            _ => false
        };
    }
}
