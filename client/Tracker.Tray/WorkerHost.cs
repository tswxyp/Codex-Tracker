using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodexTracker.Tray;

internal sealed class WorkerHost(string database,string root,string? hostExe=null):IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct JobLimits
    {
        public long PerProcess,PerJob;
        public uint Flags;
        public nuint MinWorkingSet,MaxWorkingSet;
        public uint ActiveProcesses;
        public nuint Affinity;
        public uint Priority,Scheduling;
        public ulong ReadOps,WriteOps,OtherOps,ReadBytes,WriteBytes,OtherBytes;
        public nuint ProcessMemory,JobMemory,PeakProcess,PeakJob;
    }
    [DllImport("kernel32",CharSet=CharSet.Unicode,SetLastError=true)] static extern SafeFileHandle CreateJobObject(nint attributes,string name);
    [DllImport("kernel32",CharSet=CharSet.Unicode,SetLastError=true)] static extern SafeFileHandle OpenJobObject(uint access,bool inherit,string name);
    [DllImport("kernel32",SetLastError=true)] static extern bool SetInformationJobObject(SafeFileHandle job,int kind,ref JobLimits limits,int size);
    [DllImport("kernel32",SetLastError=true)] static extern bool AssignProcessToJobObject(SafeFileHandle job,nint process);
    SafeFileHandle? job;
    Process? supervisor;
    long nextTry,started;
    int delay=1000;
    internal string Status {get;private set;}="等待采集";
    internal void Tick()
    {
        long now=Environment.TickCount64;
        if(supervisor is {HasExited:false})
        {
            if(now-started>60000)delay=1000;
            return;
        }
        if(supervisor!=null){Stop();Schedule(now);}
        if(now<nextTry)return;
        try
        {
            string name="Local\\CodexTracker-worker-"+Guid.NewGuid().ToString("N");
            job=CreateJobObject(0,name);
            var limits=new JobLimits{Flags=0x2000}; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if(job.IsInvalid || !SetInformationJobObject(job,9,ref limits,Marshal.SizeOf<JobLimits>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            supervisor=Process.Start(StartInfo(hostExe??Environment.ProcessPath!,"--owned-worker",name,database,root))
                ??throw new IOException("未能启动 Worker 管理进程。");
            started=now;
            Status="采集中";
        }
        catch(Exception ex) when(ex is Win32Exception or IOException or InvalidOperationException){Stop();Schedule(now);}
    }
    void Schedule(long now){nextTry=now+delay;delay=Math.Min(delay*2,60000);Status="Worker 中断，等待重启";}
    internal static ProcessStartInfo StartInfo(string exe,params string[] args)
    {
        var info=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=AppContext.BaseDirectory};
        foreach(var arg in args)info.ArgumentList.Add(arg);
        return info;
    }
    // Join before spawning the Worker. If the owner dies anywhere during startup, either
    // OpenJobObject fails or the closing last handle terminates the entire private job.
    internal static int Child(string name,string database,string root)
    {
        using(var handle=OpenJobObject(1,false,name))
        using(var self=Process.GetCurrentProcess())
            if(handle.IsInvalid || !AssignProcessToJobObject(handle,self.Handle))return 2;
        using var worker=Process.Start(StartInfo(Path.Combine(AppContext.BaseDirectory,"CodexTracker.exe"),
            "watch","--db",database,"--root",root));
        if(worker==null)return 2;
        worker.WaitForExit();
        return worker.ExitCode;
    }
    internal void Stop()
    {
        job?.Dispose();job=null;
        supervisor?.Dispose();supervisor=null;
        nextTry=0;
        Status="已暂停";
    }
    public void Dispose()=>Stop();
}
