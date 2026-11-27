using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace OrgExplorer.Storage.ProcessFixture;

public sealed class SqliteStage2Capture : IDisposable
{
    private const int HeaderSize = 128, RecordSize = 256, Capacity = 2048;
    private const long MapSize = HeaderSize + RecordSize * Capacity;
    private readonly MemoryMappedFile parentMap, childMap;
    private readonly MemoryMappedViewAccessor parentView, childView;
    private readonly EventWaitHandle stop;
    private readonly Process collector;
    private readonly IntPtr library;
    private readonly Mark marker;
    private readonly string results;
    private readonly Dictionary<string, string?> previous = [];
    private readonly List<IDisposable> owned = [];
    private readonly Task<string> collectorOutput, collectorError;
    private readonly int parentPid = Environment.ProcessId;
    private readonly string nonce = Guid.NewGuid().ToString("N");
    private bool disposed;
    private int? expectedChild;
    private object? firstRevision;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private delegate int Bind(int role, uint parent, uint self,
        [MarshalAs(UnmanagedType.LPWStr)] string db, [MarshalAs(UnmanagedType.LPWStr)] string wal,
        [MarshalAs(UnmanagedType.LPWStr)] string shm, [MarshalAs(UnmanagedType.LPWStr)] string map);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Mark(uint phase, ulong first, ulong second);

    public static SqliteStage2Capture? Start(string workspace, string runtimeConfig)
    {
        if (Environment.GetEnvironmentVariable("ORGEXPLORER_SQLITE_STAGE2") != "1") return null;
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted")
            throw new InvalidOperationException("Stage2 fixture capture requires the isolated hosted Windows route.");
        return new(workspace, runtimeConfig);
    }

    private SqliteStage2Capture(string workspace, string runtimeConfig)
    {
        results = Path.Combine(Required("ORGEXPLORER_SQLITE_STAGE2_RESULTS"), nonce);
        Directory.CreateDirectory(results);
        try
        {
            var parentName = @"Local\OrgExplorer-SQLite-Stage2-" + nonce + "-parent";
            var childName = @"Local\OrgExplorer-SQLite-Stage2-" + nonce + "-child";
            parentMap = MemoryMappedFile.CreateNew(parentName, MapSize);
            owned.Add(parentMap);
            childMap = MemoryMappedFile.CreateNew(childName, MapSize);
            owned.Add(childMap);
            parentView = parentMap.CreateViewAccessor();
            owned.Add(parentView);
            childView = childMap.CreateViewAccessor();
            owned.Add(childView);
            Initialize(parentView, 1);
            Initialize(childView, 2);
            stop = new EventWaitHandle(false, EventResetMode.ManualReset,
                @"Local\OrgExplorer-SQLite-Stage2-" + nonce + "-stop");
            owned.Add(stop);
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in new[] { "exec", "--runtimeconfig", runtimeConfig,
                typeof(StorageCrash).Assembly.Location, "stage2-collect", parentName, childName,
                @"Local\OrgExplorer-SQLite-Stage2-" + nonce + "-stop", results })
                start.ArgumentList.Add(argument);
            collector = Process.Start(start) ?? throw new IOException("The owned Stage2 collector did not start.");
            owned.Add(collector);
            collectorOutput = collector.StandardOutput.ReadToEndAsync();
            collectorError = collector.StandardError.ReadToEndAsync();
            Set("ORGEXPLORER_SQLITE_STAGE2_PARENT_PID", parentPid.ToString());
            Set("ORGEXPLORER_SQLITE_STAGE2_CHILD_MAP", childName);
            Set("ORGEXPLORER_SQLITE_STAGE2_DB", Path.Combine(workspace, "workspace.sqlite"));
            Set("ORGEXPLORER_SQLITE_STAGE2_WAL", Path.Combine(workspace, "workspace.sqlite-wal"));
            Set("ORGEXPLORER_SQLITE_STAGE2_SHM", Path.Combine(workspace, "workspace.sqlite-shm"));
            Set("ORGEXPLORER_SQLITE_STAGE2_NONCE", nonce);
            library = LoadVerifiedNative();
            marker = Marshal.GetDelegateForFunctionPointer<Mark>(NativeLibrary.GetExport(library, "oe_stage2_mark"));
            var bind = Marshal.GetDelegateForFunctionPointer<Bind>(NativeLibrary.GetExport(library, "oe_stage2_bind"));
            var rc = bind(1, (uint)parentPid, (uint)parentPid, Required("ORGEXPLORER_SQLITE_STAGE2_DB"),
                Required("ORGEXPLORER_SQLITE_STAGE2_WAL"), Required("ORGEXPLORER_SQLITE_STAGE2_SHM"), parentName);
            if (rc != 0) throw new IOException($"Stage2 native parent binding refused: {rc}.");
            Phase(1);
        }
        catch
        {
            if (collector is not null && !collector.HasExited)
            {
                collector.Kill(entireProcessTree: true);
                collector.WaitForExit(5000);
            }
            Cleanup();
            throw;
        }
    }

    private void Initialize(MemoryMappedViewAccessor view, int role)
    {
        view.Write(0, 0x3245544Fu);
        view.Write(4, 1u);
        view.Write(8, (uint)RecordSize);
        view.Write(12, (uint)Capacity);
        view.Write(16, (uint)parentPid);
        view.Write(24, (uint)role);
        view.Write(28, 1u);
    }

    private void Set(string name, string value)
    {
        previous[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public static void BindChild(string workspace)
    {
        if (Environment.GetEnvironmentVariable("ORGEXPLORER_SQLITE_STAGE2") != "1") return;
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted" ||
            Path.Combine(workspace, "workspace.sqlite") != Required("ORGEXPLORER_SQLITE_STAGE2_DB"))
            throw new IOException("Stage2 child scope does not match the original fixture.");
        var module = LoadVerifiedNative();
        var bind = Marshal.GetDelegateForFunctionPointer<Bind>(NativeLibrary.GetExport(module, "oe_stage2_bind"));
        var rc = bind(2, uint.Parse(Required("ORGEXPLORER_SQLITE_STAGE2_PARENT_PID")), (uint)Environment.ProcessId,
            Required("ORGEXPLORER_SQLITE_STAGE2_DB"), Required("ORGEXPLORER_SQLITE_STAGE2_WAL"),
            Required("ORGEXPLORER_SQLITE_STAGE2_SHM"), Required("ORGEXPLORER_SQLITE_STAGE2_CHILD_MAP"));
        if (rc != 0) throw new IOException($"Stage2 native child binding refused: {rc}.");
        var mark = Marshal.GetDelegateForFunctionPointer<Mark>(NativeLibrary.GetExport(module, "oe_stage2_mark"));
        mark(2, 0, 0);
    }

    private static IntPtr LoadVerifiedNative()
    {
        var path = Required("ORGEXPLORER_SQLITE_STAGE2_NATIVE_DLL");
        var expected = Required("ORGEXPLORER_SQLITE_STAGE2_NATIVE_SHA256");
        using var file = File.OpenRead(path);
        if (!Convert.ToHexString(SHA256.HashData(file)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Stage2 native artifact hash differs from the approved build manifest.");
        var module = NativeLibrary.Load(path);
        if (GetModuleHandle("e_sqlite3.dll") != module)
        {
            NativeLibrary.Free(module);
            throw new IOException("Stage2 loaded artifact is not the provider's e_sqlite3 module.");
        }
        return module;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string name);

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new IOException($"Stage2 configuration is missing {name}.");

    public void Phase(uint phase) => marker(phase, 0, 0);
    public void Child(int pid) => expectedChild = pid;
    public void Revision(string? revision, string phase, string outcome, int outstanding)
    {
        firstRevision = new { revision, phase, outcome, outstanding };
        Phase(6);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            Phase(7);
            stop.Set();
            if (!collector.WaitForExit(5000))
            {
                collector.Kill(entireProcessTree: true);
                collector.WaitForExit();
                File.WriteAllText(Path.Combine(results, "collector-timeout.json"), "{\"complete\":false}");
            }
            var summary = new
            {
                schemaVersion = 1, diagnosticOnly = true, normalCiAttested = false, nonce,
                candidate = Environment.GetEnvironmentVariable("GITHUB_SHA"),
                runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
                runAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
                nativeSha256 = Environment.GetEnvironmentVariable("ORGEXPLORER_SQLITE_STAGE2_NATIVE_SHA256"),
                parentPid, expectedChild,
                registeredParent = parentView.ReadUInt32(20), registeredChild = childView.ReadUInt32(20),
                firstRevision, collectorPid = collector.Id, collectorExitCode = collector.ExitCode,
                complete = collector.ExitCode == 0 && parentView.ReadUInt32(20) == parentPid &&
                    expectedChild is { } child && childView.ReadUInt32(20) == child
            };
            File.WriteAllText(Path.Combine(results, "fixture-binding.json"), JsonSerializer.Serialize(summary));
            Console.WriteLine("SQLITE-STAGE2-FIXTURE " + JsonSerializer.Serialize(summary));
            var errors = collectorError.GetAwaiter().GetResult();
            var output = collectorOutput.GetAwaiter().GetResult();
            if (errors.Length != 0 || output.Length != 0)
                File.WriteAllText(Path.Combine(results, "collector.log"), output + errors);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Capture failure cannot replace the original strict recovery assertion.
            Console.Error.WriteLine($"SQLITE-STAGE2-INCOMPLETE {error.GetType().Name}: {error.Message}");
        }
        finally
        {
            Cleanup();
        }
    }

    private void Cleanup()
    {
        foreach (var item in previous) Environment.SetEnvironmentVariable(item.Key, item.Value);
        for (var index = owned.Count - 1; index >= 0; index--)
        {
            try { owned[index].Dispose(); }
            catch (Exception error) when (error is IOException or InvalidOperationException or
                System.ComponentModel.Win32Exception)
            {
                Console.Error.WriteLine($"SQLITE-STAGE2-CLEANUP-INCOMPLETE {error.GetType().Name}: {error.Message}");
            }
        }
        if (library != IntPtr.Zero) NativeLibrary.Free(library);
    }

    public static int Collect(string parentName, string childName, string stopName, string results)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Stage2 mapping collection requires Windows.");
        using var parent = MemoryMappedFile.OpenExisting(parentName);
        using var child = MemoryMappedFile.OpenExisting(childName);
        using var parentView = parent.CreateViewAccessor();
        using var childView = child.CreateViewAccessor();
        using var stop = EventWaitHandle.OpenExisting(stopName);
        var finished = stop.WaitOne(TimeSpan.FromSeconds(60));
        Directory.CreateDirectory(results);
        var records = new List<object>(Capacity * 2);
        var states = new List<object>();
        var complete = finished;
        Read(parentView, 1);
        Read(childView, 2);
        File.WriteAllText(Path.Combine(results, "native-records.json"), JsonSerializer.Serialize(records));
        File.WriteAllText(Path.Combine(results, "native-verdict.json"),
            JsonSerializer.Serialize(new { schemaVersion = 1, diagnosticOnly = true, complete, states }));
        return complete ? 0 : 2;

        void Read(MemoryMappedViewAccessor view, int role)
        {
            if (view.ReadUInt32(0) != 0x3245544F || view.ReadUInt32(4) != 1 ||
                view.ReadUInt32(8) != RecordSize || view.ReadUInt32(12) != Capacity)
                throw new InvalidDataException("Stage2 mapping schema mismatch.");
            var pid = view.ReadUInt32(20);
            var reserved = view.ReadInt64(32);
            var committed = view.ReadInt64(40);
            var overflow = view.ReadInt64(48);
            var errors = view.ReadInt64(56);
            var unbound = view.ReadInt64(64);
            var dropped = view.ReadInt64(72);
            var expired = view.ReadUInt32(80);
            var configurationState = view.ReadUInt32(84);
            if (pid == 0 || reserved <= 0 || reserved > Capacity || reserved != committed ||
                overflow != 0 || errors != 0 || unbound != 0 || dropped != 0 || expired != 0 ||
                configurationState != 1 || view.ReadUInt32(24) != role) complete = false;
            states.Add(new { role, pid, reserved, committed, overflow, errors, unbound, dropped,
                expired, configurationState });
            for (long index = 0; index < Math.Min(Math.Max(reserved, 0), Capacity); index++)
            {
                var offset = HeaderSize + index * RecordSize;
                var published = view.ReadUInt64(offset);
                if (published != (ulong)index + 1) { complete = false; continue; }
                var recordedPid = view.ReadUInt32(offset + 16);
                if (recordedPid != pid) { complete = false; continue; }
                records.Add(new
                {
                    role, sequence = published, qpc = view.ReadUInt64(offset + 8), pid = recordedPid,
                    tid = view.ReadUInt32(offset + 20), code = view.ReadUInt32(offset + 24),
                    phase = view.ReadUInt32(offset + 28), token = view.ReadUInt64(offset + 32),
                    related = view.ReadUInt64(offset + 40), arg0 = view.ReadUInt64(offset + 48),
                    arg1 = view.ReadUInt64(offset + 56), arg2 = view.ReadUInt64(offset + 64),
                    rc = view.ReadInt32(offset + 72), winError = view.ReadUInt32(offset + 76),
                    volume = view.ReadUInt64(offset + 80).ToString("X16"),
                    fileId = $"{view.ReadUInt64(offset + 96):X16}{view.ReadUInt64(offset + 88):X16}",
                    flags = view.ReadUInt64(offset + 104)
                });
            }
        }
    }
}
