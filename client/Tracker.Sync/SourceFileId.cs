using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace Tracker.Sync;

internal static class SourceFileId
{
    [StructLayout(LayoutKind.Sequential)]
    struct ByHandleFileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created,Accessed,Written;
        public uint Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;
    }
    [DllImport("kernel32.dll",SetLastError=true)]
    static extern bool GetFileInformationByHandle(SafeFileHandle handle,out ByHandleFileInformation info);
    internal static string Read(string path)
    {
        using var h=File.OpenHandle(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        if(!GetFileInformationByHandle(h,out var info))throw new Win32Exception(Marshal.GetLastWin32Error());
        return $"{info.Volume:x8}-{info.IndexHigh:x8}{info.IndexLow:x8}";
    }
}
