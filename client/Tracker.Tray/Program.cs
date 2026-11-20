using System.Runtime.CompilerServices;
[assembly:InternalsVisibleTo("Tracker.Tray.Tests")]
namespace CodexTracker.Tray;

internal static class Program
{
    [STAThread] static int Main(string[] args)
    {
        if(args is ["--owned-worker",var name,var database,var root])
            return WorkerHost.Child(name,database,root);
        using var mutex=new Mutex(true,@"Local\CodexTracker-Tray-v2",out bool first);
        bool firstInstalledLaunch=false;
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(first)
            .OnFirstRun(_=>firstInstalledLaunch=true)
            .OnAfterInstallFastCallback(_=>DesktopUpdates.MigrateLogin(uninstall:false))
            .OnBeforeUninstallFastCallback(_=>DesktopUpdates.MigrateLogin(uninstall:true)).Run();
        if(args.Length!=0)return 2;
        ApplicationConfiguration.Initialize();
        if(!first){
            if(firstInstalledLaunch){
                var display=new DisplayOptions();
                MessageBox.Show(display.T("请从托盘退出正在运行的旧版 Codex Tracker，再从开始菜单打开新版。","Exit the previous Codex Tracker from the tray, then open the installed version from Start."),"Codex Tracker");
            }
            return 0;
        }
        string source=Environment.GetEnvironmentVariable("CODEX_HOME") is {Length:>0} h?h:
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex");
        using var app=new TrayHost(CodexTracker.LocalReport.DefaultDatabase,
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(source)));
        Application.Run(app);
        return 0;
    }
}
