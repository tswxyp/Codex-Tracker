using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexTracker.Tray;

internal static class Native
{
    [DllImport("user32")] internal static extern nint GetForegroundWindow();
    [DllImport("user32")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32")] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32")] internal static extern nint WindowFromPoint(Point point);
    [DllImport("user32")] internal static extern nint GetAncestor(nint hwnd,uint flags);
    [DllImport("user32")] static extern nint GetWindow(nint hwnd,uint command);
    [DllImport("user32")] static extern bool SetWindowPos(nint hwnd,nint insertAfter,int x,int y,int width,int height,uint flags);
    [DllImport("user32")] internal static extern uint GetWindowThreadProcessId(nint hwnd,out int process);
    [DllImport("user32", CharSet=CharSet.Unicode)] static extern int GetClassName(nint hwnd,StringBuilder name,int size);
    [DllImport("kernel32", CharSet=CharSet.Unicode)] static extern int GetPackageFamilyName(nint process,ref int length,StringBuilder family);
    [DllImport("user32")] static extern bool GetWindowRect(nint hwnd,out Edges rect);
    [StructLayout(LayoutKind.Sequential)] struct Edges { public int Left,Top,Right,Bottom; }
    internal static Rectangle Bounds(nint hwnd) => GetWindowRect(hwnd,out var r)
        ? Rectangle.FromLTRB(r.Left,r.Top,r.Right,r.Bottom):Rectangle.Empty;

    internal static void KeepAbove(nint popup,nint below,nint foregroundTarget)
    {
        if(GetForegroundWindow()!=foregroundTarget)return;
        // A non-activating show can preserve a hidden window's old stacking position.
        // Repair the order only when needed and never move the owner or take focus.
        nint previous=GetWindow(below,3); // GW_HWNDPREV
        for(int i=0;previous!=0 && i<512;i++,previous=GetWindow(previous,3))
            if(previous==popup)return;
        const uint noSize=1,noMove=2,noActivate=0x10,noOwnerZOrder=0x200;
        SetWindowPos(popup,0,0,0,0,0,noMove|noSize|noActivate|noOwnerZOrder); // HWND_TOP
    }

    internal static bool IsCodex(nint hwnd)
    {
        if(hwnd==0 || IsIconic(hwnd) || !IsWindowVisible(hwnd)) return false;
        var cls=new StringBuilder(100);
        GetClassName(hwnd,cls,cls.Capacity);
        if(cls.ToString()!="Chrome_WidgetWin_1") return false;
        GetWindowThreadProcessId(hwnd,out var pid);
        try
        {
            using var process=Process.GetProcessById(pid);
            int size=256;
            var family=new StringBuilder(size);
            return GetPackageFamilyName(process.Handle,ref size,family)==0 &&
                family.ToString().Equals("OpenAI.Codex_2p2nqsd0c76g0",StringComparison.OrdinalIgnoreCase);
        }
        catch(Exception ex) when(ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception){return false;}
    }
}
