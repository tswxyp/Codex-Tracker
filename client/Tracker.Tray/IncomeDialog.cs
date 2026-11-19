using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CodexTracker.Tray;

internal sealed class IncomeDialog:Form
{
    internal IncomeDialog(string entry,string name,SidebarSettings store,DisplayOptions options,bool chat)
    {
        string T(string zh,string en)=>options.T(zh,en);
        Text=chat?T("本会话收入","Chat income"):T("本项目收入","Project income");
        Font=new Font("Segoe UI",10);
        AutoScaleMode=AutoScaleMode.Font;
        AutoSize=true;AutoSizeMode=AutoSizeMode.GrowAndShrink;
        FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;
        StartPosition=FormStartPosition.CenterScreen;
        var layout=new TableLayoutPanel{AutoSize=true,Dock=DockStyle.Fill,ColumnCount=2,Padding=new Padding(16)};
        layout.ColumnStyles.Add(new(SizeType.Absolute,155));layout.ColumnStyles.Add(new(SizeType.Absolute,275));
        Controls.Add(layout);
        void AddField(int row,string label,Control control){
            layout.Controls.Add(new Label{Text=label,AutoSize=true,Anchor=AnchorStyles.Left},0,row);
            control.Margin=new Padding(3,5,3,5);control.Dock=DockStyle.Fill;layout.Controls.Add(control,1,row);
        }
        var amount=new TextBox{PlaceholderText=T("例如 1200.00","e.g. 1200.00")};
        var currency=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList};
        var currencies=Money.Codes;
        currency.Items.AddRange(currencies);
        int row=0;
        string targetLabel=chat?T("Codex 会话","Codex chat"):T("Codex 项目","Codex project");
        AddField(row++,targetLabel,new Label{Text=name,UseMnemonic=false,AutoSize=true,MaximumSize=new Size(267,0)});
        var records=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList};
        var date=new DateTimePicker{Format=DateTimePickerFormat.Custom,ShowCheckBox=true,MaxDate=DateTime.Today};
        AddField(row++,T("收入记录","Receipt"),records);
        AddField(row++,T("本笔实收","Receipt amount"),amount);
        AddField(row++,T("收入币种","Income currency"),currency);
        AddField(row++,T("到账日","Received on"),date);
        var dateHelp=new Label{AutoSize=true,MaximumSize=new Size(420,0),Margin=new Padding(3,3,3,3),
            Text=T("只填金额会计入累计收入。勾选实际到账日期并保存，这笔钱就会出现在那一天的收支明细里。",
                "The amount counts toward total income. Select the payment date and save to include it in that day’s report.")};
        layout.Controls.Add(dateHelp,0,row++);layout.SetColumnSpan(dateHelp,2);
        void RefreshDate(){
            date.CustomFormat=date.Checked?options.Culture.DateTimeFormat.ShortDatePattern:"'"+T("未填写","Not entered")+"'";
            dateHelp.Visible=!date.Checked;
        }
        date.ValueChanged+=(_,_)=>RefreshDate();
        var help=new Label{AutoSize=true,MaximumSize=new Size(420,0),Margin=new Padding(3,10,3,10),
            Text=T("可分笔登记。清空金额可删除选中的记录。","Record each payment separately. Clear the amount to delete the selected receipt.")+
                (chat?"":" "+T("项目有实收记录时，项目汇总优先使用这些记录。","Project receipts take precedence over chat receipts in project totals."))};
        layout.Controls.Add(help,0,row++);layout.SetColumnSpan(help,2);
        var buttons=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,WrapContents=false};
        var save=new Button{Text=T("保存","Save"),AutoSize=true};
        var cancel=new Button{Text=T("取消","Cancel"),AutoSize=true,DialogResult=DialogResult.Cancel};
        buttons.Controls.Add(save);buttons.Controls.Add(cancel);layout.Controls.Add(buttons,0,row);layout.SetColumnSpan(buttons,2);
        AcceptButton=save;CancelButton=cancel;
        string scope=chat?"chat":"project";
        var available=store.ReadReceipts((scope,entry)).OrderByDescending(r=>r.Date).ThenBy(r=>r.Id).ToArray();
        IncomeReceipt? Current()=>records.SelectedIndex>0?available[records.SelectedIndex-1]:null;
        records.SelectedIndexChanged+=(_,_)=>{
            var item=Current();
            amount.Text=item==null?"":item.Amount.NativeAmount.ToString("0.##",options.Culture);
            currency.SelectedItem=item?.Amount.Currency??options.DisplayCurrency;
            date.Value=item?.Date?.ToDateTime(TimeOnly.MinValue)??DateTime.Today;
            date.Checked=item==null || item.Date.HasValue;
            RefreshDate();
        };
        records.Items.Add(T("新增一笔收入","New receipt"));
        foreach(var item in available)records.Items.Add(options.Amount(item.Amount.MoneyValue)+" · "+
            (item.Date?.ToString("d",options.Culture)??T("未填写到账日","No payment date")));
        records.SelectedIndex=available.Length==1 && available[0].Date==null?1:0;
        Shown+=(_,_)=>{amount.Focus();amount.SelectAll();};
        save.Click+=(_,_)=>{
            string text=amount.Text.Trim();
            var code=currencies[currency.SelectedIndex];
            int scale=Money.Scale(code);
            decimal value=0m;
            if(text.Length>0 && ((!decimal.TryParse(text,NumberStyles.AllowDecimalPoint,options.Culture,out value) &&
                !decimal.TryParse(text,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out value)) ||
                value<0 || value>long.MaxValue/(decimal)scale || decimal.Truncate(value*scale)!=value*scale)){
                MessageBox.Show(this,T("请输入该币种支持的非负金额。","Enter a nonnegative amount valid for this currency."),Text);
                amount.Focus();return;
            }
            try{
                var old=Current();
                if(text.Length>0 || old!=null){
                    IncomeReceipt? receipt=text.Length==0?null:new(old?.Id??Guid.NewGuid().ToString("N"),scope,entry,
                        new((long)(value*scale),code),date.Checked?DateOnly.FromDateTime(date.Value):null);
                    store.SaveReceipt(old,receipt);
                }
                DialogResult=DialogResult.OK;
            }catch(Exception e) when(e is SqliteException or IOException or UnauthorizedAccessException){
                MessageBox.Show(this,T("保存失败。请检查本机存储权限，然后重试。","Could not save. Check local storage access and try again."),Text);
            }
        };
    }
}
