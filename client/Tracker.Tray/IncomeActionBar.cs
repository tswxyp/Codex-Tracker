namespace CodexTracker.Tray;

internal enum CardAction {Chat,Project,Report,Subscription}
internal sealed record IncomeButtons(string Chat,string? Project,string Report,string? Subscription)
{
    internal (Rectangle Chat,Rectangle Project,Rectangle Report,Rectangle Subscription) Layout(Rectangle area,int dpi)
    {
        int gap=OverlayLayout.Px(8,dpi),height=OverlayLayout.Px(32,dpi),width=(area.Width-gap)/2;
        var first=new Rectangle(area.Left,area.Top,Project==null?area.Width:width,height);
        var second=Project==null?Rectangle.Empty:new Rectangle(area.Left+width+gap,area.Top,area.Width-width-gap,height);
        int footer=area.Top+height+OverlayLayout.Px(15,dpi);
        var report=new Rectangle(area.Right-(area.Width-width-gap),footer,area.Width-width-gap,area.Bottom-footer);
        var fee=Subscription==null?Rectangle.Empty:new Rectangle(area.Left,footer,width,area.Bottom-footer);
        return (first,second,report,fee);
    }
}

// The card draws the buttons. This owned window only captures clicks inside their bounds.
internal sealed class IncomeActionBar:Form
{
    internal event Action<CardAction>? EditRequested;
    sealed class ActionButton:Button
    {
        internal ActionButton(){
            SetStyle(ControlStyles.Selectable,false);
            TabStop=false;Cursor=Cursors.Hand;UseMnemonic=false;
        }
    }
    readonly ActionButton chat=new(),project=new(),report=new(),subscription=new();
    (Size Size,int Dpi,bool HasProject,bool HasFee) layout;

    internal IncomeActionBar(){
        FormBorderStyle=FormBorderStyle.None;AutoScaleMode=AutoScaleMode.None;
        ShowInTaskbar=false;StartPosition=FormStartPosition.Manual;
        // Windows routes clicks through a layered window at alpha zero. One is hit-testable.
        Opacity=1d/255;
        Controls.AddRange([chat,project,report,subscription]);
        chat.Click+=(_,_)=>EditRequested?.Invoke(CardAction.Chat);
        project.Click+=(_,_)=>EditRequested?.Invoke(CardAction.Project);
        report.Click+=(_,_)=>EditRequested?.Invoke(CardAction.Report);
        subscription.Click+=(_,_)=>EditRequested?.Invoke(CardAction.Subscription);
    }
    protected override CreateParams CreateParams{get{var p=base.CreateParams;p.ExStyle|=0x08000000|0x80;return p;}}
    protected override bool ShowWithoutActivation=>true;
    protected override void WndProc(ref Message m){
        if(m.Msg==0x21){m.Result=3;return;} // MA_NOACTIVATE
        base.WndProc(ref m);
    }

    internal void Place(Form owner,nint codexWindow,Rectangle bounds,int dpi,IncomeButtons buttons)
    {
        if(Bounds!=bounds)Bounds=bounds;
        bool hasProject=buttons.Project!=null;
        bool hasFee=buttons.Subscription!=null;
        if(layout!=(bounds.Size,dpi,hasProject,hasFee)){
            var areas=buttons.Layout(new Rectangle(Point.Empty,bounds.Size),dpi);
            chat.Bounds=areas.Chat;
            var shape=new Region(areas.Chat);
            project.Visible=hasProject;
            if(hasProject){project.Bounds=areas.Project;shape.Union(areas.Project);}
            report.Bounds=areas.Report;shape.Union(areas.Report);
            subscription.Visible=hasFee;
            if(hasFee){subscription.Bounds=areas.Subscription;shape.Union(areas.Subscription);}
            var previous=Region;Region=shape;previous?.Dispose();
            layout=(bounds.Size,dpi,hasProject,hasFee);
        }
        if(chat.Text!=buttons.Chat)chat.Text=buttons.Chat;
        if(hasProject && project.Text!=buttons.Project)project.Text=buttons.Project;
        if(report.Text!=buttons.Report)report.Text=buttons.Report;
        if(hasFee && subscription.Text!=buttons.Subscription)subscription.Text=buttons.Subscription;
        if(!Visible)Show(owner);
        Native.KeepAbove(Handle,owner.Handle,codexWindow);
    }
}
