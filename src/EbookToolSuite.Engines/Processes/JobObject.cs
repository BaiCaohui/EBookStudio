using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EbookToolSuite.Engines.Processes;

/// <summary>
/// Windows Job Object 包装（设计文档 6.3.1）。
///
/// 目的：主进程崩溃或被强杀时，Calibre 子进程（它还会再 spawn 子 Python）
/// 跟着一起死，不留孤儿进程占着 CPU 和文件句柄。
/// 关闭句柄 = 杀光 job 内全部进程（KILL_ON_JOB_CLOSE）。
///
/// 注意：Assign 必须在 Process.Start() 之后【立刻】调用，中间不要插别的代码，
/// 否则子进程可能在纳管前就 fork 出去了。不要用 CREATE_SUSPENDED 方案。
/// </summary>
internal sealed partial class JobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private readonly SafeFileHandle _handle;
    private bool _disposed;

    public JobObject(string? name = null)
    {
        _handle = CreateJobObject(IntPtr.Zero, name);
        if (_handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject 失败");
        }

        ApplyKillOnClose();
    }

    /// <summary>把刚启动的进程纳入 job。</summary>
    public void Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!AssignProcessToJobObject(_handle, process.Handle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject 失败");
        }
    }

    /// <summary>立刻杀掉 job 内全部进程（取消/超时用）。句柄仍然有效，可以继续用。</summary>
    public void TerminateAll(uint exitCode = 1)
    {
        if (_disposed)
        {
            return;
        }

        TerminateJobObject(_handle, exitCode);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle.Dispose();
    }

    private void ApplyKillOnClose()
    {
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;

        var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, ptr, fDeleteOld: false);
            if (!SetInformationJobObject(
                    _handle, JobObjectExtendedLimitInformation, ptr, (uint)length))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(), "SetInformationJobObject 失败");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}

// ---------- 结构体与 P/Invoke ----------

/// <summary>JOBOBJECT_BASIC_LIMIT_INFORMATION。字段顺序与 winnt.h 一致，不可调整。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
{
    public long PerProcessUserTimeLimit;
    public long PerJobUserTimeLimit;
    public uint LimitFlags;
    public UIntPtr MinimumWorkingSetSize;
    public UIntPtr MaximumWorkingSetSize;
    public uint ActiveProcessLimit;
    public UIntPtr Affinity;
    public uint PriorityClass;
    public uint SchedulingClass;
}

[StructLayout(LayoutKind.Sequential)]
internal struct IO_COUNTERS
{
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
{
    public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
    public IO_COUNTERS IoInfo;
    public UIntPtr ProcessMemoryLimit;
    public UIntPtr JobMemoryLimit;
    public UIntPtr PeakProcessMemoryUsed;
    public UIntPtr PeakJobMemoryUsed;
}

/// <summary>Job Object 的 P/Invoke 声明。全部集中在 partial 类里，调用点在上面的 partial 类。</summary>
internal sealed partial class JobObject
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle hJob, int jobObjectInformationClass,
        IntPtr lpJobObjectInformation, uint cbJobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(SafeFileHandle hJob, uint uExitCode);
}
