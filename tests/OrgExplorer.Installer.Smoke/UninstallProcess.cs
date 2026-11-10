using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OrgExplorer.Installer.Smoke;

public static class UninstallProcess
{
    public static int RunToCompletion(ProcessStartInfo start, TimeSpan timeout)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Uninstall coordination requires Windows job objects.");
        if (start.UseShellExecute || start.RedirectStandardOutput || start.RedirectStandardError || start.RedirectStandardInput || start.Arguments.Length != 0)
            throw new ArgumentException("Use an unredirected process with ArgumentList for uninstall coordination.", nameof(start));
        var elapsed = Stopwatch.StartNew();
        using var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits = new ExtendedLimitInformation { Basic = new() { LimitFlags = 0x2000 } };
        if (!SetJobLimits(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using var port = CreateIoCompletionPort(new IntPtr(-1), IntPtr.Zero, UIntPtr.Zero, 1);
        if (port.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var association = new CompletionPortAssociation { Key = new IntPtr(1), Port = port.DangerousGetHandle() };
        if (!AssociateCompletionPort(job, 7, ref association, (uint)Marshal.SizeOf<CompletionPortAssociation>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var startup = new StartupInformation { Size = (uint)Marshal.SizeOf<StartupInformation>() };
        var command = new StringBuilder(string.Join(" ", new[] { start.FileName }.Concat(start.ArgumentList).Select(Quote)));
        // Assign the suspended root before it can create an otherwise untracked self-delete child.
        if (!CreateProcess(null, command, IntPtr.Zero, IntPtr.Zero, false, 0x08000004, IntPtr.Zero,
            start.WorkingDirectory, ref startup, out var created))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using var process = new SafeFileHandle(created.Process, true);
        using var thread = new SafeFileHandle(created.Thread, true);
        var resumed = false;
        try
        {
            if (!AssignProcessToJobObject(job, process)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (ResumeThread(thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            resumed = true;
            // Completion packets wake the wait; authoritative accounting proves quiescence.
            while (HasActiveProcesses(job))
            {
                var remaining = timeout - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException("Uninstall process tree exceeded its remaining lifecycle budget.");
                if (!GetQueuedCompletionStatus(port, out _, out _, out _, (uint)Math.Ceiling(remaining.TotalMilliseconds)))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 258 && !HasActiveProcesses(job)) break;
                    if (error == 258) throw new TimeoutException("Uninstall process tree exceeded its remaining lifecycle budget.");
                    throw new Win32Exception(error);
                }
            }
            if (!GetExitCodeProcess(process, out var exitCode)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return (int)exitCode;
        }
        finally
        {
            if (!resumed) TerminateProcess(process, 1);
        }
    }

    private static bool HasActiveProcesses(SafeFileHandle job)
    {
        if (!QueryJobAccounting(job, 1, out var accounting, (uint)Marshal.SizeOf<BasicAccountingInformation>(), IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return accounting.ActiveProcesses != 0;
    }

    private static string Quote(string argument)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes).Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long ProcessTime, JobTime;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompletionPortAssociation
    {
        public IntPtr Key, Port;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccountingInformation
    {
        public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
        public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInformation
    {
        public uint Size;
        public IntPtr Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCount, YCount, FillAttribute, Flags;
        public ushort ShowWindow, ReservedBytes;
        public IntPtr ReservedData, Input, Output, Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetJobLimits(SafeFileHandle job, int informationClass, ref ExtendedLimitInformation information, uint length);

    [DllImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssociateCompletionPort(SafeFileHandle job, int informationClass, ref CompletionPortAssociation information, uint length);

    [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryJobAccounting(SafeFileHandle job, int informationClass, out BasicAccountingInformation information, uint length, IntPtr returnedLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateIoCompletionPort(IntPtr file, IntPtr port, UIntPtr key, uint threads);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string? application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string workingDirectory,
        ref StartupInformation startup, out ProcessInformation process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeFileHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetQueuedCompletionStatus(SafeFileHandle port, out uint message, out IntPtr key, out IntPtr overlapped, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeFileHandle process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeFileHandle process, uint exitCode);
}
