using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace OrgExplorer.Workflow;

internal static class SqliteSetupNativeProbe
{
    private const string ExpectedHash = "6AD8E149F8CE3ED3716402B4B3A2268EBBDC7B64391B5FAFED747E03BB1B9418";
    private const string ExpectedSourceId = "2026-07-24 19:02:57 bf7c7f30031888f4e796e429ab3978879485813aaca6f641c7b33e4e09459bcc";
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int VersionNumber();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr SourceId();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SystemError(IntPtr connection);

    public static WorkspaceSqliteNativeProbe Read(SqliteConnection connection)
    {
        WorkspaceSqliteNativeModule? identity = null;
        WorkspaceSqliteNativeProbe Unavailable(string reason) => new("Unavailable", null, identity, reason);
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return Unavailable("The approved native module is Windows x64 only.");
        var database = connection.Handle;
        if (database is null || database.IsInvalid || database.IsClosed)
            return Unavailable("No live public connection Handle is available.");
        var retained = false;
        try
        {
            database.DangerousAddRef(ref retained);
            var provider = SQLitePCL.raw.GetNativeLibraryName();
            if (provider is not ("e_sqlite3" or "e_sqlite3.dll"))
                return Unavailable("The active provider does not identify the approved native library.");
            using var process = Process.GetCurrentProcess();
            var modules = process.Modules.Cast<ProcessModule>().Where(module =>
                module.ModuleName.Equals("e_sqlite3.dll", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (modules.Length != 1 || modules[0].BaseAddress == IntPtr.Zero)
                return Unavailable("The active provider module is absent or ambiguous.");
            var module = modules[0];
            using var file = new FileStream(module.FileName, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var hash = Convert.ToHexString(SHA256.HashData(file));
            file.Position = 0x3c;
            using var metadata = new BinaryReader(file);
            var header = metadata.ReadInt32();
            if (header < 0 || header > file.Length - 26)
                return Unavailable("The loaded module file has invalid PE metadata.");
            file.Position = header;
            var signature = metadata.ReadUInt32();
            var machine = metadata.ReadUInt16();
            file.Position = header + 24;
            var format = metadata.ReadUInt16();
            identity = new(provider, module.FileName, hash, $"machine=0x{machine:X4}; format=0x{format:X4}");
            if (signature != 0x00004550 || machine != 0x8664 || format != 0x20b || hash != ExpectedHash)
                return Unavailable("The loaded module file does not match the approved x64 SQLite asset.");

            // BaseAddress is the already-loaded Windows HMODULE. Never load or free another library.
            if (!NativeLibrary.TryGetExport(module.BaseAddress, "sqlite3_libversion_number", out var versionAddress) ||
                !NativeLibrary.TryGetExport(module.BaseAddress, "sqlite3_sourceid", out var sourceAddress) ||
                !NativeLibrary.TryGetExport(module.BaseAddress, "sqlite3_system_errno", out var errorAddress))
                return Unavailable("The verified loaded module lacks an expected public export.");
            var version = Marshal.GetDelegateForFunctionPointer<VersionNumber>(versionAddress)();
            var source = Marshal.PtrToStringUTF8(Marshal.GetDelegateForFunctionPointer<SourceId>(sourceAddress)());
            identity = identity with { Version = $"{version / 1_000_000}.{version / 1_000 % 1_000}.{version % 1_000}", SourceId = source };
            if (version != 3_053_004 || source != ExpectedSourceId)
                return Unavailable("The loaded module's read-only version/source identity differs from the approved asset.");
            var errno = Marshal.GetDelegateForFunctionPointer<SystemError>(errorAddress)(database.DangerousGetHandle());
            return new(errno == 0 ? "UnknownZero" : "Reported", errno, identity,
                "SQLite-reported stored iSysErrno before connection disposal; may be stale relative to the original Win32 call. No failed-file or holder claim.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or
            InvalidOperationException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return Unavailable($"Native probe failed: type={error.GetType().Name}; hresult=0x{error.HResult:X8}.");
        }
        finally
        {
            if (retained) database.DangerousRelease();
            GC.KeepAlive(connection);
        }
    }
}
