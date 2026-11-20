using System.Diagnostics;
using System.Reflection;

namespace CodexTracker.Tray;

internal enum CommunityPage { About, Consent, Preview }

internal sealed class CommunityDialog:Form
{
    internal const string Repository="https://github.com/tswxyp/Codex-Tracker";
    readonly Label? preview,progress;
    readonly ComboBox? sampleList;
    readonly Label? uploadStatus;
    readonly TextBox? receipt;
    readonly Button? exclude;
    readonly DisplayOptions display;
    internal event Action<SampleSubmission>? ExclusionRequested;
    sealed record SampleChoice(SampleSubmission Item,string Title){public override string ToString()=>Title;}
    internal CommunityDialog(DisplayOptions options,CommunityPage page)
    {
        display=options;
        string T(string zh,string en)=>options.T(zh,en);
        bool consent=page==CommunityPage.Consent;
        Text=page switch{
            CommunityPage.About=>T("关于 Codex Tracker","About Codex Tracker"),
            CommunityPage.Consent=>T("自愿参与用量采样","Contribute a usage sample"),
            _=>T("本次用量样本","Current usage sample")
        };
        AutoScaleMode=AutoScaleMode.Dpi;StartPosition=FormStartPosition.CenterScreen;
        FormBorderStyle=FormBorderStyle.FixedDialog;MinimizeBox=MaximizeBox=false;AutoSize=true;AutoSizeMode=AutoSizeMode.GrowAndShrink;
        Font=new Font("Segoe UI",10);Icon=Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        var body=new TableLayoutPanel{AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,ColumnCount=1,
            Dock=DockStyle.Fill,Padding=new Padding(22,16,22,16)};
        Controls.Add(body);
        void Add(Control item){int row=body.RowCount++;body.RowStyles.Add(new(SizeType.AutoSize));body.Controls.Add(item,0,row);}
        void Line(string value,bool heading=false){
            var label=new Label{Text=value,AutoSize=true,MaximumSize=new Size(460,0),Margin=new Padding(0,4,0,6),UseMnemonic=false};
            if(heading)label.Font=new Font(Font.FontFamily,14,FontStyle.Bold);
            Add(label);
        }
        LinkLabel? link=null;
        if(page==CommunityPage.About){
            Line(T("感谢使用 Codex Tracker 工具！","Thank you for using Codex Tracker!"),true);
            string version=typeof(TrayHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
            Line(options.Format("当前版本：{0}","Current version: {0}",version));
            string text=T("如果您认为这个工具还凑合，请在 GitHub 上给我一个⭐️吧！","If this tool works for you, give it a ⭐️ on GitHub!");
            link=new LinkLabel{Text=text,AutoSize=true,MaximumSize=new Size(460,0),Margin=new Padding(0,4,0,8),UseMnemonic=false};
            link.Links.Clear();link.Links.Add(text.IndexOf("GitHub",StringComparison.Ordinal),6,Repository);
            link.LinkClicked+=(_,_)=>{
                try{Process.Start(new ProcessStartInfo(Repository){UseShellExecute=true});}
                catch(System.ComponentModel.Win32Exception){MessageBox.Show(this,Repository,T("无法打开系统浏览器","Could not open browser"));}
            };
            Add(link);
        }else if(consent){
            Line(T("感谢您帮助采集并上传您用量和token情况，这有助于更精确地核算每个任务所消耗的真实货币成本。",
                "Thank you for contributing usage and token samples. They help estimate the subscription cost of individual tasks."));
            Line(T("采样期间，请只用一台设备，并且只在一个 Codex 会话里运行任务。其他会话可以开着，请等采样结束后再运行其他任务或子代理。",
                "While sampling, use one device and run tasks in only one Codex chat. Other chats can stay open. Wait until sampling finishes before running another task or subagent."));
            Line(T("注：本工具不会收集用户的任何信息、会话内容和隐私内容。","This sampling feature collects no personal information, conversation content or private materials."));
            Line(T("您可以随时使用Codex扫描本项目的代码以确认这一点。","You can ask Codex to inspect the project's source code at any time."));
        }else{
            progress=new(){AutoSize=true,MaximumSize=new Size(460,0),Margin=new Padding(0,4,0,8)};
            Add(progress);
            sampleList=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=330,
                AccessibleName=T("本次用量样本","Current usage sample")};
            sampleList.SelectedIndexChanged+=(_,_)=>ShowSelection();
            Add(sampleList);
            preview=new(){AutoSize=true,MaximumSize=new Size(460,0),Margin=new Padding(0,4,0,8),UseMnemonic=false};
            Add(preview);
            uploadStatus=new(){AutoSize=true,MaximumSize=new Size(460,0),Margin=new Padding(0,4,0,2)};
            receipt=new(){ReadOnly=true,Width=330,Margin=new Padding(0,0,0,8),
                AccessibleName=T("本次样本编号","Sample receipt number")};
            Add(uploadStatus);Add(receipt);
        }
        if(consent){
            Line(T("同意后，周额度每使用1%、2%、3%时，符合条件的样本（单设备单任务）会自动上传至Cloudflare云托管，每档自动上传2份样本，各取不同时段的样本进行记录。",
                "After you agree, qualifying samples from one task on one device are uploaded automatically to Cloudflare for intervals that use 1%, 2% or 3% of your weekly quota. Each group uses two samples from separate intervals."));
            Line(T("可能会采集您的会话ID和对应会话所使用的token，绝不会采集会话名称以及会话内容。",
                "Sampling may read your chat ID and the number of tokens used by that chat on this device. Chat titles and content are never collected."));
            Line(T("取消勾选、暂停监控、锁屏或退出立即停止上传并清空本机样本（不会影响会话内容和任务记录等），下次登录需要重新同意。",
                "Unchecking this option, pausing monitoring, locking your screen or quitting immediately stops uploads and clears samples on this device. Your chats and task history are preserved. You’ll need to opt in again the next time you sign in."));
            Line(T("您可以选择多次采集并上传样本，感谢您的贡献！:D",
                "You can opt in again to collect and upload more samples. Thanks for contributing! :D"));
        }
        var actions=new FlowLayoutPanel{AutoSize=true,FlowDirection=FlowDirection.RightToLeft,Dock=DockStyle.Fill,Margin=new Padding(0,8,0,0)};
        Button Button(string title,DialogResult result){
            var button=new Button{AutoSize=true,Text=title,DialogResult=result,Padding=new Padding(8,2,8,2)};
            actions.Controls.Add(button);return button;
        }
        if(consent){
            var decline=Button(T("暂不采集","Not now"),DialogResult.Cancel);
            Button(T("同意并自动上传","Agree and upload automatically"),DialogResult.OK);
            AcceptButton=CancelButton=decline;
            Shown+=(_,_)=>decline.Focus();
        }else{
            AcceptButton=CancelButton=Button(T("关闭","Close"),DialogResult.OK);
            if(page==CommunityPage.Preview){
                exclude=Button(T("标记为多任务并排除","Report multitasking and exclude"),DialogResult.None);
                exclude.Click+=(_,_)=>{
                    if(sampleList?.SelectedItem is not SampleChoice choice || MessageBox.Show(this,T("将这份样本标记为多任务，并从校准中排除？",
                        "Mark this sample as multitasking and exclude it from calibration?"),Text,
                        MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;
                    ExclusionRequested?.Invoke(choice.Item);
                };
            }
        }
        Add(actions);
        var theme=new FinanceTheme(FinanceTheme.SystemDark(),options.RedGain);theme.Apply(this);
        if(link!=null)link.LinkColor=link.ActiveLinkColor=link.VisitedLinkColor=theme.Accent;
    }
    internal void UpdatePreview(IReadOnlyList<SampleSubmission> samples,bool unavailable)
    {
        if(progress==null || sampleList==null)return;
        int Confirmed(int points)=>samples.Count(s=>s.Sample.Window.Points==points && s.Review!=null);
        int one=Confirmed(1),two=Confirmed(2),three=Confirmed(3);
        string counts=display.Format("已上传　1%：{0}/2　　2%：{1}/2　　3%：{2}/2",
            "Uploaded   1%: {0}/2     2%: {1}/2     3%: {2}/2",one,two,three);
        string status=one+two+three==CalibrationSample.MaxPoints*CalibrationSample.PerSize?
            display.T("本次采样已完成","Sampling complete"):
            unavailable?display.T("暂时无法读取用量，将自动重试。","Usage is temporarily unavailable. Sampling will retry."):
            display.T("等待单会话额度变化，合格样本将自动上传。","Waiting for single-chat quota changes. Eligible samples upload automatically.");
        progress.Text=counts+Environment.NewLine+status;
        var previous=(sampleList.SelectedItem as SampleChoice)?.Item;
        if(!sampleList.Items.Cast<SampleChoice>().Select(x=>x.Item).SequenceEqual(samples)){
            sampleList.BeginUpdate();
            try{
                sampleList.Items.Clear();
                foreach(var item in samples){
                    var choice=new SampleChoice(item,$"{item.Sample.Window.Points}%  ·  {item.Sample.Model}  ·  {item.Receipt.ToString("N")[..8]}");
                    sampleList.Items.Add(choice);
                    if(item==previous)sampleList.SelectedItem=choice;
                }
                if(sampleList.SelectedIndex<0 && samples.Count>0)sampleList.SelectedIndex=samples.Count-1;
            }finally{sampleList.EndUpdate();}
        }
        sampleList.Visible=samples.Count>0;
        ShowSelection();
    }

    void ShowSelection()
    {
        if(preview==null || uploadStatus==null || receipt==null || exclude==null)return;
        var submission=(sampleList?.SelectedItem as SampleChoice)?.Item;
        preview.Visible=uploadStatus.Visible=receipt.Visible=submission!=null;
        exclude.Visible=submission?.State==UploadState.Stored;
        if(submission==null)return;
        receipt.Text=submission.Receipt.ToString("D");
        uploadStatus.Text=submission.State switch{
            UploadState.Stored=>display.T("已上传 · 云端核对通过","Uploaded · Passed server checks"),
            UploadState.Excluded when submission.Review=="excluded_uncertain"=>display.T("已上传 · 用量、任务归属或模型档位未能核实，已排除","Uploaded · Excluded because usage, attribution or model settings could not be verified"),
            UploadState.Excluded=>display.T("已排除 · 不纳入校准","Excluded from calibration"),
            UploadState.Unconfirmed or UploadState.CheckUnavailable or UploadState.ReportUnconfirmed=>
                display.T("等待自动重试","Waiting for automatic retry"),
            UploadState.Reporting=>display.T("正在提交排除请求…","Submitting exclusion…"),
            UploadState.Sending=>display.T("正在等待服务器回执…","Waiting for the server receipt…"),
            _=>display.T("正在复核本次用量并提交…","Checking and submitting this sample…")
        };
        var sample=submission.Sample;
        string Unknown(string value)=>value=="unknown"?display.T("未记录","Not recorded"):value;
        string Detected(bool value)=>value?display.T("已发现","Detected"):display.T("未发现","Not detected");
        preview.Text=string.Join(Environment.NewLine,
            display.Format("本机运行任务的会话数：{0}","Active chats on this device: {0}",sample.ActiveSessions),
            display.Format("任务重叠：{0}　·　无法归属的用量：{1}","Task overlap: {0} · Unattributed usage: {1}",
                Detected(sample.TaskOverlap),Detected(sample.UnattributedUsage)),
            display.Format("模型 {0}　·　推理强度 {1}　·　速度 {2}","Model {0} · Effort {1} · Speed {2}",sample.Model,Unknown(sample.Effort),Unknown(sample.Speed)),
            display.Format("输入 {0:N0}　·　输出 {1:N0}","Input {0:N0} · Output {1:N0}",sample.Input,sample.Output),
            display.Format("缓存读取 {0:N0}　·　缓存写入 {1:N0}","Cache read {0:N0} · Cache write {1:N0}",sample.CacheRead,sample.CacheWrite),
            display.Format("输出中包含推理 Token {0:N0}","Reasoning tokens included in output: {0:N0}",sample.Reasoning),
            display.Format("累计计数增量：{0}","Counter change: {0}",sample.SnapshotTokens?.ToString("N0",display.Culture)??display.T("未记录","Not recorded")));
    }
}
