namespace CodexTracker.Tray;

internal static class QuotaIntervals
{
    const long Minute=60_000, ResetTolerance=15*Minute;

    internal sealed record Interval(long Start,long End,int Points=1);
    internal static List<Interval> Intervals(List<QuotaWindow> windows,QuotaWindow target,int maximumStep)
    {
        var result=new List<Interval>();
        QuotaWindow? previous=null,high=null;
        long? crossing=null,firstLowerSeen=null;
        foreach(var w in windows){
            if(w.Seen>target.Seen)break;
            if(w.Plan!=target.Plan || Math.Abs(w.End-target.End)>ResetTolerance){
                previous=high=null;crossing=firstLowerSeen=null;result.Clear();continue;
            }
            if(previous==null || high==null){
                previous=high=w;continue;
            }
            long gap=w.Seen-previous.Seen;
            if(gap>15*Minute)crossing=null;
            if(w.Percent<high.Percent){
                // A chat may report a stale percentage long after other chats first saw
                // the higher reading. Measure the duration of the lower readings themselves.
                firstLowerSeen??=w.Seen;
                if(high.Percent-w.Percent>1 || w.Seen-firstLowerSeen.Value>2*Minute){
                    result.Clear();crossing=null;high=w;
                    firstLowerSeen=null;
                }
            }else{
                firstLowerSeen=null;
                if(w.Percent>high.Percent){
                    decimal step=w.Percent-high.Percent;
                    bool observed=step>=1 && step<=maximumStep && step==decimal.Truncate(step) &&
                        w.Percent<100 && gap<=3*Minute && previous.Percent==high.Percent;
                    if(observed && crossing is long start && w.Seen>start)result.Add(new(start,w.Seen,(int)step));
                    crossing=observed?w.Seen:null;
                    high=w;
                }
            }
            previous=w;
        }
        return result;
    }
}
