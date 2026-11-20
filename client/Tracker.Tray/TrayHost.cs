using Microsoft.Win32;
using Microsoft.Data.Sqlite;
using System.Runtime.InteropServices;

namespace CodexTracker.Tray;

internal sealed record DisplayData(TitleCatalog Catalog,Dictionary<string,Stats> Stats,long ReadAt);
internal sealed record BoundRow(ChatRow Row,string Id,Stats Stats);
internal static class Binding
{
    internal static List<BoundRow> Rows(IEnumerable<ChatRow> rows,DisplayData data)
    {
        var result=new List<BoundRow>();
        foreach(var group in rows.GroupBy(x=>x.Name,StringComparer.Ordinal))
        {
            if(group.Count()!=1)continue;
            var row=group.First();
            if(!row.HitVerified)continue;
            if(data.Catalog.Resolve(row.Name) is {} id && data.Stats.TryGetValue(id,out var stats))
                result.Add(new(row,id,stats));
        }
        return result;
    }
}

internal sealed class TrayHost:ApplicationContext
{
    [DllImport("user32")] static extern nint GetDC(nint hwnd);
    [DllImport("user32")] static extern int ReleaseDC(nint hwnd,nint dc);
    [DllImport("gdi32")] static extern uint GetPixel(nint dc,int x,int y);
    readonly string database,root,pausedFile;
    readonly SidebarSettings settingsStore;
    readonly MarketRates marketRates;
    DisplayOptions options=new();
    readonly Icon trayIcon;
    long regionRequest;
    readonly WorkerHost worker;
    readonly ScrollGuard guard;
    Sidebar? locator;
    long invalidatedAt,lastFrameReceived;
    long locatorEpoch=-1;
    string[] requestedTitles=[];
    readonly NotifyIcon icon;
    readonly DesktopUpdates updates=new();
    readonly ToolStripMenuItem updateMenu;
    bool updateBalloon;
    readonly ToolStripMenuItem pause,status,login,subscription,report,language,currency,palette,quit,about,sampling,samplePreview;
    CancellationTokenSource? sampleConsent;
    readonly List<SampleSubmission> samples=[];
    bool sampleReading,sampleSending;
    long consentAt,sampleCursor,nextSampleRead,sessionGeneration,consentGeneration;
    bool sampleUnavailable;
    CommunityDialog? sampleDialog;
    readonly System.Windows.Forms.Timer tick=new(){Interval=50};
    readonly Overlay detail=new();
    readonly IncomeActionBar incomeActions=new();
    readonly ContextMenuStrip menu=new();
    bool paused;
    Task<DisplayData>? reading;
    bool discardRead;
    Task<SidebarFrame>? locating;
    SidebarFrame? frame;
    DisplayData? data;
    readonly CatalogCache catalogCache=new();
    nint target;
    long nextLocate,nextData,nextDiagnostic;
    long leftRowAt;
    (ChatRow Row,BoundRow? Match)? shownRow;
    bool editingIncome;
    string info="等待 Codex";
    internal TrayHost(string database,string root,string? settingsDirectory=null,string? hostExe=null)
    {
        this.database=database;this.root=root;
        string settings=settingsDirectory??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CodexTracker");
        Directory.CreateDirectory(settings);
        settingsStore=new(settings,root);
        marketRates=new(settings);
        try{options=settingsStore.ReadOptions();}catch(SqliteException){ /* Display remains available if preferences are locked. */ }
        pausedFile=Path.Combine(settings,"monitor.paused");
        paused=File.Exists(pausedFile);
        worker=new(database,root,hostExe);
        guard=new ScrollGuard(()=>{
            if(target==0 || Native.GetForegroundWindow()!=target || !OverRows(Cursor.Position))return;
            invalidatedAt=Environment.TickCount64;frame=null;nextLocate=invalidatedAt+100;HideAll();
        });
        status=new("等待 Codex"){Enabled=false};
        pause=new(paused?"恢复监控":"暂停监控",null,(_,_)=>SetPaused(!paused));
        login=new("登录 Windows 时启动",null,(_,_)=>ToggleLogin());
        sampling=new("",null,(_,_)=>ToggleSampling());
        samplePreview=new("",null,(_,_)=>ShowCommunity(CommunityPage.Preview));
        about=new("",null,(_,_)=>ShowCommunity(CommunityPage.About));
        updateMenu=new("",null,(_,_)=>UpdateClicked());
        updateMenu.Visible=updates.Installed;
        subscription=new("",null,(_,_)=>EditSubscription());
        report=new("",null,(_,_)=>OpenReport());
        language=new();currency=new();palette=new();
        foreach(string choice in new[]{"auto","red","green"})
            palette.DropDownItems.Add(new ToolStripMenuItem("",null,(_,_)=>ChangeOptions(options with {ProfitColors=choice})){Tag=choice});
        foreach(var (key,title) in new[]{("auto",""),("zh-CN","简体中文"),("en","English"),("ja","日本語"),("ko","한국어")})
            language.DropDownItems.Add(new ToolStripMenuItem(title,null,(_,_)=>ChangeOptions(options with {Language=key})){Tag=key});
        foreach(string key in Money.Codes)
            currency.DropDownItems.Add(new ToolStripMenuItem(key,null,(_,_)=>ChangeOptions(options with {Currency=options.Currency==key?"auto":key})){Tag=key});
        quit=new("",null,(_,_)=>ExitThread());
        menu.Items.AddRange(new ToolStripItem[]{report,subscription,language,currency,palette,new ToolStripSeparator(),status,pause,login,sampling,samplePreview,new ToolStripSeparator(),updateMenu,about,quit});
        TranslateMenu();
        using var iconStream=typeof(TrayHost).Assembly.GetManifestResourceStream("CodexTracker.Tray.Tracker.ico")!;
        trayIcon=new Icon(iconStream,SystemInformation.SmallIconSize);
        icon=new(){Icon=trayIcon,Text="Codex Tracker",ContextMenuStrip=menu,Visible=true};
        icon.DoubleClick+=(_,_)=>OpenReport();
        icon.BalloonTipClicked+=(_,_)=>{if(updateBalloon)UpdateClicked();else EditSubscription();};
        incomeActions.EditRequested+=EditFromCard;
        menu.Opening+=(_,_)=>{
            using var key=Registry.CurrentUser.OpenSubKey(DesktopUpdates.LoginKey);
            login.Checked=string.Equals(key?.GetValue(DesktopUpdates.LoginValue) as string,DesktopUpdates.LoginCommand,StringComparison.OrdinalIgnoreCase);
        };
        tick.Tick+=(_,_)=>Refresh();tick.Start();
        _=menu.Handle;
        SystemEvents.SessionSwitch+=OnSessionSwitch;
        SystemEvents.SessionEnding+=OnSessionEnding;
        _=RefreshIpCurrency();
        try{
            if(settingsStore.ReadPlan()==null && settingsStore.FirstFeePrompt()){
                icon.ShowBalloonTip(12000,T("输入订阅费用","Enter subscription fee"),
                    T("已开始记录本机用量。点此填写月费，以计算对应成本和盈亏。","Enter your monthly fee to calculate cost and profit for recorded usage."),ToolTipIcon.Info);
            }
        }catch(Exception e) when(e is IOException or SqliteException or UnauthorizedAccessException){ }
        updates.Changed+=UpdateChanged;
        updates.Start();
    }
    void UpdateClicked()
    {
        if(updates.State!=UpdateState.Ready){_=updates.Check();return;}
        if(editingIncome)return;
        if(updates.ScheduleRestart())ExitThread();
        else MessageBox.Show(T("请重新打开 Codex Tracker 以完成更新。","Reopen Codex Tracker to finish updating."),"Codex Tracker");
    }
    void UpdateChanged()
    {
        TranslateUpdate();
        if(updates.State!=UpdateState.Ready)return;
        updateBalloon=true;
        icon.ShowBalloonTip(10000,"Codex Tracker",T("新版已就绪。点此立即更新，或在下次启动时自动更新。","Update ready. Click to update now, or it will apply on the next start."),ToolTipIcon.Info);
    }
    void TranslateUpdate()
    {
        updateMenu.Enabled=updates.State is not (UpdateState.Checking or UpdateState.Downloading);
        updateMenu.Text=updates.State switch{
            UpdateState.Checking=>T("正在检查更新…","Checking for updates…"),
            UpdateState.Downloading=>T("正在下载新版…","Downloading update…"),
            UpdateState.Ready=>T("新版已就绪 · 立即更新","Update ready · Restart Tracker"),
            UpdateState.Current=>T("已是最新版 · 再次检查","Up to date · Check again"),
            UpdateState.Failed=>T("更新检查失败 · 重试","Update unavailable · Retry"),
            _=>T("检查更新","Check for updates")};
    }
    string T(string zh,string en)=>options.T(zh,en);
    DialogResult ShowCommunity(CommunityPage page)
    {
        if(editingIncome)return DialogResult.Cancel;
        editingIncome=true;HideAll();
        try{
            using var dialog=new CommunityDialog(options,page);
            if(page!=CommunityPage.About)sampleDialog=dialog;
            if(page==CommunityPage.Preview){
                dialog.UpdatePreview(samples,sampleUnavailable);
                dialog.ExclusionRequested+=item=>_=ReportSample(item);
            }
            return dialog.ShowDialog();
        }finally{sampleDialog=null;editingIncome=false;}
    }
    void ToggleSampling()
    {
        if(sampleConsent!=null){StopSampling();return;}
        long generation=Interlocked.Read(ref sessionGeneration);
        if(paused || ShowCommunity(CommunityPage.Consent)!=DialogResult.OK || generation!=Interlocked.Read(ref sessionGeneration))return;
        consentAt=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        consentGeneration=generation;sampleCursor=consentAt;
        nextSampleRead=Environment.TickCount64+60_000;
        sampleConsent=new();sampling.Checked=true;TranslateMenu();
    }
    void StopSampling()
    {
        var prior=sampleConsent;sampleConsent=null;
        prior?.Cancel();prior?.Dispose();
        samples.Clear();sampleReading=false;sampleSending=false;sampleUnavailable=false;
        sampling.Checked=false;samplePreview.Available=false;
        sampleDialog?.Close();
    }
    void OnSessionSwitch(object? sender,SessionSwitchEventArgs e)
    {
        if(e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff or
            SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect or SessionSwitchReason.SessionLogon)EndConsent();
    }
    void OnSessionEnding(object? sender,SessionEndingEventArgs e)=>EndConsent();
    async Task ReportSample(SampleSubmission current)
    {
        if(sampleConsent is not {} consent || !samples.Contains(current) ||
            consentGeneration!=Interlocked.Read(ref sessionGeneration))return;
        var send=current.ReportMultitaskingAsync(consent.Token);
        sampleDialog?.UpdatePreview(samples,sampleUnavailable);
        await send;
        if(sampleConsent==consent)sampleDialog?.UpdatePreview(samples,sampleUnavailable);
    }
    async Task SendNextSample(CancellationTokenSource consent,SampleSubmission current)
    {
        sampleSending=true;
        try{
            var send=current.NeedsUpload?current.SendAsync(database,root,consentAt,settingsStore.Speeds.Current,consent.Token):
                current.ReportMultitaskingAsync(consent.Token);
            sampleDialog?.UpdatePreview(samples,sampleUnavailable);
            await send;
        }finally{
            if(sampleConsent==consent){
                sampleSending=false;
                if(current.State is UploadState.Ineligible or UploadState.Rejected)samples.Remove(current);
                sampleDialog?.UpdatePreview(samples,sampleUnavailable);
            }
        }
    }
    void EndConsent()
    {
        Interlocked.Increment(ref sessionGeneration);
        try{Volatile.Read(ref sampleConsent)?.Cancel();}catch(ObjectDisposedException){ }
        try{if(menu.InvokeRequired)menu.BeginInvoke(StopSampling);else StopSampling();}
        catch(InvalidOperationException) when(menu.Disposing || menu.IsDisposed){ }
    }
    async Task ReadSample(CancellationTokenSource consent)
    {
        sampleReading=true;
        var token=consent.Token;
        long since=consentAt,cursor=sampleCursor;
        var slots=new int[CalibrationSample.MaxPoints+1];
        for(int i=1;i<slots.Length;i++)slots[i]=CalibrationSample.PerSize-samples.Count(s=>s.Sample.Window.Points==i);
        try{
            var result=await Task.Run(()=>CalibrationSample.Read(database,root,since,cursor,slots,settingsStore.Speeds.Current,token),token);
            if(sampleConsent!=consent || token.IsCancellationRequested)return;
            sampleUnavailable=false;
            foreach(var sample in result){
                samples.Add(new(sample));sampleCursor=sample.Window.End;
            }
        }catch(OperationCanceledException) when(token.IsCancellationRequested){ }
        catch(Exception e) when(e is SqliteException or IOException or UnauthorizedAccessException or OverflowException or FormatException){
            if(sampleConsent==consent)sampleUnavailable=true;
        }finally{
            if(sampleConsent==consent){
                sampleReading=false;nextSampleRead=Environment.TickCount64+60_000;
                sampleDialog?.UpdatePreview(samples,sampleUnavailable);
            }
        }
    }
    void TranslateMenu()
    {
        string label=T("语言","Language");
        language.Text=label=="Language"?label:label+" (Language)";
        currency.Text=T("显示货币","Display currency");
        subscription.Text=T("输入订阅费用","Enter subscription fee");
        report.Text=T("近期盈亏","Recent profit and loss");
        palette.Text=T("盈亏颜色","Profit and loss colors");
        foreach(ToolStripMenuItem item in palette.DropDownItems){
            item.Checked=Equals(item.Tag,options.ProfitColors);
            item.Text=item.Tag switch{"red"=>T("红盈绿亏","Red gains, green losses"),"green"=>T("绿盈红亏","Green gains, red losses"),
                _ when options.HasExplicitCurrency=>options.Format("跟随显示货币 · {0}","Use display currency · {0}",options.Currency),
                _=>T("跟随 Windows 系统语言","Windows display language")};
        }
        quit.Text=T("退出","Exit");
        login.Text=T("登录 Windows 时启动","Start at Windows sign-in");
        about.Text=T("关于","About");
        TranslateUpdate();
        sampling.Text=T("我愿意帮助采集并上传用量样本","Contribute usage samples");
        sampling.Enabled=!paused;
        samplePreview.Available=sampleConsent!=null;
        samplePreview.Text=T("查看采样进度","Sampling progress");
        pause.Text=paused?T("恢复监控","Resume monitoring"):T("暂停监控","Pause monitoring");
        foreach(ToolStripMenuItem item in language.DropDownItems){
            item.Checked=Equals(item.Tag,options.Language);
            if(Equals(item.Tag,"auto"))item.Text=T("跟随 Windows 显示语言","Windows display language");
            if(Equals(item.Tag,options.UiLanguage))language.Text+=" · "+item.Text;
        }
        foreach(ToolStripMenuItem item in currency.DropDownItems){
            string key=(string)item.Tag!;
            item.Checked=key==options.DisplayCurrency;
            item.Text=key+"   "+Money.Symbol(key);
            item.ToolTipText=key==options.Currency?T("再次选择以恢复自动货币","Select again to restore automatic currency"):"";
        }
        currency.Text+=" · "+options.DisplayCurrency;
        currency.ToolTipText=T("盈亏按此币种显示","Currency used for profit and loss");
    }
    async Task RefreshIpCurrency()
    {
        long request=++regionRequest;
        if(!options.UsesIp)return;
        var code=await IpRegion.LookupCurrency();
        if(request!=regionRequest || !options.UsesIp || menu.IsDisposed)return;
        options=options with {IpCurrency=code};
        TranslateMenu();
    }
    void ChangeOptions(DisplayOptions next)
    {
        try{settingsStore.SaveOptions(next);options=next with {IpCurrency=null};TranslateMenu();_=RefreshIpCurrency();}
        catch(Exception e) when(e is IOException or SqliteException or UnauthorizedAccessException){
            MessageBox.Show(T("设置未能保存，请重试。","Settings could not be saved. Please try again."),"Codex Tracker");
        }
    }
    void EditFromCard(CardAction action)
    {
        var source=shownRow?.Match;
        if(source==null || frame==null || data==null || frame.Window!=target || Native.GetForegroundWindow()!=target ||
            frame.WindowBounds!=Native.Bounds(target) || frame.Dpi!=Native.GetDpiForWindow(target) ||
            Environment.TickCount64-lastFrameReceived>1000 || Environment.TickCount64-frame.Started>2300 || Environment.TickCount64-data.ReadAt>10000 ||
            data.Catalog.Resolve(source.Row.Name)!=source.Id ||
            frame.Rows.Count(row=>row.Name==source.Row.Name)!=1 ||
            !frame.Rows.Any(row=>row.HitVerified && row.Name==source.Row.Name && row.Bounds==source.Row.Bounds))return;
        switch(action){
            case CardAction.Report:OpenReport(source);break;
            case CardAction.Subscription:EditSubscription();break;
            default:EditIncome(action==CardAction.Chat,source);break;
        }
    }
    void OpenReport(BoundRow? source=null){
        if(editingIncome)return;
        bool dark=source==null?FinanceTheme.SystemDark():RowColor(source.Row.Bounds).GetBrightness()<.5f;
        editingIncome=true;HideAll();
        try{
            using var form=new ProfitReportForm(database,root,settingsStore,marketRates,options,dark,source);
            form.ShowDialog();
        }finally{editingIncome=false;}
    }
    void EditIncome(bool chat,BoundRow source)
    {
        if(editingIncome)return;
        editingIncome=true;
        HideAll();
        try{
            string entry=source.Id,name=source.Row.Name;
            if(!chat){
                if(source.Stats.Project.Project is not string project)return;
                var projects=LocalProjects.Read(root);
                if(projects.ByThread.GetValueOrDefault(source.Id)!=project || !projects.Items.TryGetValue(project,out var item))return;
                entry=item.Id;name=item.Name;
            }
            using var form=new IncomeDialog(entry,name,settingsStore,options,chat);
            new FinanceTheme(FinanceTheme.SystemDark(),options.RedGain).Apply(form);
            if(form.ShowDialog()==DialogResult.OK){data=null;discardRead=reading!=null;nextData=0;HideAll();}
        }catch(Exception e) when(e is IOException or SqliteException or System.Text.Json.JsonException or UnauthorizedAccessException or InvalidOperationException){
            MessageBox.Show(T("暂时无法读取收入记录，请稍后重试。","Could not read income records. Please try again."),"Codex Tracker");
        }finally{editingIncome=false;}
    }
    void EditSubscription()
    {
        if(editingIncome)return;
        editingIncome=true;HideAll();
        try{
            QuotaWindow? accountWindow=null;
            try{accountWindow=WeeklyQuota.Read(database,root);}catch(SqliteException){ /* The fee remains editable. */ }
            using var form=new SubscriptionDialog(options,settingsStore.ReadPlan(),accountWindow);
            if(form.ShowDialog()!=DialogResult.OK)return;
            settingsStore.SavePlan(form.Plan);
            data=null;discardRead=reading!=null;nextData=0;HideAll();
        }catch(Exception e) when(e is SqliteException or IOException or UnauthorizedAccessException){
            MessageBox.Show(T("无法读取或保存订阅费用。","Could not read or save the subscription budget."),"Codex Tracker");
        }finally{editingIncome=false;}
    }
    void ToggleLogin()
    {
        try
        {
            using var key=Registry.CurrentUser.CreateSubKey(DesktopUpdates.LoginKey);
            if(login.Checked)key.DeleteValue(DesktopUpdates.LoginValue,false);
            else key.SetValue(DesktopUpdates.LoginValue,DesktopUpdates.LoginCommand);
        }
        catch(Exception){status.Text=T("登录设置未保存","Startup preference could not be saved");}
    }
    internal void SetPaused(bool value)
    {
        if(value==paused)return;
        try
        {
            if(paused)File.Delete(pausedFile);else File.WriteAllText(pausedFile,"paused");
            paused=value;
            if(paused)StopSampling();
            TranslateMenu();
            guard.Enable(false);worker.Stop();data=null;frame=null;invalidatedAt=Environment.TickCount64;nextData=0;HideAll();
        }
        catch(IOException){status.Text=T("暂停设置未保存","Pause preference could not be saved");}
    }
    void HideAll()
    {
        incomeActions.Hide();detail.Hide();shownRow=null;leftRowAt=0;
    }
    bool OverRows(Point point)
    {
        var rows=frame?.Rows;
        return rows is {Count:>0} && Rectangle.Union(rows[0].Bounds,rows[^1].Bounds).Contains(point);
    }
    void Refresh()
    {
        long now=Environment.TickCount64;
        if(paused){status.Text=T("用量记录已暂停","Usage recording paused");icon.Text="Codex Tracker · "+status.Text;return;}
        worker.Tick();
        if(sampleConsent is {} consent && !consent.IsCancellationRequested && consentGeneration==Interlocked.Read(ref sessionGeneration)){
            if(samples.Count<CalibrationSample.MaxPoints*CalibrationSample.PerSize && !sampleReading && now>=nextSampleRead)_=ReadSample(consent);
            if(!sampleSending && samples.FirstOrDefault(s=>s.NeedsRetry && now>=s.RetryAt) is {} next)_=SendNextSample(consent,next);
        }
        if(reading?.IsCompleted==true)
        {
            try{var result=reading.GetAwaiter().GetResult();if(!discardRead)data=result;info=T("等待聊天列表","Waiting for chats");}
            catch(Exception){info=T("等候用量数据","Waiting for usage");}
            reading=null;nextData=discardRead?0:now+3000;discardRead=false;
        }

        if(locating?.IsCompleted==true)
        {
            try{
                var next=locating.GetAwaiter().GetResult();
                if(next.Started>=invalidatedAt && next.Window==target){frame=next;lastFrameReceived=now;}
            }catch(Exception){
                locator=null;locatorEpoch=-1;info=T("正在定位聊天列表","Locating chats");
            }
            locating=null;nextLocate=now+90;
        }
        var active=Native.GetForegroundWindow();
        if(active!=target && (target!=0 || Native.IsCodex(active)))
        {
            target=Native.IsCodex(active)?active:0;
            frame=null;HideAll();nextLocate=0;invalidatedAt=now;
        }
        guard.Enable(target!=0);
        if(Control.MouseButtons.HasFlag(MouseButtons.Left) && OverRows(Cursor.Position)){frame=null;invalidatedAt=now;HideAll();return;}
        if(target!=0 && locating==null && now>=nextLocate)
        {
            var hwnd=target;
            long epoch=invalidatedAt;
            var anchor=shownRow?.Row;
            locating=Task.Run(()=>{
                locator??=new Sidebar();
                if(locatorEpoch!=epoch){locator.Invalidate();locatorEpoch=epoch;}
                return locator.Read(hwnd,prioritizeHover:true,anchor:anchor);
            });
        }
        if(target!=0 && frame?.Window==target && reading==null)
        {
            var titles=frame.Rows.Select(x=>x.Name).Distinct().Order(StringComparer.Ordinal).ToArray();
            if(now>=nextData || !requestedTitles.SequenceEqual(titles))
            {
                requestedTitles=titles;
                reading=Task.Run(()=>{
                    var catalog=catalogCache.Read(root);
                    var ids=titles.Select(catalog.Resolve).OfType<string>().Distinct().ToArray();
                    return SidebarData.Read(root,database,catalog,ids,settingsStore);
                });
            }
        }
        if(editingIncome || target==0 || frame==null || frame.Window!=target || frame.WindowBounds!=Native.Bounds(target) ||
            frame.Dpi!=Native.GetDpiForWindow(target) || Native.IsIconic(target) ||
            now-lastFrameReceived>1000 || now-frame.Started>2300)
            HideAll();
        else
        {
            var fresh=data!=null && now-data.ReadAt<=10000?data:null;
            List<BoundRow> bound=fresh==null?[]:Binding.Rows(frame.Rows,fresh);
            Paint(bound,frame,fresh);
            info=options.Format("已匹配 {0} / {1} 行","{0} / {1} chats matched",bound.Count,frame.Rows.Count);
        }
        if(Environment.GetEnvironmentVariable("CODEX_TRACKER_DIAGNOSTICS")=="1" && now>=nextDiagnostic)
        {
            nextDiagnostic=now+1000;
            Console.WriteLine($"target={target!=0} frame={frame?.Rows.Count} age={now-frame?.Started} data={data?.Stats.Count} detail={detail.Visible} state={info}");
        }
        var activity=worker.Status switch{
            "采集中"=>T("正在后台记录本机用量","Recording local usage"),"等待采集"=>T("正在启动用量记录","Starting usage recording"),
            "已暂停"=>T("用量记录已暂停","Usage recording paused"),_=>T("正在恢复采集","Restarting collector")};
        status.Text=activity;icon.Text="Codex Tracker · "+activity;
    }
    static Color RowColor(Rectangle row)
    {
        var dc=GetDC(0);
        try
        {
            uint c=GetPixel(dc,row.Left+2,row.Top+row.Height/2);
            return c==uint.MaxValue?SystemColors.Window:Color.FromArgb((int)(c&255),(int)((c>>8)&255),(int)((c>>16)&255));
        }
        finally{ReleaseDC(0,dc);}
    }
    CardField Notice(ChatRow row,SidebarFrame frame,DisplayData? fresh)
    {
        string label=T("会话统计","Chat statistics");
        if(fresh==null)return new(label,T("正在读取本机会话记录，请稍候。","Reading local chat records. Please wait."));
        var issue=frame.Rows.Count(item=>item.Name==row.Name)>1?MatchIssue.Ambiguous:fresh.Catalog.Issue(row.Name);
        string message=issue switch{
            MatchIssue.OtherDevice=>T("这条会话归在其他设备，目前没有可显示的本机用量。","This chat belongs to another device. Local usage is unavailable."),
            MatchIssue.Ambiguous=>T("这个标题对应多条记录，暂时无法确定哪条用量属于这次会话。","Several records match this title. The usage for this chat cannot be identified."),
            MatchIssue.PendingTitle=>T("本机目录还未确认这条会话，确认后会自动显示统计。","This chat is awaiting confirmation in the local catalog. Its statistics will appear automatically."),
            _=>T("本机暂未找到这条会话的统计记录。","No matching record for this chat has been found on this PC yet.")
        };
        return new(label,message);
    }
    void Paint(List<BoundRow> matches,SidebarFrame f,DisplayData? fresh)
    {
        var mouse=Cursor.Position;
        var hot=f.Rows.FirstOrDefault(row=>row.Bounds.Contains(mouse));
        if(hot is {HitVerified:false}){nextLocate=0;HideAll();return;}
        if(hot==null && detail.Visible && shownRow is {} previous){
            var anchor=f.Rows.FirstOrDefault(row=>row.HitVerified && row.Name==previous.Row.Name && row.Bounds==previous.Row.Bounds);
            if(anchor!=null){
                if(detail.CardBounds.Contains(mouse) || InCardGap(mouse,anchor.Bounds,detail.CardBounds))hot=anchor;
                else{
                    long now=Environment.TickCount64;
                    if(leftRowAt==0)leftRowAt=now;
                    if(now-leftRowAt<180)return;
                }
            }
        }
        if(hot==null){HideAll();return;}
        leftRowAt=0;
        var work=Screen.FromRectangle(hot.Bounds).WorkingArea;
        bool dark=RowColor(hot.Bounds).GetBrightness()<.5f;
        var match=matches.FirstOrDefault(item=>item.Row==hot);
        shownRow=(hot,match);
        if(match==null){
            incomeActions.Hide();
            detail.PlaceCard(f.Window,hot.Bounds,work,f.Dpi,[Notice(hot,f,fresh)],dark,options.UiLanguage);
            return;
        }
        var buttons=new IncomeButtons(T("填写会话收入","Enter chat income"),
            match.Stats.Project.Project!=null?T("填写项目收入","Enter project income"):null,
            T("近期盈亏","Recent profit and loss"),match.Stats.HasPlan?null:T("输入订阅费用","Enter subscription fee"));
        var footer=detail.PlaceCard(f.Window,hot.Bounds,work,f.Dpi,match.Stats.Fields(options,marketRates),dark,options.UiLanguage,buttons,f.Rows,options.RedGain);
        if(!footer.IsEmpty)incomeActions.Place(detail,f.Window,footer,detail.CardDpi,buttons);
        else incomeActions.Hide();
    }
    static bool InCardGap(Point point,Rectangle row,Rectangle card)
    {
        if(point.Y<Math.Max(row.Top,card.Top) || point.Y>=Math.Min(row.Bottom,card.Bottom))return false;
        return row.Right<=point.X && point.X<card.Left || card.Right<=point.X && point.X<row.Left;
    }
    protected override void Dispose(bool disposing)
    {
        if(disposing)
        {
            SystemEvents.SessionSwitch-=OnSessionSwitch;SystemEvents.SessionEnding-=OnSessionEnding;
            StopSampling();
            updates.Changed-=UpdateChanged;updates.Dispose();
            tick.Stop();tick.Dispose();guard.Dispose();worker.Dispose();icon.Visible=false;icon.Dispose();menu.Dispose();
            trayIcon.Dispose();
            incomeActions.Dispose();detail.Dispose();
        }
        base.Dispose(disposing);
    }
}
