namespace CodexTracker.Tray;

internal sealed class SubscriptionDialog:Form
{
    readonly NumericUpDown fee=new(){Maximum=1_000_000_000,Width=135};
    readonly int estimateDays;
    readonly ComboBox currencies=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=135};
    string Code=>(string)currencies.SelectedItem!;
    internal SubscriptionPlan Plan=>new(new((long)decimal.Round(fee.Value*Money.Scale(Code),0,MidpointRounding.AwayFromZero),Code),estimateDays);
    internal SubscriptionDialog(DisplayOptions options,SubscriptionPlan? saved,QuotaWindow? account)
    {
        string T(string zh,string en)=>options.T(zh,en);
        Text=T("输入订阅费用","Enter subscription fee");AutoScaleMode=AutoScaleMode.Font;
        Font=new Font("Segoe UI",10);StartPosition=FormStartPosition.CenterScreen;
        FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;
        AutoSize=true;AutoSizeMode=AutoSizeMode.GrowAndShrink;
        var layout=new TableLayoutPanel{Padding=new Padding(21),ColumnCount=2,AutoSize=true,Dock=DockStyle.Fill};
        layout.ColumnStyles.Add(new(SizeType.AutoSize));layout.ColumnStyles.Add(new(SizeType.AutoSize));
        Controls.Add(layout);
        var title=new Label{Text=Text,Font=new Font(Font.FontFamily,17,FontStyle.Bold),AutoSize=true,Margin=new Padding(1,0,4,13)};
        layout.Controls.Add(title,0,0);layout.SetColumnSpan(title,2);
        Label Add(string text,Control control,int row){
            var label=new Label{Text=text,AutoSize=true,Anchor=AnchorStyles.Left,Margin=new Padding(3,6,15,6)};
            layout.Controls.Add(label,0,row);layout.Controls.Add(control,1,row);return label;
        }
        Add(T("订阅币种","Subscription currency"),currencies,1);
        Add(T("每月实付","Monthly payment"),fee,2);
        estimateDays=saved?.EstimateDays??30;
        Add(T("计费周期","Billing cycle"),new Label{AutoSize=true,Anchor=AnchorStyles.Left,
            Text=options.Format("每月（按 {0} 天估算）","Monthly ({0} days for estimates)",estimateDays)},3);
        currencies.Items.AddRange(Money.Codes);
        currencies.SelectedItem=saved?.Paid.Currency??options.DisplayCurrency;
        fee.DecimalPlaces=Money.Scale(Code)==1?0:2;
        fee.Value=saved?.Paid.NativeAmount??0;
        var budget=new TableLayoutPanel{ColumnCount=1,AutoSize=true,Dock=DockStyle.Fill,Padding=new Padding(10,7,10,7),
            Margin=new Padding(2,10,2,6)};
        var budgetLabel=new Label{AutoSize=true,Text=T("七天预算","Seven-day budget")};
        var amount=new Label{AutoSize=true,Font=new Font(Font.FontFamily,16,FontStyle.Bold)};
        budget.Controls.Add(budgetLabel);budget.Controls.Add(amount);
        layout.Controls.Add(budget,0,4);layout.SetColumnSpan(budget,2);
        if(account!=null){
            var at=DateTimeOffset.FromUnixTimeMilliseconds(account.Seen).ToLocalTime().ToString("g",options.Culture);
            var note=new Label{AutoSize=true,MaximumSize=new Size(420,0),Margin=new Padding(2,2,2,8),
                Text=options.Format("账号已用 {0:0.##}% · 剩余 {1:0.##}%（截至 {2}）",
                    "Account used {0:0.##}% · remaining {1:0.##}% (as of {2})",account.Percent,100-account.Percent,at)};
            layout.Controls.Add(note,0,5);layout.SetColumnSpan(note,2);
        }
        var actions=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft};
        var save=new Button{Text=T("保存","Save"),AutoSize=true,DialogResult=DialogResult.OK};
        var cancel=new Button{Text=T("取消","Cancel"),AutoSize=true,DialogResult=DialogResult.Cancel};
        actions.Controls.Add(save);actions.Controls.Add(cancel);layout.Controls.Add(actions,0,6);layout.SetColumnSpan(actions,2);
        AcceptButton=save;CancelButton=cancel;
        var theme=new FinanceTheme(FinanceTheme.SystemDark(),options.RedGain);
        theme.Apply(this);
        budget.BackColor=budgetLabel.BackColor=amount.BackColor=theme.Panel;
        amount.ForeColor=theme.Accent;budgetLabel.ForeColor=theme.Muted;

        void RefreshBudget(){
            save.Enabled=Plan.Paid.Minor>0;
            amount.Text=save.Enabled?options.Approx(Plan.WeeklyBudget):"—";
        }
        currencies.SelectedIndexChanged+=(_,_)=>{
            fee.DecimalPlaces=Money.Scale(Code)==1?0:2;
            fee.Value=decimal.Round(fee.Value,fee.DecimalPlaces);
            RefreshBudget();
        };
        fee.ValueChanged+=(_,_)=>RefreshBudget();
        RefreshBudget();
    }
}
