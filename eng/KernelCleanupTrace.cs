using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

public sealed class KernelCleanupTrace : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Header
    {
        public uint Size, Provider;
        public ulong HistoricalContext, Timestamp;
        public Guid Guid;
        public uint ClientContext, Flags;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Properties
    {
        public Header Header;
        public uint BufferSize, MinimumBuffers, MaximumBuffers, MaximumFileSize;
        public uint LogFileMode, FlushTimer, EnableFlags, AgeLimit;
        public uint NumberOfBuffers, FreeBuffers, EventsLost, BuffersWritten, LogBuffersLost, RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset, LoggerNameOffset;
    }
    private IntPtr properties;
    private ulong session;
    private readonly string name = "OrgExplorerCleanup-" + Guid.NewGuid().ToString("N");
    public uint EventsLost { get; private set; }
    public uint BuffersLost { get; private set; }
    public KernelCleanupTrace(string file)
    {
        var size = Marshal.SizeOf<Properties>();
        var fileBytes = (file.Length + 1) * 2;
        var nameBytes = (name.Length + 1) * 2;
        properties = Marshal.AllocHGlobal(size + fileBytes + nameBytes);
        var options = new Properties {
            Header = new Header { Size = (uint)(size + fileBytes + nameBytes),
                Guid = new Guid("9e814aad-3204-11d2-9a82-006008a86939"), ClientContext = 1, Flags = 0x20000 },
            BufferSize = 64, MinimumBuffers = 2, MaximumBuffers = 64, MaximumFileSize = 128,
            LogFileMode = 0x02000001, FlushTimer = 1, EnableFlags = 0x06000207,
            LogFileNameOffset = (uint)size, LoggerNameOffset = (uint)(size + fileBytes)
        };
        Marshal.StructureToPtr(options, properties, false);
        Marshal.Copy((file + "\0").ToCharArray(), 0, IntPtr.Add(properties, size), file.Length + 1);
        Marshal.Copy((name + "\0").ToCharArray(), 0, IntPtr.Add(properties, size + fileBytes), name.Length + 1);
        var status = StartTrace(out session, name, properties);
        if (status != 0) {
            Marshal.FreeHGlobal(properties);
            properties = IntPtr.Zero;
            throw new Win32Exception((int)status, "Owned kernel process/image trace start failed.");
        }
    }
    public void Dispose()
    {
        if (properties == IntPtr.Zero) return;
        try {
            var status = ControlTrace(session, name, properties, 1);
            if (status != 0) throw new Win32Exception((int)status, "Owned kernel process/image trace stop failed.");
            var result = Marshal.PtrToStructure<Properties>(properties);
            EventsLost = result.EventsLost;
            BuffersLost = result.LogBuffersLost + result.RealTimeBuffersLost;
        } finally { Marshal.FreeHGlobal(properties); properties = IntPtr.Zero; }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    public static object Identity(string path)
    {
        using (var file = CreateFile(path, 0x80, 7, IntPtr.Zero, 3, 0, IntPtr.Zero))
        {
            if (file.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Exact-owned-file metadata open refused.");
            if (!GetFileInformationByHandle(file, out var info))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Exact-owned-file identity query refused.");
            return new { attributes = info.Attributes, volumeSerial = info.VolumeSerial,
                fileIndex = ((ulong)info.IndexHigh << 32) | info.IndexLow,
                length = ((ulong)info.SizeHigh << 32) | info.SizeLow, links = info.Links };
        }
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "StartTraceW")]
    private static extern uint StartTrace(out ulong session, string name, IntPtr properties);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "ControlTraceW")]
    private static extern uint ControlTrace(ulong session, string name, IntPtr properties, uint action);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share,
        IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);
}
