using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;

namespace CodexTracker;

// Windows records the actual sleep and wake instants as UTC fields in this event.
internal static class PowerHistory
{
    public static void Refresh(Store db,string device)
    {
        var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"wevtutil.exe"))
        {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{"qe","System","/q:*[System[Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1]]","/f:xml","/e:Events","/uni:false"})
            start.ArgumentList.Add(arg);
        try
        {
            using var p=Process.Start(start)!;
            var read=p.StandardOutput.ReadToEndAsync();var errors=p.StandardError.ReadToEndAsync();
            if(!p.WaitForExit(10000)){p.Kill();throw new IOException("timeout");}
            if(p.ExitCode!=0)throw new IOException("eventlog");
            var xml=XDocument.Parse(read.GetAwaiter().GetResult());errors.GetAwaiter().GetResult();
            db.Atomic(()=>
            {
                var changed=new HashSet<string>();
                foreach(var ev in xml.Descendants().Where(x=>x.Name.LocalName=="Event"))
                {
                    string? Field(string name)=>ev.Descendants().FirstOrDefault(x=>x.Name.LocalName=="Data" && (string?)x.Attribute("Name")==name)?.Value;
                    if(!DateTimeOffset.TryParse(Field("SleepTime"),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var a)
                        || !DateTimeOffset.TryParse(Field("WakeTime"),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var b) || b<=a)continue;
                    var from=a.ToUnixTimeMilliseconds();var to=b.ToUnixTimeMilliseconds();
                    if(db.Run("INSERT OR IGNORE INTO PowerGap VALUES($p0,$p1,$p2)",device,from,to)==0)continue;
                    foreach(var row in db.Rows("SELECT thread_id FROM TaskTiming WHERE device_id=$p0 AND first_ms<$p1 AND last_ms>$p2",device,to,from))changed.Add((string)row[0]!);
                }
                foreach(var thread in changed)Projection.Build(db,device,thread);
                db.Run("DELETE FROM DataQuality WHERE id=$p0",Store.Key(device,"POWER_HISTORY_UNAVAILABLE"));
            });
        }
        catch(Exception ex) when(ex is IOException or System.ComponentModel.Win32Exception or System.Xml.XmlException or UnauthorizedAccessException)
        {
            db.Run("INSERT OR REPLACE INTO DataQuality VALUES($p0,$p1,NULL,NULL,NULL,'POWER_HISTORY_UNAVAILABLE','PARTIAL','')",Store.Key(device,"POWER_HISTORY_UNAVAILABLE"),device);
        }
    }
}
