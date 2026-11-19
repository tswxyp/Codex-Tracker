using System.IO;
using System.Windows;
using Microsoft.Win32;
namespace CodexTracker.Viewer;

public partial class TaskWindow : Window
{
    private string path=Environment.GetCommandLineArgs().Skip(1).FirstOrDefault()??LocalReport.DefaultDatabase;
    public TaskWindow() => InitializeComponent();
    private void Choose(object sender,RoutedEventArgs e)
    {
        var dialog=new OpenFileDialog{Filter="Tracker 数据库|*.sqlite"};
        if(dialog.ShowDialog()!=true)return;
        path=dialog.FileName;Refresh(sender,e);
    }
    private async void Refresh(object sender,RoutedEventArgs e)
    {
        IsEnabled=false;
        try
        {
            if(!File.Exists(path)){Status.Text="尚未找到数据库。运行采集器后刷新，或选择已有的 Tracker 数据库。";return;}
            var rows=await Task.Run(()=>LocalReport.Read(path));
            string Duration(long ms)=>TimeSpan.FromMilliseconds(ms).ToString(@"d\.hh\:mm\:ss");
            Tasks.ItemsSource=rows.Select(r=>new {r.Thread,r.Device,r.Total,r.PartialTokens,r.CacheRead,r.CacheWrite,
                Wall=Duration(r.TotalMs),Active=Duration(r.ActiveMs),Paused=Duration(r.PausedMs),Unknown=Duration(r.UnknownMs),
                State=r.State switch{"COMPLETED"=>"已完成","RUNNING"=>"最后观测为运行中","PAUSED"=>"已中断或暂停",_=>"未知"},r.Issues}).ToList();
            Status.Text=$"{rows.Count} 个线程　·　{path}";
        }
        catch(Exception ex){Tasks.ItemsSource=null;Status.Text="读取失败　"+ex.Message;}
        finally{IsEnabled=true;}
    }
}
