namespace CodexTracker.Tray;

internal sealed record WeeklyEstimate(MoneyAmount Cost,decimal Percent,long UnpricedTokens=0,bool AssumedStandard=false);

internal static class SubscriptionCost
{
    // Reference from the Pro $200 weekly allowance, expressed in CreditWeights units.
    const decimal CreditsPerWeeklyPoint = 335.669685m;

    internal static WeeklyEstimate? Estimate(IEnumerable<Usage> usage, SubscriptionPlan plan)
    {
        decimal? pricedUnits = null;
        long unpricedTokens = 0;
        bool assumedStandard = false;
        foreach (var row in usage)
        {
            if (CreditWeights.Credits(row, out bool missingSpeed) is not decimal credits)
            {
                unpricedTokens += row.Total;
                continue;
            }
            pricedUnits = (pricedUnits ?? 0m) + credits;
            assumedStandard |= missingSpeed;
        }

        if (pricedUnits is not decimal units) return null;
        decimal weeklyPercent = units / CreditsPerWeeklyPoint;
        var budget = plan.WeeklyBudget;
        var cost = new MoneyAmount(budget.Amount * weeklyPercent / 100m, budget.Currency);
        return new(cost, weeklyPercent, unpricedTokens, units > 0m && assumedStandard);
    }
}
