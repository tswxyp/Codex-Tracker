using CodexTracker.Tray;
using Binding=CodexTracker.Tray.Binding;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
namespace Tracker.Tray.Tests;

internal static class Program
{
    [DllImport("user32")] static extern void mouse_event(uint flags,uint x,uint y,int data,nuint extra);
    [DllImport("user32")] static extern int GetWindowLongW(nint hwnd,int index);
    [DllImport("user32")] static extern nint GetWindow(nint hwnd,uint command);
    delegate bool EnumWindow(nint hwnd,nint param);
    [DllImport("user32")] static extern bool EnumWindows(EnumWindow callback,nint param);
    [DllImport("user32")] static extern nint SendMessageW(nint hwnd,int message,nint wParam,nint lParam);
    static int checks;
    static void Check(bool ok,string name)
    {
        if(!ok)throw new Exception(name);
        Console.WriteLine("PASS "+name);checks++;
    }
    [STAThread] public static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();
        if(args is ["live"]){Live(false);return 0;}
        if(args is ["scroll"])
        {
            var hwnd=Native.GetForegroundWindow();
            Check(Native.IsCodex(hwnd),"front Codex package");
            var locator=new Sidebar();
            var first=locator.Read(hwnd,false).Rows;
            var old=Cursor.Position;
            var box=first[first.Count/2].Bounds;
            var spot=new Point(box.Left+box.Width/2,box.Top+box.Height/2);
            Cursor.Position=spot;
            Check(Native.GetAncestor(Native.WindowFromPoint(spot),2)==hwnd,"wheel over the foreground Codex sidebar");
            mouse_event(0x800,0,0,-1200,0);
            try
            {
                for(int i=0;i<18;i++)
                {
                    Thread.Sleep(150);
                    if(Native.GetForegroundWindow()!=hwnd)break;
                    var after=locator.Read(hwnd,false).Rows;
                    var enter=after.Select(r=>r.Name).Except(first.Select(r=>r.Name)).Count();
                    var leave=first.Select(r=>r.Name).Except(after.Select(r=>r.Name)).Count();
                    if(enter>0 && leave>0 && after.Count>0)
                    {
                        Console.WriteLine($"Before {first.Count}, after {after.Count}, entered {enter}, left {leave}.");
                        return 0;
                    }
                }
                throw new Exception("No visible row replacement observed.");
            }
            finally
            {
                if(Cursor.Position==spot && Native.GetForegroundWindow()==hwnd)
                {
                    mouse_event(0x800,0,0,1200,0);Cursor.Position=old;
                }
            }
        }
        if(args is ["relocate"])
        {
            var hwnd=Native.GetForegroundWindow();
            Check(Native.IsCodex(hwnd),"front Codex package");
            var original=new Sidebar().Read(hwnd,false);
            var fallback=new Sidebar(semanticNavigationOnly:true).Read(hwnd,false);
            var originalRows=original.Rows.Select(r=>(r.Name,r.Bounds,r.Current)).ToHashSet();
            Check(originalRows.Count>0 && originalRows.SetEquals(fallback.Rows.Select(r=>(r.Name,r.Bounds,r.Current))),"real UIA recaptured after preferred panel selector unavailable");
            return 0;
        }
        if(args is ["hover"]){Live(true);return 0;}
        if(args is ["host",var path,var source,var settings,var exe])
        {
            using var app=new TrayHost(path,source,settings,exe);
            var commands=new System.Collections.Concurrent.ConcurrentQueue<string>();
            _=Task.Run(()=>{while(Console.ReadLine() is {} line)commands.Enqueue(line);});
            using var timer=new System.Windows.Forms.Timer{Interval=50};
            timer.Tick+=(_,_)=>{while(commands.TryDequeue(out var c))
            {
                if(c=="exit"){app.ExitThread();return;}
                app.SetPaused(c=="pause");
            }};
            timer.Start();Application.Run(app);return 0;
        }
        var rows=new[]{
            new CatalogEntry("local","1","short",true),new("local","2","duplicate",true),new("local","3","duplicate",true),
            new("remote","4","remote",true),new("local","5","pending",false),new("local","6","alias",true),
            new("local","7","cross",true),new("remote","7","cross",true),new("local","8","fresh",true)};
        var catalog=new TitleCatalog(rows,[("alias","retired")]);
        Check(catalog.Resolve("short")=="1" && catalog.Resolve("Short")==null,"exact title");
        Check(new[]{"duplicate","remote","pending","alias","cross","absent"}.All(t=>catalog.Resolve(t)==null),"ambiguous, foreign, pending and unknown suppressed");
        var r=new ChatRow("short",new Rectangle(50,90,324,30),[new Rectangle(320,95,20,20)],true);
        var stats=new Stats();
        stats.Usage.AddRange([new("A","OBSERVED",10,8,2,4,0),new("B","DERIVED",20,17,3,2,0),new("A","PARTIAL",100,99,1,80,0)]);
        var chinese=new DisplayOptions("zh-CN","CNY");
        var fields=stats.Fields(chinese).ToDictionary(f=>f.Label,f=>f.Value);
        Check(stats.Total==30 && stats.Partial==100 && fields["输入"]=="25" && fields["缓存命中率"]=="24%","confirmed inputs and weighted cache hit rate");
        Check(fields.Count==8 && fields["模型区分"]=="B · A" && fields["本会话收入"]=="未填写","eight fields and actual models");
        var unknown=new Stats();unknown.Usage.Add(new("X","PARTIAL",200,199,1,null,null));
        Check(unknown.Total==null && unknown.Labels(chinese).Last()=="用量待确认" && unknown.Fields(chinese).Single(f=>f.Label=="缓存命中率").Value=="暂无数据","partial-only row not exact or zero");
        var data=new DisplayData(catalog,new(){["1"]=stats,["8"]=unknown},0);
        Check(Binding.Rows([r,r],data).Count==0,"duplicate visible rows suppressed");
        Check(Binding.Rows([r],data).Single().Id=="1" && Binding.Rows([r with{Name="fresh",Current=false}],data).Single().Id=="8","virtualized bounds rebound by identity");
        Check(Binding.Rows([],data).Count==0 && Binding.Rows([r with{Name="absent"}],data).Count==0,"collapsed, closed or unrecognized layout");
        foreach(int dpi in new[]{96,120,144})
        foreach(int left in new[]{-1920,0,1920})
        {
            var work=new Rectangle(left,-100,1920,1080);
            var box=new Rectangle(left+15,50,OverlayLayout.Px(324,dpi),OverlayLayout.Px(30,dpi));
            var action=new Rectangle(box.Right-OverlayLayout.Px(46,dpi),box.Y,OverlayLayout.Px(40,dpi),box.Height);
            var row=r with{Bounds=box,Actions=[action]};
            var label=OverlayLayout.Inline(row,dpi,OverlayLayout.Px(58,dpi),new(OverlayLayout.Px(85,dpi),OverlayLayout.Px(16,dpi)));
            var longTitleLabel=OverlayLayout.Inline(row,dpi,box.Width,new(90,18));
            Check(!label.IsEmpty && box.Contains(label) && !label.IntersectsWith(action) &&
                !longTitleLabel.IsEmpty && longTitleLabel.Right==label.Right && work.Contains(OverlayLayout.Detail(box,work,new(500,350),dpi)),
                $"DPI {dpi}, monitor origin {left}, action avoidance and detail clamp");
        }
        TestWal();
        TestWindow();
        if(args is [var sample,var root])
        {
            var sampleRows=Data.Read(sample,root);
            Check(sampleRows.Values.Sum(x=>x.Total??0)==14730816 && sampleRows.Values.Sum(x=>x.Partial)==58814396,"Phase 1 real sample, attributable and partial subtotals");
            Check(Data.Read(sample,root+"-elsewhere").Count==0,"device root isolation");
        }
        Console.WriteLine($"Passed {checks} checks.");return 0;
    }
    static void TestWal()
    {
        var dir=Directory.CreateTempSubdirectory("Tracker-test-");
        try
        {
            string file=Path.Combine(dir.CreateSubdirectory("sqlite").FullName,"codex.db");
            using var c=Data.Open(file,SqliteOpenMode.ReadWriteCreate);
            using var q=c.CreateCommand();
            q.CommandText="""PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE local_thread_catalog(host_id,thread_id,display_title,missing_candidate,pending_observed_title);""";
            q.ExecuteNonQuery();
            q.CommandText="PRAGMA wal_checkpoint(TRUNCATE)";q.ExecuteNonQuery();
            q.CommandText="INSERT INTO local_thread_catalog VALUES('local','first','WAL',0,0)";q.ExecuteNonQuery();
            byte[] Before(string suffix){using var input=new FileStream(file+suffix,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var copy=new MemoryStream();input.CopyTo(copy);return copy.ToArray();}
            var baseline=Before("");var wal=Before("-wal");
            Check(wal.Length>32 && TitleCatalog.Read(dir.FullName).Resolve("WAL")=="first","committed WAL-only title");
            Check(baseline.SequenceEqual(Before("")) && wal.SequenceEqual(Before("-wal")),"source database and WAL bytes preserved");
            q.CommandText="INSERT INTO local_thread_catalog VALUES('local','second','WAL',0,0)";q.ExecuteNonQuery();
            Check(TitleCatalog.Read(dir.FullName).Resolve("WAL")==null,"new WAL collision revokes match");
        }
        finally{dir.Delete(recursive:true);}
    }
    static void TestWindow()
    {
        using var target=new Form{Text="Tracker hit-test fixture",StartPosition=FormStartPosition.CenterScreen,Size=new(420,240)};
        using var button=new Button{Text="Target",Bounds=new(25,25,250,110)};
        target.Controls.Add(button);target.Show();target.Activate();Application.DoEvents();
        var b=button.RectangleToScreen(button.ClientRectangle);var point=new Point(b.Left+40,b.Top+30);
        nint before=Native.WindowFromPoint(point),foreground=Native.GetForegroundWindow();
        using var overlay=new Overlay();overlay.Place(target.Handle,b,"Token 123",96,true,false);
        Application.DoEvents();
        Check((GetWindowLongW(overlay.Handle,-20)&Overlay.Styles)==Overlay.Styles,"layered transparent noactivate tool window");
        Check(before==button.Handle && Native.WindowFromPoint(point)==button.Handle && Native.GetForegroundWindow()==foreground,"actual HWND hit-through and foreground unchanged");
        var original=Cursor.Position;
        int clicks=0,wheels=0;
        button.Click+=(_,_)=>clicks++;
        button.MouseWheel+=(_,e)=>wheels+=e.Delta;
        button.Focus();
        try
        {
            Cursor.Position=point;Application.DoEvents();
            Check(Native.GetForegroundWindow()==target.Handle && Native.WindowFromPoint(point)==button.Handle,"input target belongs to isolated test form");
            mouse_event(2,0,0,0,0);mouse_event(4,0,0,0,0);Application.DoEvents();
            mouse_event(0x800,0,0,120,0);
            for(int i=0;i<15 && (clicks==0 || wheels==0);i++){Thread.Sleep(10);Application.DoEvents();}
            Check(clicks==1 && wheels==120,"native click and wheel delivered through overlay");
        }
        finally{if(Cursor.Position==point)Cursor.Position=original;}
        Check(SendMessageW(overlay.Handle,0x84,0,0)==-1 && SendMessageW(overlay.Handle,0x21,0,0)==3,"WM_NCHITTEST and WM_MOUSEACTIVATE");
        var bg=overlay.BackColor;overlay.Place(target.Handle,b,"详情",96,false,true);
        Check(bg!=overlay.BackColor && Native.WindowFromPoint(point)==before,"dark and light layered detail");
        target.WindowState=FormWindowState.Maximized;Application.DoEvents();
        var large=Native.Bounds(target.Handle);
        target.WindowState=FormWindowState.Normal;Application.DoEvents();
        Check(large.Width>Native.Bounds(target.Handle).Width,"real window maximize and restore");
        overlay.Hide();target.Close();
    }
    static int Live(bool hover)
    {
        nint hwnd=Native.GetForegroundWindow();
        if(!Native.IsCodex(hwnd)){Console.WriteLine("Codex not foreground");return 0;}
        var src=Environment.GetEnvironmentVariable("CODEX_HOME")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex");
        var frame=new Sidebar().Read(hwnd,probePoints:false);
        TitleCatalog? catalog=null;
        for(int i=0;i<5 && catalog==null;i++){try{catalog=TitleCatalog.Read(src);}catch(IOException) when(i<4){Thread.Sleep(120);}}
        if(catalog==null)throw new IOException("目录持续更新。");
        var bound=Binding.Rows(frame.Rows,new(catalog,Data.Read(CodexTracker.LocalReport.DefaultDatabase,src),0));
        var labelBounds=new List<Rectangle>();int labels=0,details=0,pass=0,actionPoints=0,actionPass=0;
        EnumWindows((h,_)=>{
            if(!Native.IsWindowVisible(h) || (GetWindowLongW(h,-20)&Overlay.Styles)!=Overlay.Styles || GetWindow(h,4)!=hwnd)return true;
            Native.GetWindowThreadProcessId(h,out int pid);
            using var p=Process.GetProcessById(pid);
            if(p.ProcessName!="CodexTracker.Tray")return true;
            var b=Native.Bounds(h);if(b.Height>frame.Dpi/2)details++;else {labels++;labelBounds.Add(b);}
            var hit=Native.WindowFromPoint(new Point(b.Left+b.Width/2,b.Top+b.Height/2));
            if(Native.GetAncestor(hit,2)==hwnd)pass++;
            return true;
        },0);
        if(hover)
        {
            if(bound.Count==0)throw new InvalidOperationException("No matched chat for hover test.");
            var rect=bound[0].Row.Bounds;var old=Cursor.Position;
            var spot=new Point(rect.Left+OverlayLayout.Px(18,frame.Dpi),rect.Top+rect.Height/2);
            if(Native.GetAncestor(Native.WindowFromPoint(spot),2)!=hwnd)throw new InvalidOperationException("Test point no longer targets Codex.");
            Cursor.Position=spot;
            try
            {
                for(int i=0;i<8;i++)
                {
                    Thread.Sleep(250);
                    if(Live(false)>0)return 1;
                }
                throw new Exception("No visible detail within timeout.");
            }
            finally{if(Cursor.Position==spot)Cursor.Position=old;}
        }
        foreach(var row in bound)foreach(var a in row.Row.Actions)
        {
            actionPoints++;
            if(Native.GetAncestor(Native.WindowFromPoint(new(a.Left+a.Width/2,a.Top+a.Height/2)),2)==hwnd)actionPass++;
        }
        Console.WriteLine(JsonSerializer.Serialize(new{frame.Candidates,Visible=frame.Rows.Count,frame.ScrollAncestors,Current=frame.Rows.Count(x=>x.Current),
            Resolved=frame.Rows.Count(x=>catalog.Resolve(x.Name)!=null),Bound=bound.Count,Labels=labels,Rects=labelBounds.Select(b=>b.ToString()),Details=details,PassThrough=pass,
            ActionPoints=actionPoints,ActionPass=actionPass,frame.Dpi,Monitors=Screen.AllScreens.Length,FrameMs=Environment.TickCount64-frame.Started}));
        if(labels!=0 || pass!=details || actionPass!=actionPoints)throw new Exception("Unexpected HWND hit");
        return details;
    }
}
