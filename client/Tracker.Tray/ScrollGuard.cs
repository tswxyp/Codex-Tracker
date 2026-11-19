using System.Runtime.InteropServices;

namespace CodexTracker.Tray;

// Observe wheel/button transitions while Codex is foreground. No legacy input is disabled,
// no messages are consumed from Codex, no positions, key strokes or clicks are persisted.
internal sealed class ScrollGuard:NativeWindow,IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct Device {public ushort Page,Usage;public uint Flags;public nint Target;}
    [StructLayout(LayoutKind.Sequential)] struct Header {public uint Type,Size;public nint Device,Param;}
    [StructLayout(LayoutKind.Sequential)] struct Packet
    {
        public Header Header;
        public ushort Flags,Padding,Buttons,ButtonData;
        public uint RawButtons;public int X,Y;public uint Extra;
    }
    [DllImport("user32")] static extern bool RegisterRawInputDevices(ref Device device,uint count,uint size);
    [DllImport("user32")] static extern uint GetRawInputData(nint handle,uint command,out Packet packet,ref uint size,uint headerSize);
    readonly Action changed;
    bool enabled;
    internal ScrollGuard(Action changed)
    {
        this.changed=changed;
        CreateHandle(new CreateParams{Parent=-3,Caption="CodexTracker input observer"});
    }
    internal void Enable(bool value)
    {
        if(value==enabled)return;
        var dev=new Device{Page=1,Usage=2,Flags=value?0x100u:1u,Target=value?Handle:0};
        if(RegisterRawInputDevices(ref dev,1,(uint)Marshal.SizeOf<Device>()))enabled=value;
    }
    protected override void WndProc(ref Message m)
    {
        if(enabled && m.Msg==0xff)
        {
            uint size=(uint)Marshal.SizeOf<Packet>();
            if(GetRawInputData(m.LParam,0x10000003,out var p,ref size,(uint)Marshal.SizeOf<Header>())==size &&
                p.Header.Type==0 && p.Buttons!=0)changed();
        }
        base.WndProc(ref m);
    }
    public void Dispose(){Enable(false);DestroyHandle();}
}
