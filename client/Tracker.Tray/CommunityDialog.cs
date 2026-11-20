using System.Diagnostics;
using System.Reflection;

namespace CodexTracker.Tray;

internal enum CommunityPage { About, Consent, Preview }

internal sealed class CommunityDialog:Form
{
    internal const string Repository="https://github.com/tswxyp/Codex-Tracker";
    readonly Label? preview;
    readonly Label? uploadStatus;
    readonly TextBox? receipt;
    readonly Button? upload;
    readonly DisplayOptions display;
    bool reportMultitasking;
    internal event Action<bool>? SubmissionRequested;
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
            preview=new(){AutoSize=true,MaximumSize=new Size(460,0),Margin=new Padding(0,4,0,8),UseMnemonic=false};
            Add(preview);
            uploadStatus=new(){AutoSize=true,MaximumSize=new Size(460,0),Margin=new Padding(0,4,0,2)};
            receipt=new(){ReadOnly=true,Width=330,Margin=new Padding(0,0,0,8),
                AccessibleName=T("本次样本编号","Sample receipt number")};
            Add(uploadStatus);Add(receipt);
        }
        if(page!=CommunityPage.About){
            Line(options.Format("点「上传这份样本」才会发送到 {0}。","Use Upload this sample to send it to {0}.",SampleSubmission.Host));
            if(consent)Line(T("仅发送模型、推理强度、速度、Token 计数和本次随机编号。Cloudflare 处理连接时可见 IP，样本库不保存 IP。取消勾选、锁屏或退出会清空本机样本。",
                "Only the model, effort, speed, token counts and a random receipt are sent. Cloudflare sees the connection IP. The sample database does not store it. Unchecking, locking or quitting clears the local sample."));
        }
        var actions=new FlowLayoutPanel{AutoSize=true,FlowDirection=FlowDirection.RightToLeft,Dock=DockStyle.Fill,Margin=new Padding(0,8,0,0)};
        Button Button(string title,DialogResult result){
            var button=new Button{AutoSize=true,Text=title,DialogResult=result,Padding=new Padding(8,2,8,2)};
            actions.Controls.Add(button);return button;
        }
        if(consent){
            var decline=Button(T("暂不采集","Not now"),DialogResult.Cancel);
            Button(T("确认采集","Confirm sampling"),DialogResult.OK);
            AcceptButton=CancelButton=decline;
            Shown+=(_,_)=>decline.Focus();
        }else{
            AcceptButton=CancelButton=Button(T("关闭","Close"),DialogResult.OK);
            if(page==CommunityPage.Preview){
                upload=Button(T("上传这份样本","Upload this sample"),DialogResult.None);
                upload.Click+=(_,_)=>{
                    if(reportMultitasking && MessageBox.Show(this,T("将这份样本标记为多任务，并从校准中排除？",
                        "Mark this sample as multitasking and exclude it from calibration?"),Text,
                        MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;
                    SubmissionRequested?.Invoke(reportMultitasking);
                };
            }
        }
        Add(actions);
        var theme=new FinanceTheme(FinanceTheme.SystemDark(),options.RedGain);theme.Apply(this);
        if(link!=null)link.LinkColor=link.ActiveLinkColor=link.VisitedLinkColor=theme.Accent;
    }
    internal void UpdatePreview(CalibrationSample? sample,bool unavailable,SampleSubmission? submission)
    {
        if(preview==null || uploadStatus==null || receipt==null || upload==null)return;
        reportMultitasking=submission?.State is UploadState.Stored or UploadState.Reporting or UploadState.ReportUnconfirmed;
        upload.Enabled=submission?.State is UploadState.Ready or UploadState.Unconfirmed or UploadState.CheckUnavailable or UploadState.Stored or UploadState.ReportUnconfirmed;
        upload.Text=reportMultitasking?display.T("标记为多任务并排除","Report multitasking and exclude"):
            submission?.State is UploadState.Unconfirmed or UploadState.CheckUnavailable?display.T("重试上传","Retry upload"):display.T("上传这份样本","Upload this sample");
        uploadStatus.Visible=receipt.Visible=submission!=null;
        receipt.Text=submission?.Receipt.ToString("D")??"";
        uploadStatus.Text=submission?.State switch{
            UploadState.Ready=>display.T("本次样本编号","Sample receipt number"),
            UploadState.Checking=>display.T("正在复核本次用量并提交…","Checking and submitting this sample…"),
            UploadState.CheckUnavailable=>display.T("暂时无法复核本机用量，可稍后重试。","Local usage could not be checked. Try again later."),
            UploadState.Ineligible=>display.T("本机任务或用量记录已变化，这份样本已停止提交。重新勾选可再采一份。","Local activity or usage changed. Submission stopped. Opt in again to collect a new sample."),
            UploadState.Sending=>display.T("正在等待服务器回执…","Waiting for the server receipt…"),
            UploadState.Stored when submission.Review=="pending"=>display.T("已上传 · 待审核，尚未纳入校准","Uploaded · Awaiting review, excluded from calibration for now"),
            UploadState.Stored when submission.Review=="approved"=>display.T("已上传 · 已通过校准样本审核","Uploaded · Approved for calibration"),
            UploadState.Stored=>display.T("已上传 · 服务器回执号","Uploaded · Server receipt"),
            UploadState.Reporting=>display.T("正在标记多任务样本…","Reporting multitasking…"),
            UploadState.ReportUnconfirmed=>display.T("样本已上传，尚未确认排除。可重试标记。","Sample uploaded. Exclusion is unconfirmed. Retry reporting."),
            UploadState.Excluded=>display.T("已排除 · 不纳入校准","Excluded from calibration"),
            UploadState.Unconfirmed=>display.T("尚未确认收到。可用同一编号重试。","Delivery unconfirmed. Retry with the same receipt number."),
            UploadState.Rejected=>display.T("服务器拒收了这份样本。","The server rejected this sample."),
            _=>""
        };
        if(sample==null){
            preview.Text=unavailable?display.T("暂时无法读取用量，将自动重试。","Usage is temporarily unavailable. Sampling will retry."):
                display.T("等待一个会话跨过两个相邻的周额度百分点。期间检测到其他任务，会跳过这段用量。","Waiting for one chat to cross two consecutive weekly quota marks. Intervals with other task activity are skipped.");
            return;
        }
        string Unknown(string value)=>value=="unknown"?display.T("未记录","Not recorded"):value;
        preview.Text=string.Join(Environment.NewLine,display.T("周额度变化约 1 个百分点","About one percentage point of weekly quota"),
            display.Format("模型 {0}　·　推理强度 {1}　·　速度 {2}","Model {0} · Effort {1} · Speed {2}",sample.Model,Unknown(sample.Effort),Unknown(sample.Speed)),
            display.Format("输入 {0:N0}　·　输出 {1:N0}","Input {0:N0} · Output {1:N0}",sample.Input,sample.Output),
            display.Format("缓存读取 {0:N0}　·　缓存写入 {1:N0}","Cache read {0:N0} · Cache write {1:N0}",sample.CacheRead,sample.CacheWrite),
            display.Format("输出中包含推理 Token {0:N0}","Reasoning tokens included in output: {0:N0}",sample.Reasoning));
    }
}
