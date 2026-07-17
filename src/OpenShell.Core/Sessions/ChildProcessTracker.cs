using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenShell.Sessions;

/// <summary>
/// 子进程生命周期追踪器。Per ADR-0034 §12.
/// 确保 GUI / CLI 宿主退出时, 其生成的子进程被正确清理, 避免孤儿进程。
/// </summary>
/// <remarks>
/// 平台实现:
/// <list type="bullet">
///   <item><b>Windows</b>: 通过 Job Object (CreateJobObject + AssignProcessToJobObject) 将子进程加入作业,
///     作业标志设为 <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>。父进程退出时 OS 自动终止作业内所有进程。</item>
///   <item><b>Unix</b>: 追踪子进程 PID, 在父进程退出时 (ProcessExit) 发送 SIGTERM。
///     理想方案是在子进程内调用 <c>prctl(PR_SET_PDEATHSIG, SIGTERM)</c>, 但 C# Process.Start
///     使用 fork+exec, 无法在 exec 前注入代码, 故采用 PID 追踪 + 退出时信号清理的等价方案。</item>
/// </list>
/// 用法: 在 <c>Process.Start</c> 后调用 <see cref="AddProcess(Process)"/>。
/// </remarks>
public static class ChildProcessTracker
{
    /// <summary>
    /// 注册一个子进程到追踪器。应在 <see cref="Process.Start(ProcessStartInfo)"/> 返回后立即调用。
    /// 失败时静默记录 stderr, 不抛异常 (追踪失败不应阻塞进程启动)。
    /// </summary>
    /// <param name="process">已启动的子进程。</param>
    public static void AddProcess(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                AddProcessWindows(process);
            }
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                AddProcessUnix(process);
            }
            // 其他平台: 不追踪 (best-effort)。
        }
        catch (Exception ex)
        {
            // 追踪失败不阻塞功能: 进程已启动, 仅清理保障缺失。
            Console.Error.WriteLine($"[sessions] ChildProcessTracker.AddProcess failed: {ex.Message}");
        }
    }

    // ---- Windows: Job Object ----

    private static IntPtr s_jobHandle;
    private static readonly object s_windowsLock = new();

    private static void AddProcessWindows(Process process)
    {
        // 惰性创建 Job Object (线程安全)。
        if (s_jobHandle == IntPtr.Zero)
        {
            lock (s_windowsLock)
            {
                if (s_jobHandle == IntPtr.Zero)
                {
                    s_jobHandle = CreateJobObjectWithKillOnClose();
                    if (s_jobHandle == IntPtr.Zero)
                    {
                        return;
                    }

                    // 注册进程退出时关闭 Job Handle (OS 会自动终止作业内进程)。
                    AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                    {
                        if (s_jobHandle != IntPtr.Zero)
                        {
                            CloseHandle(s_jobHandle);
                            s_jobHandle = IntPtr.Zero;
                        }
                    };
                }
            }
        }

        // 将子进程加入作业。Windows 8+ 支持嵌套作业, 允许子进程已属于其他作业时仍可加入。
        if (!AssignProcessToJobObject(s_jobHandle, process.Handle))
        {
            // 嵌套作业失败时记录但不抛异常 (子进程仍正常运行, 仅失去自动清理保障)。
            Console.Error.WriteLine(
                $"[sessions] AssignProcessToJobObject failed for PID {process.Id} (nested jobs may be unsupported).");
        }
    }

    /// <summary>创建带 KILL_ON_JOB_CLOSE 标志的 Job Object。</summary>
    private static IntPtr CreateJobObjectWithKillOnClose()
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
            return IntPtr.Zero;

        // 配置 JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE: 作业句柄关闭时终止所有子进程。
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
        };

        var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            SetInformationJobObject(
                handle,
                JOBOBJECTINFOCLASS.ExtendedLimitInformation,
                ptr, (uint)length);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }

        return handle;
    }

    // ---- Unix: PID 追踪 + 退出时 SIGTERM ----

    private static readonly HashSet<int> s_trackedPids = new();
    private static readonly object s_unixLock = new();
    private static bool s_unixExitHandlerRegistered;

    private static void AddProcessUnix(Process process)
    {
        lock (s_unixLock)
        {
            s_trackedPids.Add(process.Id);

            if (!s_unixExitHandlerRegistered)
            {
                s_unixExitHandlerRegistered = true;
                AppDomain.CurrentDomain.ProcessExit += (_, _) => KillTrackedUnixProcesses();
            }
        }
    }

    /// <summary>向所有追踪的 Unix 子进程发送 SIGTERM。Per ADR-0034 §12: 超时 5s 不强制 kill。</summary>
    private static void KillTrackedUnixProcesses()
    {
        List<int> pids;
        lock (s_unixLock)
        {
            pids = s_trackedPids.ToList();
        }

        foreach (var pid in pids)
        {
            try
            {
                // SIGTERM = 15。Per ADR-0034 §12: 子进程应 5s 内退出, 超时仅记录 warning 不强制 kill。
                kill(pid, SIGTERM);
            }
            catch
            {
                // best-effort: 进程可能已退出。
            }
        }
    }

    // ---- Windows P/Invoke (kernel32) ----

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    private enum JOBOBJECTINFOCLASS : uint
    {
        ExtendedLimitInformation = 9,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob, JOBOBJECTINFOCLASS infoClass, IntPtr lpInfo, uint cbInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    // ---- Unix P/Invoke (libc) ----

    private const int SIGTERM = 15;

    // prctl 常量: 用于文档化理想方案 (在子进程内调用, C# Process.Start 无法注入)。
    private const int PR_SET_PDEATHSIG = 1;

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    // prctl 定义供参考: 理想方案是在子进程 fork 后 exec 前调用 prctl(PR_SET_PDEATHSIG, SIGTERM),
    // 使父进程退出时内核自动向子进程发送信号。C# Process.Start 使用 fork+exec, 无法在两步之间注入,
    // 故 Unix 采用 PID 追踪 + ProcessExit 时 kill 的等价方案。
    [DllImport("libc", SetLastError = true)]
    private static extern int prctl(int option, int arg2, IntPtr arg3, IntPtr arg4, IntPtr arg5);
}
