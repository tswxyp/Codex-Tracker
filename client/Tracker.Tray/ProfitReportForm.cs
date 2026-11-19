namespace CodexTracker.Tray;

internal sealed class ProfitReportForm:Form
{
    readonly string db,root;
    readonly SidebarSettings settings;
    readonly MarketRates market;
    readonly DisplayOptions options;
    DisplayOptions reportDisplay;
    readonly FinanceTheme theme;
    readonly ComboBox period=Picker(148),currency=Picker(95);
    readonly DateTimePicker start=DatePicker(),end=DatePicker();
    readonly Label range=Caption(),notice=Caption(),income=Caption(),expense=Caption(),profit=Caption();
    readonly DataGridView table=new(){Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,
        AllowUserToResizeRows=false,RowHeadersVisible=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,
        BorderStyle=BorderStyle.None,CellBorderStyle=DataGridViewCellBorderStyle.SingleHorizontal,SelectionMode=DataGridViewSelectionMode.FullRowSelect,
        MultiSelect=false,EnableHeadersVisualStyles=false,ColumnHeadersBorderStyle=DataGridViewHeaderBorderStyle.None};
    readonly ProfitChart chart;
    readonly System.Windows.Forms.Timer update=new(){Interval=1500};
    readonly SemaphoreSlim reader=new(1);
    readonly QuotaCalibrationCache calibration=new();
    readonly string? threadId;
    ProfitReportData? snapshot;
    DisplayMoney? money;
    bool changing,loading;
    CancellationTokenSource? request;
    DateOnly from,to;
    long refreshAt;
    static ComboBox Picker(int width)=>new(){Width=width,DropDownStyle=ComboBoxStyle.DropDownList,Margin=new Padding(0,3,10,3)};
    static DateTimePicker DatePicker()=>new(){Format=DateTimePickerFormat.Short,Width=134,Visible=false,MaxDate=DateTime.Today};
    static Label Caption()=>new(){AutoSize=true,Anchor=AnchorStyles.Left,Margin=new Padding(2,3,5,3)};
    string T(string zh,string en)=>options.T(zh,en);

    internal ProfitReportForm(string database,string root,SidebarSettings settings,MarketRates market,DisplayOptions options,
        bool dark,BoundRow? source)
    {
        db=database;this.root=root;this.settings=settings;this.market=market;this.options=options;
        reportDisplay=options;
        threadId=source?.Id;
        theme=new(dark,options.RedGain);chart=new(theme,options){Dock=DockStyle.Fill};
        Text=T("近期盈亏","Recent profit and loss");Font=new("Segoe UI",10);
        Icon=Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        AutoScaleMode=AutoScaleMode.Dpi;StartPosition=FormStartPosition.CenterScreen;
        MinimumSize=new(690,535);ClientSize=new(900,670);
        var page=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=8,Padding=new Padding(20,15,20,12)};
        page.ColumnStyles.Add(new(SizeType.Percent,100));
        foreach(var style in new[]{new RowStyle(SizeType.AutoSize),new(SizeType.AutoSize),new(SizeType.AutoSize),
            new(SizeType.AutoSize),new(SizeType.Percent,51),new(SizeType.AutoSize),new(SizeType.Percent,49),new(SizeType.AutoSize)})
            page.RowStyles.Add(style);
        Controls.Add(page);
        var title=Caption();title.Text=Text;title.Font=new(Font.FontFamily,19,FontStyle.Bold);
        var header=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=2};
        header.ColumnStyles.Add(new(SizeType.Percent,100));header.ColumnStyles.Add(new(SizeType.AutoSize));
        header.Controls.Add(title,0,0);header.Controls.Add(currency,1,0);page.Controls.Add(header,0,0);
        var subject=new Label{Dock=DockStyle.Fill,AutoEllipsis=true,Height=Font.Height+5,UseMnemonic=false,
            Text=source?.Row.Name??T("全部本机收支","All local income and costs")};
        header.Controls.Add(subject,0,1);header.SetColumnSpan(subject,2);
        currency.AccessibleName=T("显示货币","Display currency");
        currency.Items.AddRange(Money.Codes);currency.SelectedItem=options.DisplayCurrency;
        var filters=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true,Margin=new Padding(0,8,0,8)};
        period.AccessibleName=T("统计周期","Period");
        period.Items.AddRange([T("近 7 天","Last 7 days"),T("近 30 天","Last 30 days"),T("近 90 天","Last 90 days"),
            T("本月","This month"),T("自定义","Custom")]);
        period.SelectedIndex=0;start.Value=DateTime.Today.AddDays(-6);
        start.AccessibleName=T("开始日期","Start date");end.AccessibleName=T("结束日期","End date");
        filters.Controls.AddRange([period,start,end,range]);page.Controls.Add(filters,0,1);
        var kpis=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=3,Margin=Padding.Empty};
        var tiles=new List<TableLayoutPanel>();
        void Metric(int col,string name,Label value){
            kpis.ColumnStyles.Add(new(SizeType.Percent,100f/3));
            var tile=new TableLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,ColumnCount=1,Padding=new Padding(10,7,8,7),
                Margin=new Padding(0,2,col==2?0:8,5)};
            var caption=Caption();caption.Text=name;
            value.Text="—";value.Font=new(Font.FontFamily,16,FontStyle.Bold);
            tile.Controls.Add(caption,0,0);tile.Controls.Add(value,0,1);kpis.Controls.Add(tile,col,0);tiles.Add(tile);
        }
        Metric(0,T("预估盈亏","Estimated profit / loss"),profit);Metric(1,T("实收收入","Recorded income"),income);
        Metric(2,T("对应成本","Attributed cost"),expense);page.Controls.Add(kpis,0,2);
        Label Heading(string value){
            var label=Caption();label.Text=value;label.Font=new(Font.FontFamily,11,FontStyle.Bold);
            label.Margin=new Padding(2,9,2,4);return label;
        }
        page.Controls.Add(Heading(T("盈亏趋势","Profit / loss trend")),0,3);
        page.Controls.Add(chart,0,4);page.Controls.Add(Heading(T("收支明细","Daily breakdown")),0,5);
        foreach(var heading in new[]{T("日期","Date"),T("实收收入","Recorded income"),T("对应成本","Attributed cost"),T("预估盈亏","Estimated profit / loss")}){
            int i=table.Columns.Add("col"+table.Columns.Count,heading);
            table.Columns[i].SortMode=DataGridViewColumnSortMode.NotSortable;
            if(i>0)table.Columns[i].DefaultCellStyle.Alignment=DataGridViewContentAlignment.MiddleRight;
            table.Columns[i].HeaderCell.Style.Alignment=table.Columns[i].DefaultCellStyle.Alignment;
        }
        page.Controls.Add(table,0,6);page.Controls.Add(notice,0,7);
        theme.Apply(this);
        foreach(var tile in tiles){tile.BackColor=theme.Panel;foreach(Control child in tile.Controls)child.BackColor=theme.Panel;}
        range.ForeColor=notice.ForeColor=subject.ForeColor=theme.Muted;
        table.BackgroundColor=theme.Paper;table.GridColor=theme.Line;
        table.DefaultCellStyle.BackColor=theme.Paper;table.DefaultCellStyle.ForeColor=theme.Ink;
        table.DefaultCellStyle.SelectionBackColor=theme.Panel;table.DefaultCellStyle.SelectionForeColor=theme.Ink;
        table.ColumnHeadersDefaultCellStyle.BackColor=theme.Panel;table.ColumnHeadersDefaultCellStyle.ForeColor=theme.Muted;
        table.ColumnHeadersHeightSizeMode=DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        currency.SelectionChangeCommitted+=(_,_)=>{
            if(currency.SelectedItem is not string code)return;
            reportDisplay=options with {Currency=code};
            theme.RedGain=reportDisplay.RedGain;
            Render();
        };
        period.SelectedIndexChanged+=(_,_)=>ChangeRange();
        start.ValueChanged+=(_,_)=>{if(!changing)ChangeRange(true);};end.ValueChanged+=(_,_)=>{if(!changing)ChangeRange(false);};
        update.Tick+=(_,_)=>{
            if(loading)return;
            if(Environment.TickCount64>=refreshAt){ChangeRange();return;}
            if(money is { } view && (view.AwaitingRate || view.UsedRates!=null) && market.ForConversion()!=view.UsedRates)Render();
        };
        Shown+=(_,_)=>{ChangeRange();update.Start();};
    }

    void ChangeRange(bool startMoved=true){
        changing=true;
        try{
            var today=DateOnly.FromDateTime(DateTime.Today);
            bool custom=period.SelectedIndex==4;
            start.Visible=end.Visible=custom;range.Visible=!custom;
            if(custom && start.Value.Date>end.Value.Date){if(startMoved)end.Value=start.Value;else start.Value=end.Value;}
            to=custom?DateOnly.FromDateTime(end.Value):today;
            from=period.SelectedIndex switch{0=>to.AddDays(-6),1=>to.AddDays(-29),2=>to.AddDays(-89),
                3=>new DateOnly(to.Year,to.Month,1),_=>DateOnly.FromDateTime(start.Value)};
            range.Text=from.ToString("d",options.Culture)+"  –  "+to.ToString("d",options.Culture);
        }finally{changing=false;}
        _=LoadRange();
    }


    async Task LoadRange(){
        request?.Cancel();
        using var job=new CancellationTokenSource();request=job;
        var token=job.Token;
        var first=from;var last=to;
        loading=true;
        if(loadedRange!=(first,last) || snapshot==null){
            snapshot=null;money=null;ClearFigures();notice.Text=T("正在读取收支记录…","Loading financial records…");
        }
        try{
            await reader.WaitAsync(token);
            ProfitReportData result;
            try{
                result=await Task.Run(()=>ProfitReportData.Read(db,root,settings,first,last,calibration,token,threadId),token);
            }finally{reader.Release();}
            if(token.IsCancellationRequested || IsDisposed)return;
            snapshot=result;loadedRange=(first,last);Render();
        }catch(OperationCanceledException) when(token.IsCancellationRequested){ }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or System.Text.Json.JsonException or InvalidOperationException){
            if(!token.IsCancellationRequested && !IsDisposed)notice.Text=T("读取收支记录失败，稍后会自动重试。","Could not load financial records. Retrying shortly.");
        }finally{
            if(request==job){request=null;loading=false;refreshAt=Environment.TickCount64+(snapshot==null?10000:60000);}
        }
    }
    (DateOnly From,DateOnly To)? loadedRange;

    void ClearFigures(){
        table.Rows.Clear();income.Text=expense.Text=profit.Text="—";chart.SetData([],from,to,reportDisplay.DisplayCurrency);
    }
    void Render(){
        if(snapshot==null)return;
        money=new(reportDisplay,market);
        var figures=snapshot.Calculate(from,to,money);
        decimal? receipts=0,charges=0;
        table.SuspendLayout();
        try{
            table.Rows.Clear();
            foreach(var day in figures.Days.Reverse()){
                receipts+=day.Received;charges+=day.Cost;
                var index=table.Rows.Add(day.Date.ToString("d",options.Culture),money.ReportAmount(day.Received),money.ReportAmount(day.Cost),money.ReportAmount(day.Net,signed:true));
                var cell=table.Rows[index].Cells[3];cell.Style.ForeColor=cell.Style.SelectionForeColor=theme.Profit(day.Net);
            }
            table.ClearSelection();
        }finally{table.ResumeLayout();}
        bool empty=figures.Days.Length==0;
        income.Text=money.ReportAmount(empty?null:receipts);expense.Text=money.ReportAmount(empty?null:charges);
        profit.Text=money.ReportAmount(empty?null:receipts-charges,signed:true);profit.ForeColor=theme.Profit(receipts-charges);
        chart.SetData(figures.Days,from,to,reportDisplay.DisplayCurrency);
        var notes=new List<string>();
        if(empty)notes.Add(T("所选周期没有收支记录","No records in this period"));
        if(figures.AssumedStandard)notes.Add(options.SpeedEstimateNote);
        if(figures.Undated>0)notes.Add(options.Format("{0} 笔收入未填写到账日，补上日期后会计入对应期间。","{0} receipts have no payment date. Add a date to include them in the matching period.",figures.Undated));
        if(money.AwaitingRate)notes.Add(T("部分金额等待换算。","Some amounts are awaiting currency conversion."));
        else if(charges==null)notes.Add(options.Format("{0} 天缺少完整成本，相关金额显示 N/A。","Costs are incomplete for {0} day(s). Affected amounts show N/A.",figures.Days.Count(d=>d.Cost==null)));
        notice.Text=notes.Count==0?T("按到账日和本机用量发生日统计","By receipt date and date of local usage"):string.Join("  ·  ",notes);
    }
    protected override void Dispose(bool disposing){
        if(disposing){update.Stop();update.Dispose();request?.Cancel();}
        base.Dispose(disposing);
    }
}
