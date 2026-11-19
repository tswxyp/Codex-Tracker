using System.Runtime.CompilerServices;
[assembly:InternalsVisibleTo("Tracker.Tray.Tests")]
namespace CodexTracker.Tray;

internal static class Program
{
    [STAThread] static int Main(string[] args)
    {
        if(args is ["--owned-worker",var name,var database,var root])
            return WorkerHost.Child(name,database,root);
        if(args.Length!=0)return 2;
        ApplicationConfiguration.Initialize();
        using var mutex=new Mutex(true,@"Local\CodexTracker-Tray-v2",out bool first);
        if(!first)return 0;
        string source=Environment.GetEnvironmentVariable("CODEX_HOME") is {Length:>0} h?h:
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex");
        using var app=new TrayHost(CodexTracker.LocalReport.DefaultDatabase,
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(source)));
        Application.Run(app);
        return 0;
    }
}
