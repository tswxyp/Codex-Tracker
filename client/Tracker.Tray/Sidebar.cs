using Interop.UIAutomationClient;
using static Interop.UIAutomationClient.UIA_PropertyIds;
using static Interop.UIAutomationClient.UIA_ControlTypeIds;
using static Interop.UIAutomationClient.TreeScope;

namespace CodexTracker.Tray;

internal sealed record ChatRow(string Name, Rectangle Bounds, Rectangle[] Actions, bool Current, int? TimeLeft=null, bool HitVerified=true);
internal sealed record SidebarFrame(nint Window,Rectangle WindowBounds,int Dpi,List<ChatRow> Rows,int Candidates,int ScrollAncestors,long Started);

internal sealed class Sidebar
{
    readonly Dictionary<(string,Rectangle,bool),long> verified=new();
    (string,Rectangle,bool)[] lastLayout=[];
    internal void Invalidate(){verified.Clear();lastLayout=[];}
    nint panelWindow;
    List<IUIAutomationElement> livePanels=[];
    int scrollAbove;
    readonly IUIAutomation ui=new CUIAutomation8();
    readonly IUIAutomationCacheRequest cache;
    readonly IUIAutomationCondition navigation,shell;
    readonly IUIAutomationTreeWalker walk;
    internal Sidebar(bool semanticNavigationOnly=false)
    {
        var limits=(IUIAutomation2)ui;
        limits.ConnectionTimeout=800;
        limits.TransactionTimeout=800;
        walk=ui.ControlViewWalker;
        cache=ui.CreateCacheRequest();
        cache.TreeFilter=ui.ControlViewCondition;
        cache.TreeScope=TreeScope_Subtree;
        foreach(int id in new[]{UIA_ControlTypePropertyId,UIA_NamePropertyId,UIA_ClassNamePropertyId,UIA_BoundingRectanglePropertyId,
            UIA_IsOffscreenPropertyId,UIA_AriaPropertiesPropertyId,UIA_IsScrollPatternAvailablePropertyId,UIA_IsInvokePatternAvailablePropertyId})
            cache.AddProperty(id);
        shell=semanticNavigationOnly?ui.CreateFalseCondition():ui.CreatePropertyConditionEx(UIA_ClassNamePropertyId,"app-shell-left-panel",PropertyConditionFlags.PropertyConditionFlags_MatchSubstring);
        navigation=ui.CreatePropertyCondition(UIA_AriaRolePropertyId,"navigation");
    }
    static Rectangle Box(IUIAutomationElement e)
    {
        var b=e.CachedBoundingRectangle;
        return Rectangle.FromLTRB(b.left,b.top,b.right,b.bottom);
    }
    bool HitsRow(IUIAutomationElement row,Point point)
    {
        var p=new tagPOINT{x=point.X,y=point.Y};
        for(int attempt=0;attempt<2;attempt++)
        {
            var hit=ui.ElementFromPoint(p);
            for(int i=0;hit!=null && i<8;i++,hit=walk.GetParentElement(hit))
                if(ui.CompareElements(row,hit)!=0)return true;
            if(attempt==0)Thread.Sleep(16);
        }
        return false;
    }
    internal SidebarFrame Read(nint window,bool probePoints=true,bool prioritizeHover=false,ChatRow? anchor=null)
    {
        long started=Environment.TickCount64;
        var outer=Native.Bounds(window);
        int dpi=(int)Native.GetDpiForWindow(window),candidates=0,scroll=0;
        if(panelWindow!=window || livePanels.Count==0)
        {
            var root=ui.ElementFromHandle(window);
            var found=root.FindAll(TreeScope_Descendants,shell);
            if(found.Length==0)found=root.FindAll(TreeScope_Descendants,navigation);
            livePanels=[];scrollAbove=0;panelWindow=window;
            for(int i=0;i<found.Length;i++)
            {
                var panel=found.GetElement(i);livePanels.Add(panel);
                for(var p=walk.GetParentElement(panel);p!=null;p=walk.GetParentElement(p))
                {
                    if(Convert.ToBoolean(p.GetCurrentPropertyValue(UIA_IsScrollPatternAvailablePropertyId))){scrollAbove++;break;}
                    if(p.CurrentControlType==UIA_WindowControlTypeId)break;
                }
            }
        }
        var ordered=new List<(IUIAutomationElement Element,ChatRow Row,Point Probe)>();
        scroll=scrollAbove;
        try
        {
            foreach(var live in livePanels)
            {
                var panel=live.BuildUpdatedCache(cache);
                if(panel.CachedIsOffscreen==0 && Box(panel).IntersectsWith(outer))Visit(panel,outer,0,0,0);
            }
        }
        catch{livePanels.Clear();throw;}
        if(ordered.Count==0)livePanels.Clear();
        void Visit(IUIAutomationElement node,Rectangle clip,int parent,int grand,int great)
        {
            var b=Box(node);
            int type=node.CachedControlType;
            if(b.Width>0 && b.Height>0)clip=Rectangle.Intersect(b,clip);
            if(clip.Width<=0 || clip.Height<=0)return;
            if(Convert.ToBoolean(node.GetCachedPropertyValue(UIA_IsScrollPatternAvailablePropertyId)))scroll++;
            var children=node.GetCachedChildren();
            bool inListItem=parent==UIA_GroupControlTypeId && grand==UIA_ListItemControlTypeId && great==UIA_ListControlTypeId;
            bool legacyRow=type==UIA_ButtonControlTypeId && inListItem &&
                Convert.ToBoolean(node.GetCachedPropertyValue(UIA_IsInvokePatternAvailablePropertyId));
            int titleIndex=-1;
            string? title=legacyRow?node.CachedName:null;
            if(type==UIA_GroupControlTypeId && (inListItem || (parent==UIA_ListItemControlTypeId && grand==UIA_ListControlTypeId)) &&
                HasClass(node,"sidebar-item") && children!=null)
            {
                // Codex 26.1117 exposes the row bounds and aria-current on a group.
                // Its primary title button is distinct from the adjacent menu and pin controls.
                for(int i=0;i<children.Length;i++){
                    var child=children.GetElement(i);
                    if(child.CachedControlType!=UIA_ButtonControlTypeId)continue;
                    bool titleButton=HasClass(child,"before:sidebar-item") ||
                        (child.CachedName==node.CachedName && (child.CachedAriaProperties??"").Split(';').Contains("hasinterestfor=true"));
                    if(!titleButton)continue;
                    if(titleIndex>=0)return;
                    titleIndex=i;title=child.CachedName;
                }
                if(titleIndex<0 && (node.CachedClassName??"").Contains("app-action-sidebar-thread-selected",StringComparison.Ordinal))return;
            }
            if(legacyRow || titleIndex>=0)
            {
                candidates++;
                if(node.CachedIsOffscreen!=0 || b.Width<1 || b.Height<1 || string.IsNullOrWhiteSpace(title))return;
                var actions=new List<Rectangle>();
                int? timeLeft=null;
                void ActionBounds(IUIAutomationElementArray? array,int primaryButton=-1)
                {
                    if(array==null)return;
                    for(int j=0;j<array.Length;j++)
                    {
                        var child=array.GetElement(j);
                        var bounds=Box(child);
                        if(child.CachedIsOffscreen!=0)continue;
                        if(j!=primaryButton && child.CachedControlType==UIA_ButtonControlTypeId && bounds.IntersectsWith(b)){
                            actions.Add(bounds);
                            continue;
                        }
                        // Some Codex versions expose the timestamp as a separate text run.
                        if(child.CachedControlType==UIA_TextControlTypeId && bounds.Width>0 &&
                            bounds.Left>b.Left+b.Width/2 && b.Contains(bounds) &&
                            child.CachedName!=title && IsTimestamp(child.CachedName))
                            timeLeft=Math.Min(timeLeft??bounds.Left,bounds.Left);
                        ActionBounds(child.GetCachedChildren());
                    }
                }
                ActionBounds(children,titleIndex);
                bool current=(node.CachedAriaProperties??"").Split(';').Contains("current=page");
                var probe=new Point(clip.Left+Math.Min(clip.Width/2,OverlayLayout.Px(18,dpi)),clip.Top+clip.Height/2);
                if(titleIndex>=0){
                    // The indentation of a project chat can hit the document instead of the row.
                    var label=Rectangle.Intersect(clip,Box(children!.GetElement(titleIndex)));
                    if(label.Width>0 && label.Height>0)probe=new(label.Left+label.Width/2,label.Top+label.Height/2);
                }
                ordered.Add((node,new(title,clip,actions.ToArray(),current,timeLeft),probe));
                return;
            }
            if(children==null)return;
            for(int j=0;j<children.Length;j++)Visit(children.GetElement(j),clip,type,parent,grand);
        }
        var rows=new List<ChatRow>();
        // Keep all structural candidates for duplicate-title checks. The interactive path
        // only needs a fresh hit on the hovered row or the row owning the open card.
        var mouse=Cursor.Position;
        var keys=ordered.Select(item=>(item.Row.Name,item.Row.Bounds,item.Row.Current)).ToArray();
        if(!lastLayout.SequenceEqual(keys))verified.Clear();
        lastLayout=keys;
        foreach(var (element,row,primaryPoint) in ordered)
        {
            var b=row.Bounds;
            var p=primaryPoint;
            var key=(row.Name,b,row.Current);
            if(!probePoints){rows.Add(row);continue;}
            bool onRow=b.Contains(mouse);
            bool previousAnchor=anchor!=null && anchor.Name==row.Name && anchor.Bounds==b;
            bool valid=verified.TryGetValue(key,out long until) && until>started;
            if(prioritizeHover && !onRow && !previousAnchor && !row.Current && !valid){
                rows.Add(row with {HitVerified=false});
                continue;
            }
            if(Native.GetAncestor(Native.WindowFromPoint(p),2)!=window){
                if(onRow && Native.GetAncestor(Native.WindowFromPoint(mouse),2)==window)p=mouse;
                else{
                    verified.Remove(key);
                    if(prioritizeHover)rows.Add(row with {HitVerified=false});
                    continue;
                }
            }
            if(!valid || onRow)
            {
                var probe=onRow && Native.GetAncestor(Native.WindowFromPoint(mouse),2)==window?mouse:p;
                bool hit=HitsRow(element,probe);
                if(!hit && probe!=p)hit=HitsRow(element,p);
                if(!hit){
                    verified.Remove(key);
                    if(prioritizeHover)rows.Add(row with {HitVerified=false});
                    continue;
                }
                verified[key]=started+2500+Random.Shared.Next(2000);
            }
            rows.Add(row);
        }
        return new(window,outer,dpi,rows,candidates,scroll,started);
    }
    static bool HasClass(IUIAutomationElement element,string token)=>
        (element.CachedClassName??"").Split(' ',StringSplitOptions.RemoveEmptyEntries).Contains(token,StringComparer.Ordinal);
    static bool IsTimestamp(string? text)=>text is {Length:>0 and <=24} &&
        System.Text.RegularExpressions.Regex.IsMatch(text.Trim(),
            @"^(?:刚刚|昨天|今天|now|today|yesterday|\d{1,2}:\d{2}|\d+\s*(?:[smhdwy]|秒|分钟|小时|天|周|个月|年)(?:前| ago)?)$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant|System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}
