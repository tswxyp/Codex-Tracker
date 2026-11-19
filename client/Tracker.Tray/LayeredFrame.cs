using System.Runtime.InteropServices;
using System.Drawing.Imaging;

namespace CodexTracker.Tray;

internal static class LayeredFrame
{
    [StructLayout(LayoutKind.Sequential)]
    struct Blend {public byte Operation,Flags,Alpha,Format;}
    [DllImport("user32")] static extern bool UpdateLayeredWindow(nint hwnd,nint screen,ref Point destination,
        ref Size size,nint source,ref Point origin,uint colorKey,ref Blend blend,uint flags);
    [DllImport("gdi32")] static extern nint CreateCompatibleDC(nint screen);
    [DllImport("gdi32")] static extern nint SelectObject(nint dc,nint obj);
    [DllImport("gdi32")] static extern bool DeleteObject(nint obj);
    [DllImport("gdi32")] static extern bool DeleteDC(nint dc);
    [StructLayout(LayoutKind.Sequential)]
    struct DibHeader {
        public uint HeaderSize;
        public int Width,Height;
        public ushort Planes,BitCount;
        public uint Compression,ImageSize;
        public int XPixelsPerMeter,YPixelsPerMeter;
        public uint ColorsUsed,ColorsImportant;
    }
    [DllImport("gdi32")] static extern nint CreateDIBSection(nint dc,ref DibHeader header,uint usage,out nint pixels,nint section,uint offset);
    [DllImport("kernel32",EntryPoint="RtlMoveMemory")] static extern void CopyMemory(nint destination,nint source,nuint length);

    // GetHbitmap flattens transparency. Copy the premultiplied BGRA pixels to a
    // top-down DIB for the rounded card and its shadow, then submit them together.
    static nint AlphaBitmap(nint dc,Bitmap frame){
        var header=new DibHeader{HeaderSize=(uint)Marshal.SizeOf<DibHeader>(),Width=frame.Width,Height=-frame.Height,Planes=1,BitCount=32};
        nint dib=CreateDIBSection(dc,ref header,0,out var pixels,0,0);
        if(dib==0)return 0;
        try{
            var bits=frame.LockBits(new Rectangle(Point.Empty,frame.Size),ImageLockMode.ReadOnly,PixelFormat.Format32bppPArgb);
            try{
                int bytes=checked(frame.Width*4);
                if(bits.Stride==bytes)CopyMemory(pixels,bits.Scan0,(nuint)checked(bytes*frame.Height));
                else for(int y=0;y<frame.Height;y++)CopyMemory(pixels+y*bytes,bits.Scan0+y*bits.Stride,(nuint)bytes);
            }finally{frame.UnlockBits(bits);}
        }catch{DeleteObject(dib);throw;}
        return dib;
    }

    internal static bool Present(nint window,Point destination,Bitmap frame)
    {
        var dc=CreateCompatibleDC(0);
        if(dc==0)return false;
        nint bitmap=0,previous=0;
        try{
            bool alpha=frame.PixelFormat==PixelFormat.Format32bppPArgb;
            bitmap=alpha?AlphaBitmap(dc,frame):frame.GetHbitmap();
            if(bitmap==0)return false;
            previous=SelectObject(dc,bitmap);
            if(previous==0 || previous==-1)return false;
            var origin=Point.Empty;
            var size=frame.Size;
            var blend=new Blend{Alpha=255,Format=(byte)(alpha?1:0)};
            return UpdateLayeredWindow(window,0,ref destination,ref size,dc,ref origin,0,ref blend,2); // ULW_ALPHA
        }finally{
            if(previous!=0 && previous!=-1)SelectObject(dc,previous);
            DeleteDC(dc);
            if(bitmap!=0)DeleteObject(bitmap);
        }
    }
}
