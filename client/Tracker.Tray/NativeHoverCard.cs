using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace CodexTracker.Tray;

// The A popover. Text, pills, dividers and hit targets are measured in physical pixels
// for the monitor that contains the Codex row. Only the finished frame reaches the HWND.
internal sealed class NativeHoverCard:IDisposable
{
    sealed record Run(string Text,Font Font,Color Color,RectangleF Box,bool Right);
    readonly List<Run> runs=[];
    readonly List<RectangleF> tags=[];
    readonly List<int> dividers=[];
    readonly List<Font> fonts=[];
    readonly StringFormat left=new(StringFormat.GenericTypographic){Trimming=StringTrimming.None};
    readonly StringFormat right=new(StringFormat.GenericTypographic){Alignment=StringAlignment.Far,Trimming=StringTrimming.None};
    readonly CardField[] fields;
    readonly IncomeButtons? buttons;
    readonly int dpi,width,bodyHeight,headerHeight;
    readonly string language;
    readonly bool dark,redGain;
    readonly Color paper,outline,ink,muted,accent,wash,tagFill,rule;
    readonly Font label,detail,figure,number,amount,note,tag,buttonFont,icon;
    internal Padding Shadow {get;}
    internal Rectangle Actions {get;}
    int P(int dip)=>OverlayLayout.Px(dip,dpi);
    Font Face(string family,int size,FontStyle style=FontStyle.Regular){
        var font=new Font(family,Math.Max(1,size*dpi/96f),style,GraphicsUnit.Pixel);fonts.Add(font);return font;
    }
    static Color Hex(string value)=>ColorTranslator.FromHtml(value);
    internal bool Matches(CardField[] next,IncomeButtons? actions,int w,int scale,string lang,bool night,bool red)=>
        width==w && dpi==scale && language==lang && dark==night && redGain==red && buttons==actions && fields.SequenceEqual(next);

    internal NativeHoverCard(CardField[] fields,IncomeButtons? buttons,int width,int dpi,string language,bool dark,bool redGain){
        this.fields=fields;this.buttons=buttons;this.width=width;this.dpi=dpi;this.language=language;this.dark=dark;this.redGain=redGain;
        Color C(string light,string night)=>Hex(dark?night:light);
        paper=C("#ffffff","#262b28");outline=C("#d4ddd7","#465048");ink=C("#22332a","#eef3ef");
        muted=C("#59675e","#b3bfb5");accent=C("#1f7057","#9ddbb9");
        wash=C("#edf5ef","#2d3d33");tagFill=C("#f1f4f1","#353e37");rule=C("#e1e6e1","#434a46");
        string family=language switch{"zh-CN"=>"Microsoft YaHei UI","ja"=>"Yu Gothic UI","ko"=>"Malgun Gothic",_=>"Segoe UI"};
        label=Face(family,13);note=Face(family,12);detail=Face(family,14,FontStyle.Bold);
        number=Face("Segoe UI Semibold",13);amount=Face("Segoe UI Semibold",16);
        figure=Face("Segoe UI Semibold",25);tag=Face("Segoe UI Semibold",12);
        buttonFont=Face(family,13,FontStyle.Bold);icon=Face("Segoe MDL2 Assets",14);
        Shadow=new(P(12),P(6),P(12),P(16));
        using var surface=new Bitmap(1,1);surface.SetResolution(dpi,dpi);
        using var g=Graphics.FromImage(surface);Prepare(g);
        int pad=P(18),inner=Math.Max(1,width-2*pad),gap=P(9),leftWidth=Math.Max(1,(int)((inner-gap)*1.13/2.13));
        int rightWidth=Math.Max(1,inner-gap-leftWidth),rightX=pad+leftWidth+gap;
        int H(string text,Font font,int available)=>(int)Math.Ceiling(g.MeasureString(text,font,Math.Max(1,available),left).Height)+1;
        int W(string text,Font font)=>(int)Math.Ceiling(g.MeasureString(text,font,PointF.Empty,left).Width);
        int Text(string text,Font font,Color color,int x,int y,int w,bool alignRight=false){
            int h=H(text,font,w);
            runs.Add(new(text,font,color,new(x,y,Math.Max(1,w),h),alignRight));return y+h;
        }
        int y=P(17);
        if(fields.Length==1){
            y=Text(fields[0].Label,detail,ink,pad,y,inner)+P(6);
            bodyHeight=Text(fields[0].Value,label,muted,pad,y,inner)+P(16);return;
        }
        var theme=new FinanceTheme(dark,redGain);
        int labelEnd=Math.Max(Text(fields[0].Label,label,muted,pad,y,leftWidth),Text(fields[1].Label,label,muted,rightX,y,rightWidth));
        int Header(int i,int x,int cell){
            string[] parts=fields[i].Value.Split('\n',2);
            string main=parts[0],prefix="";
            if(i==1 && main.Any(char.IsDigit)){
                foreach(string token in new[]{"约","約","약","≈"})
                    if(main.StartsWith(token,StringComparison.Ordinal)){prefix=token;main=main[token.Length..].TrimStart();break;}
            }
            int prefixWidth=prefix.Length==0?0:W(prefix,label)+P(4);
            int valueY=labelEnd+P(4),available=Math.Max(1,cell-prefixWidth);
            Font font=main.Any(char.IsDigit)?figure:detail;
            if(font==figure && W(main,font)>available){
                float scale=available/(float)W(main,font);
                font=Face("Segoe UI Semibold",Math.Clamp((int)(25*scale),15,25));
            }
            int bottom=Text(main,font,i==1?accent:ink,x+prefixWidth,valueY,available);
            if(prefixWidth>0){
                float Baseline(Font f)=>f.Size*f.FontFamily.GetCellAscent(f.Style)/f.FontFamily.GetEmHeight(f.Style);
                Text(prefix,label,accent,x,valueY+Math.Max(0,(int)Math.Round(Baseline(font)-Baseline(label))),prefixWidth);
            }
            if(parts.Length==2)bottom=Text(parts[1],note,muted,x,bottom+P(4),cell);
            return bottom;
        }
        headerHeight=Math.Max(Header(0,pad,leftWidth),Header(1,rightX,rightWidth))+P(14);
        y=headerHeight+P(13);
        for(int i=2;i<=4;i++){
            var field=fields[i];
            int a=Text(field.Label,label,muted,pad,y,leftWidth);
            int b=Text(field.Value,field.Value.Any(char.IsDigit)?number:label,ink,rightX,y,rightWidth,true);
            if(field.Note is {} sub)b=Text(sub,note,muted,pad,b+P(2),inner,true);
            y=Math.Max(a,b)+P(7);
        }
        // Multiple models occupy separate tags, wrapping onto the next line within the value column.
        var models=fields[5];
        int modelLabel=Math.Min(inner/2,W(models.Label,label)+P(12));
        int labelBottom=Text(models.Label,label,muted,pad,y+P(2),modelLabel);
        if(models.ModelTags){
            int limit=Math.Max(P(36),inner-modelLabel-P(6)),lineWidth=0;
            var line=new List<(string Name,int Width)>();
            void TagLine(){
                int x=pad+inner-lineWidth;
                int height=line.Max(m=>H(m.Name,tag,m.Width-P(25)))+P(4);
                foreach(var m in line){
                    tags.Add(new(x,y,m.Width,height));
                    Text(m.Name,tag,ink,x+P(17),y+P(2),m.Width-P(25));
                    x+=m.Width+P(5);
                }
                y+=height+P(4);line.Clear();lineWidth=0;
            }
            foreach(string name in models.Value.Split(" · ",StringSplitOptions.RemoveEmptyEntries)){
                int w=Math.Min(limit,W(name,tag)+P(25));
                if(line.Count>0 && lineWidth+P(5)+w>limit)TagLine();
                lineWidth+=w+(line.Count>0?P(5):0);line.Add((name,w));
            }
            if(line.Count>0){TagLine();y-=P(4);}
        }else y=Text(models.Value,label,muted,rightX,y,rightWidth,true);
        y=Math.Max(labelBottom,y)+P(12);
        int pairEnd=fields.Length>=10?10:8;
        for(int i=6;i<pairEnd;i+=2){
            dividers.Add(y);y+=P(11);
            int end=Math.Max(Text(fields[i].Label,label,muted,pad,y,leftWidth),Text(fields[i+1].Label,label,muted,rightX,y,rightWidth))+P(4);
            int Cell(int index,int x,int w){
                var f=fields[index];bool numeric=f.Value.Any(char.IsDigit);
                int bottom=Text(f.Value,numeric?amount:label,f.ProfitSign==0?(numeric?ink:muted):theme.Profit(f.ProfitSign),x,end,w);
                return f.Note is {} sub?Text(sub,note,muted,x,bottom+P(2),w):bottom;
            }
            y=Math.Max(Cell(i,pad,leftWidth),Cell(i+1,rightX,rightWidth))+P(12);
        }
        for(int i=pairEnd;i<fields.Length;i++){
            int a=Text(fields[i].Label,note,muted,pad,y,leftWidth);
            int b=Text(fields[i].Value,number,ink,rightX,y,rightWidth,true);y=Math.Max(a,b)+P(7);
        }
        bodyHeight=y+P(3);
        Actions=new(pad,y,inner,P(78));
    }

    internal Size BodySize(bool actions)=>new(width,actions?Actions.Bottom+P(4):bodyHeight);
    static void Prepare(Graphics g){
        g.PageUnit=GraphicsUnit.Pixel;g.SmoothingMode=SmoothingMode.AntiAlias;
        g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
    }
    internal Bitmap Render(Size body,bool actions){
        var bitmap=new Bitmap(body.Width+Shadow.Horizontal,body.Height+Shadow.Vertical,PixelFormat.Format32bppPArgb);
        bitmap.SetResolution(dpi,dpi);
        using var g=Graphics.FromImage(bitmap);Prepare(g);g.Clear(Color.Transparent);
        g.TranslateTransform(Shadow.Left,Shadow.Top);
        var face=new RectangleF(.5f,.5f,body.Width-1,body.Height-1);
        // System.Drawing takes the width and height of the corner ellipse.
        var radius=new SizeF(P(26),P(26));
        // Successive translucent contours give the card a soft shadow without another window.
        int spread=P(10);
        for(int n=spread;n>0;n--){
            var shadow=face;shadow.Inflate(n*.84f,n*.8f);shadow.Offset(0,P(6));
            using var tint=new SolidBrush(Color.FromArgb(Math.Clamp((dark?110:35)/spread,1,22),Color.Black));
            g.FillRoundedRectangle(tint,shadow,new SizeF(radius.Width+2*n,radius.Height+2*n));
        }
        using var surface=new SolidBrush(paper);g.FillRoundedRectangle(surface,face,radius);
        using var line=new Pen(rule,Math.Max(1,dpi/96f));
        if(headerHeight>0){
            var state=g.Save();g.SetClip(new Rectangle(0,0,body.Width,headerHeight));
            // CSS 108deg points slightly downward from left to right.
            using var gradient=new LinearGradientBrush(new Rectangle(0,0,body.Width,headerHeight),wash,paper,18f);
            g.FillRoundedRectangle(gradient,face,radius);g.Restore(state);
            g.DrawLine(line,1,headerHeight,body.Width-1,headerHeight);
        }
        using var chip=new SolidBrush(tagFill);using var dot=new SolidBrush(accent);
        foreach(var box in tags){
            g.FillRoundedRectangle(chip,box,new SizeF(P(10),P(10)));
            int d=P(5);g.FillEllipse(dot,box.X+P(7),box.Y+(box.Height-d)/2,d,d);
        }
        foreach(int y in dividers)g.DrawLine(line,P(18),y,body.Width-P(18),y);
        foreach(var run in runs){
            using var brush=new SolidBrush(run.Color);
            g.DrawString(run.Text,run.Font,brush,run.Box,run.Right?right:left);
        }
        if(actions && buttons!=null)DrawActions(g,buttons,body.Width,line);
        using var border=new Pen(outline,Math.Max(1,dpi/96f));
        g.DrawRoundedRectangle(border,face,radius);
        return bitmap;
    }
    void DrawActions(Graphics g,IncomeButtons buttons,int cardWidth,Pen line){
        var areas=buttons.Layout(Actions,dpi);
        using var center=new StringFormat(left){Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center,
            Trimming=StringTrimming.EllipsisCharacter,FormatFlags=StringFormatFlags.NoWrap};
        using var accentBrush=new SolidBrush(accent);
        void Button(Rectangle box,string text,bool primary){
            var rect=new RectangleF(box.X+.5f,box.Y+.5f,box.Width-1,box.Height-1);
            var radius=new SizeF(P(14),P(14));
            using var fill=new SolidBrush(primary?wash:paper);using var edge=new Pen(outline,Math.Max(1,dpi/96f));
            using var fg=new SolidBrush(primary?accent:ink);
            g.FillRoundedRectangle(fill,rect,radius);g.DrawRoundedRectangle(edge,rect,radius);
            int iconSize=P(15),gap=P(6);
            float tw=Math.Min(g.MeasureString(text,buttonFont,PointF.Empty,left).Width+2,Math.Max(1,rect.Width-iconSize-gap-P(6)));
            float x=rect.X+(rect.Width-iconSize-gap-tw)/2;
            if(primary)g.DrawString("\uE70F",icon,fg,new RectangleF(x,rect.Y,iconSize,rect.Height),center);
            else{
                var saved=g.Save();
                g.TranslateTransform(x,rect.Y+(rect.Height-iconSize)/2);
                g.ScaleTransform(iconSize/16f,iconSize/16f);
                using var stroke=new Pen(ink,1.05f){LineJoin=LineJoin.Round};
                using var folder=new GraphicsPath();
                folder.AddLines([new PointF(1.3f,3),new(5.6f,3),new(7.7f,5),new(14.5f,5),
                    new(14.5f,13),new(1.3f,13)]);
                folder.CloseFigure();g.DrawPath(stroke,folder);g.DrawLine(stroke,1.5f,6,14.2f,6);
                g.Restore(saved);
            }
            g.DrawString(text,buttonFont,fg,new RectangleF(x+iconSize+gap,rect.Y,tw,rect.Height),center);
        }
        Button(areas.Chat,buttons.Chat,true);
        if(buttons.Project!=null)Button(areas.Project,buttons.Project,false);
        g.DrawLine(line,1,areas.Report.Top-P(3),cardWidth-1,areas.Report.Top-P(3));
        using var trailing=new StringFormat(center){Alignment=StringAlignment.Far};
        g.DrawString(buttons.Report,buttonFont,accentBrush,areas.Report,trailing);
        if(buttons.Subscription!=null){
            using var leading=new StringFormat(center){Alignment=StringAlignment.Near};
            g.DrawString(buttons.Subscription,buttonFont,accentBrush,areas.Subscription,leading);
        }
    }
    public void Dispose(){foreach(var f in fonts)f.Dispose();left.Dispose();right.Dispose();}
}
