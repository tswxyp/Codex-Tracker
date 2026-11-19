using CodexTracker.Billing;

namespace CodexTracker.Tray;

internal static class CreditWeights
{
    // Use the same units for a chat and the historical intervals that price it.
    internal static decimal? Credits(Usage usage,out bool assumedStandard)
    {
        assumedStandard=false;
        if(usage.Total==0)return 0m;
        if(usage.Confidence is not ("OBSERVED" or "DERIVED") || !CodexCreditRates.Rates.TryGetValue(usage.Model,out var rate) ||
            usage.Read is not long cached || cached<0 || cached>usage.Input || usage.Output<0)return null;
        bool missing=string.IsNullOrWhiteSpace(usage.Speed);
        assumedStandard=missing && (rate.Fast.HasValue || rate.Ultrafast.HasValue);
        decimal? speed=missing?1m:rate.Multiplier(usage.Speed);
        return speed*((usage.Input-cached)*rate.Input+cached*rate.Read+usage.Output*rate.Output)/1_000_000m;
    }
}
