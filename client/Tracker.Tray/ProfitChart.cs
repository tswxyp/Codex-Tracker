using System.Drawing.Drawing2D;
using System.Globalization;

namespace CodexTracker.Tray;

internal sealed class ProfitChart:Control
{
    sealed record Bin(DateOnly Start,DateOnly End,decimal? Net,bool Recorded);
    readonly FinanceTheme theme;
    readonly DisplayOptions options;
    readonly ToolTip tip=new();
    Bin[] bins=[];
    string currency="";
    int hot=-1;
    Rectangle plot;
    internal ProfitChart(FinanceTheme theme,DisplayOptions options){
        this.theme=theme;this.options=options;DoubleBuffered=true;ResizeRedraw=true;
        AccessibleRole=AccessibleRole.Chart;Margin=new Padding(0,2,0,4);
    }
    internal void SetData(DailyFigures[] days,DateOnly from,DateOnly to,string code){
        hot=-1;tip.SetToolTip(this,null);currency=code;
        int count=to.DayNumber-from.DayNumber+1;
        if(days.Length==0 || count<1){bins=[];Invalidate();return;}
        int step=count<=31?1:Math.Max(7,(count+29)/30);
        var groups=days.ToLookup(d=>(d.Date.DayNumber-from.DayNumber)/step);
        bins=new Bin[(count+step-1)/step];
        for(int i=0;i<bins.Length;i++){
            var start=from.AddDays(i*step);
            decimal? total=0;bool recorded=false;
            foreach(var d in groups[i]){total+=d.Net;recorded=true;}
            bins[i]=new(start,from.AddDays(Math.Min(count-1,(i+1)*step-1)),total,recorded);
        }
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e){
        base.OnPaint(e);
        var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;g.Clear(theme.Paper);
        int P(int value)=>LogicalToDeviceUnits(value);
        using var grid=new Pen(theme.Line){DashStyle=DashStyle.Dash};
        using var axis=new Pen(theme.Muted);
        using var muted=new SolidBrush(theme.Muted);
        using var labelRight=new StringFormat{Alignment=StringAlignment.Far,LineAlignment=StringAlignment.Center};
        using var labelCenter=new StringFormat{Alignment=StringAlignment.Center};
        if(bins.Length==0){
            using var empty=new StringFormat{Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center};
            g.DrawString("—",Font,muted,ClientRectangle,empty);
            return;
        }
        decimal top=Math.Max(0,bins.Max(b=>b.Net??0)),bottom=Math.Min(0,bins.Min(b=>b.Net??0));
        if(top==bottom){top=1;bottom=-1;}
        string Number(decimal n)=>n.ToString("N"+(Math.Max(top,-bottom)<2 && Money.Scale(currency)>1?"2":"0"),options.Culture);
        int left=(int)Math.Ceiling(Math.Max(g.MeasureString(Number(top),Font).Width,g.MeasureString(Number(bottom),Font).Width))+P(8);
        plot=Rectangle.FromLTRB(left,P(16),Width-P(14),Height-P(28));
        if(plot.Width<8 || plot.Height<8)return;
        float Y(decimal amount)=>plot.Bottom-(float)((amount-bottom)/(top-bottom))*plot.Height;
        g.DrawString(currency,Font,muted,1,0);
        foreach(decimal mark in new[]{top,bottom,0,(top+bottom)/2}.Distinct()){
            float y=Y(mark);g.DrawLine(mark==0?axis:grid,plot.Left,y,plot.Right,y);
            if(mark==0 || Math.Abs(Y(0)-y)>Font.Height)g.DrawString(Number(mark),Font,muted,new RectangleF(0,y-Font.Height,left-P(5),Font.Height*2),labelRight);
        }
        float slot=plot.Width/(float)bins.Length;
        int every=Math.Max(1,(int)Math.Ceiling(P(52)/slot));float baseline=Y(0);
        for(int i=0;i<bins.Length;i++){
            var b=bins[i];float x=plot.Left+(i+.5f)*slot;
            if(hot==i){using var hover=new SolidBrush(Color.FromArgb(24,theme.Accent));g.FillRectangle(hover,x-slot/2,plot.Top,slot,plot.Height);}
            if(b.Recorded && b.Net is decimal value && value!=0){
                float width=Math.Max(1,Math.Min(P(38),slot*.60f)),y=Y(value);
                using var brush=new SolidBrush(theme.Profit(value));
                g.FillRectangle(brush,x-width/2,Math.Min(y,baseline),width,Math.Max(1,Math.Abs(y-baseline)));
            }else if(b.Recorded && b.Net==null){g.DrawString("·",Font,muted,x,baseline-Font.Height/2,labelCenter);}
            if(i%every==0)g.DrawString(b.Start.ToString("M/d",options.Culture),Font,muted,x,plot.Bottom+P(3),labelCenter);
        }
    }
    protected override void OnMouseMove(MouseEventArgs e){
        base.OnMouseMove(e);
        int index=plot.Contains(e.Location) && bins.Length>0?Math.Min(bins.Length-1,(e.X-plot.Left)*bins.Length/Math.Max(1,plot.Width)):-1;
        if(index==hot)return;
        hot=index;Invalidate();
        if(hot<0){tip.SetToolTip(this,null);return;}
        var b=bins[hot];string date=b.Start.ToString("d",options.Culture);
        if(b.Start!=b.End)date+=" – "+b.End.ToString("d",options.Culture);
        var money=new DisplayMoney(options with {Currency=currency},null);
        tip.SetToolTip(this,date+"   "+money.ReportAmount(b.Recorded?b.Net:null,signed:true));
    }
    protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);hot=-1;Invalidate();}
    protected override void Dispose(bool disposing){if(disposing)tip.Dispose();base.Dispose(disposing);}
}
