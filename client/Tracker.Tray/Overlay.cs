using System.Drawing.Imaging;
using System.Drawing.Text;

namespace CodexTracker.Tray;

internal static class OverlayLayout
{
    internal static int Px(int dip,int dpi)=>(int)Math.Round(dip*dpi/96.0);
    internal static Rectangle Inline(ChatRow row,int dpi,int titleWidth,Size text)
    {
        var box=row.Bounds;
        // Keep the icon and a readable beginning of the native title. Longer titles can use
        // the full row normally; the click-through summary masks just their trailing portion.
        int left=box.Left+Px(30,dpi)+Math.Min(titleWidth,Px(40,dpi));
        int right=Math.Min(box.Right-Px(4,dpi),row.Actions.Select(r=>r.Left-Px(3,dpi)).DefaultIfEmpty(box.Right).Min());
        if(row.TimeLeft is int timeLeft)right=Math.Min(right,timeLeft-Px(3,dpi));
        int w=text.Width+Px(10,dpi),h=text.Height+Px(4,dpi);
        return right-left>=w && box.Height>=h+2?new Rectangle(right-w,box.Top+(box.Height-h)/2,w,h):Rectangle.Empty;
    }
    internal static Rectangle Detail(Rectangle row,Rectangle work,Size size,int dpi)
    {
        int width=Math.Min(work.Width,size.Width),height=Math.Min(work.Height,size.Height);
        int right=row.Right+Px(8,dpi);
        int x=right+width<=work.Right?right:row.Left-width-Px(8,dpi);
        return new(Math.Clamp(x,work.Left,work.Right-width),Math.Clamp(row.Top,work.Top,work.Bottom-height),width,height);
    }
}

internal sealed class Overlay:Form
{
    internal const int Styles=0x80000|0x20|0x08000000|0x80;
    const int HitTest=0x84, MouseActivate=0x21;
    readonly StringFormat centered=new(){Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center,
        FormatFlags=StringFormatFlags.NoWrap,Trimming=StringTrimming.EllipsisCharacter};
    sealed record WindowOwner(nint Handle):IWin32Window;
    bool frameDirty=true;
    string displayed="";
    int fontDpi;
    bool isDetail;
    bool truncatedTitle;
    Font? scaled;
    internal Overlay()
    {
        FormBorderStyle=FormBorderStyle.None;
        AutoScaleMode=AutoScaleMode.None;
        ShowInTaskbar=false;
        StartPosition=FormStartPosition.Manual;
    }
    protected override CreateParams CreateParams{get{var p=base.CreateParams;p.ExStyle|=Styles;return p;}}
    protected override bool ShowWithoutActivation=>true;
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        frameDirty=true;
    }
    protected override void WndProc(ref Message m)
    {
        if(m.Msg==HitTest){m.Result=-1;return;}
        if(m.Msg==MouseActivate){m.Result=3;return;}
        base.WndProc(ref m);
    }
    internal static Font MakeFont(int dpi,bool detail)=>new("Segoe UI",OverlayLayout.Px(detail?14:12,dpi),FontStyle.Regular,GraphicsUnit.Pixel);
    static Bitmap CreateSurface(Size size,int dpi)
    {
        var bitmap=new Bitmap(size.Width,size.Height,PixelFormat.Format32bppRgb);
        bitmap.SetResolution(dpi,dpi);
        return bitmap;
    }
    static Graphics OpenSurface(Bitmap bitmap)
    {
        var graphics=Graphics.FromImage(bitmap);
        graphics.PageUnit=GraphicsUnit.Pixel;
        graphics.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
        return graphics;
    }
    internal bool Place(nint owner,Rectangle bounds,string content,int dpi,bool dark,bool detail,
        Color? rowColor=null,bool clippedTitle=false)
    {
        if(card!=null){card.Dispose();card=null;frameDirty=true;}
        if(fontDpi!=dpi || detail!=isDetail || scaled==null)
        {
            scaled?.Dispose();scaled=MakeFont(dpi,detail);fontDpi=dpi;isDetail=detail;
            frameDirty=true;
        }
        Color bg=detail?(dark?Color.FromArgb(31,36,34):Color.White):
            rowColor??(dark?Color.FromArgb(40,40,43):Color.FromArgb(250,249,247));
        if(BackColor!=bg){BackColor=bg;frameDirty=true;}
        Color fg=dark?Color.FromArgb(219,218,216):Color.FromArgb(78,77,74);
        if(ForeColor!=fg){ForeColor=fg;frameDirty=true;}
        if(displayed!=content || clippedTitle!=truncatedTitle){displayed=content;truncatedTitle=clippedTitle;frameDirty=true;}
        if(Bounds!=bounds){Bounds=bounds;frameDirty=true;}
        if(detail && (Region==null || regionSize!=bounds.Size || regionDpi!=dpi)){
            regionSize=bounds.Size;regionDpi=dpi;
            using var path=new System.Drawing.Drawing2D.GraphicsPath();
            path.AddRoundedRectangle(ClientRectangle,new Size(OverlayLayout.Px(12,dpi),OverlayLayout.Px(12,dpi)));
            var old=Region;Region=new Region(path);old?.Dispose();
        }
        if(!frameDirty && Visible){
            Native.KeepAbove(Handle,owner,owner);
            return true;
        }
        using var frame=CreateSurface(bounds.Size,dpi);
        using(var graphics=OpenSurface(frame)){
            graphics.Clear(BackColor);
            int pad=OverlayLayout.Px(detail?9:4,dpi);
            var box=new Rectangle(Point.Empty,bounds.Size);box.Inflate(-pad,detail?-pad:0);
            using var ink=new SolidBrush(ForeColor);
            using var format=new StringFormat(centered){Alignment=StringAlignment.Near};
            if(truncatedTitle)graphics.DrawString("…",scaled!,ink,box,format);
            format.Alignment=detail?StringAlignment.Near:StringAlignment.Far;
            graphics.DrawString(displayed,scaled!,ink,box,format);
        }
        return PresentFrame(owner,bounds,frame);
    }
    bool PresentFrame(nint owner,Rectangle bounds,Bitmap frame){
        nint window=Handle;
        if(!LayeredFrame.Present(window,bounds.Location,frame)){Hide();frameDirty=true;return false;}
        if(!Visible){
            Show(new WindowOwner(owner));
            if(Handle!=window && !LayeredFrame.Present(Handle,bounds.Location,frame)){Hide();frameDirty=true;return false;}
        }
        Native.KeepAbove(Handle,owner,owner);
        frameDirty=false;
        return true;
    }
    Size regionSize;
    int regionDpi;
    NativeHoverCard? card;
    bool showingActions;
    int designDpi,monitorDpi;
    Size workSize;
    internal Rectangle CardBounds {get;private set;}
    internal int CardDpi=>designDpi;

    internal Rectangle PlaceCard(nint owner,Rectangle row,Rectangle work,int dpi,CardField[] content,bool dark,string language,
        IncomeButtons? buttons=null,IReadOnlyList<ChatRow>? protectedRows=null,bool redProfit=false)
    {
        int width=Math.Min(OverlayLayout.Px(374,dpi),Math.Max(1,work.Width-2*OverlayLayout.Px(12,dpi)));
        if(card==null || workSize!=work.Size || monitorDpi!=dpi || !card.Matches(content,buttons,width,designDpi,language,dark,redProfit)){
            card?.Dispose();designDpi=dpi;monitorDpi=dpi;workSize=work.Size;
            card=new NativeHoverCard(content,buttons,width,designDpi,language,dark,redProfit);
            // Keep the action row reachable on a short, high-DPI display.
            int fullHeight=card.BodySize(buttons!=null).Height+card.Shadow.Vertical;
            if(fullHeight>work.Height){
                designDpi=Math.Max(1,Math.Min(dpi,(int)((long)dpi*work.Height/fullHeight)));
                card.Dispose();card=new NativeHoverCard(content,buttons,width,designDpi,language,dark,redProfit);
            }
            frameDirty=true;
        }
        var shadow=card.Shadow;
        var room=new Rectangle(work.Left+shadow.Left,work.Top+shadow.Top,Math.Max(1,work.Width-shadow.Horizontal),
            Math.Max(1,work.Height-shadow.Vertical));
        Rectangle Layout(bool actions)=>OverlayLayout.Detail(row,room,card.BodySize(actions),dpi);
        var body=Layout(buttons!=null);
        Rectangle footer=Rectangle.Empty;
        if(buttons!=null){
            var area=card.Actions;area.Offset(body.Location);
            if(body.Contains(area) && protectedRows?.Any(r=>r.Bounds.IntersectsWith(area))!=true)footer=area;
            else body=Layout(false);
        }
        CardBounds=body;
        var window=new Rectangle(body.Left-shadow.Left,body.Top-shadow.Top,body.Width+shadow.Horizontal,body.Height+shadow.Vertical);
        bool show=!footer.IsEmpty;
        if(showingActions!=show){showingActions=show;frameDirty=true;}
        if(Region!=null){var old=Region;Region=null;old.Dispose();regionSize=Size.Empty;frameDirty=true;}
        if(Bounds!=window){Bounds=window;frameDirty=true;}
        if(!frameDirty && Visible){Native.KeepAbove(Handle,owner,owner);return footer;}
        using var frame=card.Render(body.Size,show);
        return PresentFrame(owner,window,frame)?footer:Rectangle.Empty;
    }
    protected override void Dispose(bool disposing)
    {
        if(disposing){card?.Dispose();scaled?.Dispose();centered.Dispose();}
        base.Dispose(disposing);
    }
}
