using System.Runtime.InteropServices;
using System.Text;
using System.Runtime.InteropServices.ComTypes;

namespace OrgExplorer.Storage.ProcessFixture;

public static class LeaseOwners
{
    public static object Read(string[] paths)
    {
        var result = RmStartSession(out var session, 0, new StringBuilder(33));
        if (result != 0) return new { operation = "start", error = result };
        try
        {
            result = RmRegisterResources(session, (uint)paths.Length, paths, 0, null, 0, null);
            if (result != 0) return new { operation = "register", error = result };
            uint count = 0, reasons = 0;
            result = RmGetList(session, out var needed, ref count, null, ref reasons);
            if (result is not (0 or 234)) return new { operation = "inventory", error = result };
            count = Math.Min(needed, 32);
            var owners = new ProcessInfo[count];
            result = RmGetList(session, out needed, ref count, owners, ref reasons);
            return new
            {
                operation = "inventory", error = result, needed, count, reasons,
                owners = result == 0 ? owners.Take((int)count).Select(owner => new
                {
                    processId = owner.Process.Id,
                    startFileTime = ((long)owner.Process.Start.dwHighDateTime << 32) | (uint)owner.Process.Start.dwLowDateTime,
                    type = owner.Type, status = owner.Status
                }).ToArray() : null
            };
        }
        finally
        {
            var ended = RmEndSession(session);
            if (ended != 0) Console.Error.WriteLine($"SQLITE-CRASH-OWNER-QUERY EndSession error={ended}");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UniqueProcess
    {
        public int Id;
        public FILETIME Start;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessInfo
    {
        public UniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Service;
        public int Type;
        public uint Status;
        public uint Session;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, uint flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint count, string[] paths,
        uint processes, UniqueProcess[]? applications, uint services, string[]? names);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmGetList(uint session, out uint needed, ref uint count,
        [In, Out] ProcessInfo[]? processes, ref uint reasons);
    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);
}
