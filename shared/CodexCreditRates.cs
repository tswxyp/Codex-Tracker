namespace CodexTracker.Billing;

// A dated, reproducible weighting convention for personal Codex subscription usage.
// Input includes cache writes; Codex credit billing has no separate cache-write charge.
internal static class CodexCreditRates
{
    internal const string Version="codex-personal-20261119";
    internal const string Source="https://learn.chatgpt.com/docs/pricing?codex-pricing-plans=individual#token-rates";
    internal sealed record Rate(decimal Input,decimal Read,decimal Output,decimal? Fast=null,decimal? Ultrafast=null)
    {
        public decimal? Multiplier(string? speed)=>speed?.ToLowerInvariant() switch{
            "default" or "standard"=>1m,
            "fast" or "priority"=>Fast,
            "ultrafast"=>Ultrafast,
            _=>null
        };
    }
    // Credits per million tokens at Standard; speed factors for included subscription usage.
    internal static readonly IReadOnlyDictionary<string,Rate> Rates=new Dictionary<string,Rate>(StringComparer.OrdinalIgnoreCase){
        ["gpt-6.2-astra"]=new(200,10,1000,2,6), ["gpt-6.2-sol"]=new(50,2.5m,250,2,6),
        ["gpt-6-astra"]=new(250,25,1250,2.5m,8), ["gpt-6.1-sol"]=new(50,2.5m,250,2.5m,8),
        ["gpt-6.1-terra"]=new(25,1.25m,125,2.5m), ["gpt-6-sol"]=new(50,5,250,2.5m),
        ["gpt-6-luna"]=new(2.5m,.25m,12.5m,2.5m), ["gpt-5.6-sol"]=new(100,10,500),
        ["gpt-5.6-terra"]=new(50,5,300), ["gpt-5.6-luna"]=new(5,.5m,30)
    };
}
