using System.Diagnostics;
using System.Reflection;

namespace CodexTracker.Tray;

internal enum CommunityPage { About, Consent, Preview }

internal sealed class CommunityDialog:Form
{
    internal const string Repository="https://github.com/tswxyp/Codex-Tracker";
    readonly Label? preview;
    readonly DisplayOptions display;
    internal CommunityDialog(DisplayOptions options,CommunityPage page,CalibrationSample? sample=null,bool unavailable=false)
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
            Line(T("请确保您是单设备任务进行的时候再勾选。","Only opt in while using this account on a single device."));
            Line(T("注：本工具不会收集用户的任何信息、会话内容和隐私内容。","This sampling feature collects no personal information, conversation content or private materials."));
            Line(T("您可以随时使用Codex扫描本项目的代码以确认这一点。","You can ask Codex to inspect the project's source code at any time."));
        }else{
            preview=new(){AutoSize=true,MaximumSize=new Size(460,0),Margin=new Padding(0,4,0,8),UseMnemonic=false};
            Add(preview);UpdatePreview(sample,unavailable);
        }
        if(page!=CommunityPage.About)Line(T("本版仅在本机内存中保留一份样本，暂不上传。取消勾选、锁屏或退出后清空。",
            "This version keeps one sample in memory on this PC. Uploads are off. Unchecking, locking Windows or quitting clears it."));
        var actions=new FlowLayoutPanel{AutoSize=true,FlowDirection=FlowDirection.RightToLeft,Dock=DockStyle.Fill,Margin=new Padding(0,8,0,0)};
        Button Button(string title,DialogResult result){
            var button=new Button{AutoSize=true,Text=title,DialogResult=result,Padding=new Padding(8,2,8,2)};
            actions.Controls.Add(button);return button;
        }
        if(consent){
            var decline=Button(T("暂不采集","Not now"),DialogResult.Cancel);
            Button(T("确认采集","Confirm sampling"),DialogResult.OK);
            AcceptButton=CancelButton=decline;ActiveControl=decline;
        }else AcceptButton=CancelButton=Button(T("关闭","Close"),DialogResult.OK);
        Add(actions);
        var theme=new FinanceTheme(FinanceTheme.SystemDark(),options.RedGain);theme.Apply(this);
        if(link!=null)link.LinkColor=link.ActiveLinkColor=link.VisitedLinkColor=theme.Accent;
    }
    internal void UpdatePreview(CalibrationSample? sample,bool unavailable)
    {
        if(preview==null)return;
        if(sample==null){
            preview.Text=unavailable?display.T("暂时无法读取用量，将自动重试。","Usage is temporarily unavailable. Sampling will retry."):
                display.T("等待单个任务跨过两个相邻的周额度百分点。","Waiting for one task to cross two consecutive percentage marks on the weekly meter.");
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
