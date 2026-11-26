using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;

public sealed class OwnedCleanupTrace : IDisposable
{
    private sealed class Owned
    {
        public int Id;
        public int Parent;
        public long Start;
        public bool Active = true;
        public string LastImage = "";
    }
    private readonly object gate = new object();
    private readonly Dictionary<int, Owned> owned = new Dictionary<int, Owned>();
    private readonly string output;
    private readonly string scope;
    private readonly ManagementEventWatcher starts;
    private readonly ManagementEventWatcher stops;
    private readonly Timer timer;
    private readonly bool observeModules;
    private bool disposed;

    public OwnedCleanupTrace(int root, string scope, string output, bool observeModules)
    {
        this.observeModules = observeModules;
        this.scope = Path.GetFullPath(scope).TrimEnd('\\') + "\\";
        this.output = output;
        using (var process = Process.GetProcessById(root))
            owned[root] = new Owned { Id = root, Start = process.StartTime.ToUniversalTime().ToFileTimeUtc() };
        Log("trace-start", root, 0, owned[root].Start, this.scope);
        starts = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace"));
        stops = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_ProcessStopTrace"));
        starts.EventArrived += OnStart;
        stops.EventArrived += OnStop;
        starts.Start();
        stops.Start();
        timer = new Timer(_ => Snapshot("sample"), null, observeModules ? 0 : Timeout.Infinite, 250);
    }

    private void Log(string kind, int pid, int parent, long start, string detail)
    {
        lock (gate)
            File.AppendAllText(output, DateTimeOffset.UtcNow.ToString("O") + "\t" + kind + "\t" +
                pid + "\t" + parent + "\t" + start + "\t" + detail.Replace('\r', ' ').Replace('\n', ' ') + "\n");
    }

    public void Launched(int pid, int parent)
    {
        lock (gate)
        {
            if (!owned.ContainsKey(parent)) throw new InvalidOperationException("Unverified process parent.");
            if (owned.ContainsKey(pid) && owned[pid].Active) return;
            var lifetime = new Owned { Id = pid, Parent = parent };
            try
            {
                using (var process = Process.GetProcessById(pid))
                    lifetime.Start = process.StartTime.ToUniversalTime().ToFileTimeUtc();
            }
            catch (ArgumentException) { lifetime.Active = false; }
            catch (Win32Exception error) { Log("start-unavailable", pid, parent, 0, error.NativeErrorCode.ToString()); }
            owned[pid] = lifetime;
            Log("process-start", pid, parent, lifetime.Start, "verified-parent");
        }
    }

    private void OnStart(object sender, EventArrivedEventArgs args)
    {
        var pid = Convert.ToInt32(args.NewEvent["ProcessID"]);
        var parent = Convert.ToInt32(args.NewEvent["ParentProcessID"]);
        lock (gate)
        {
            if (disposed || !owned.ContainsKey(parent) || !owned[parent].Active) return;
            Launched(pid, parent);
        }
    }

    private void OnStop(object sender, EventArrivedEventArgs args)
    {
        var pid = Convert.ToInt32(args.NewEvent["ProcessID"]);
        lock (gate)
        {
            if (disposed || !owned.TryGetValue(pid, out var lifetime)) return;
            lifetime.Active = false;
            Log("process-stop-event", pid, lifetime.Parent, lifetime.Start,
                "exit=" + Convert.ToString(args.NewEvent["ExitStatus"]) +
                ";eventFileTime=" + Convert.ToString(args.NewEvent["TIME_CREATED"]));
        }
    }

    public void Snapshot(string boundary)
    {
        lock (gate)
        {
            if (disposed || !observeModules) return;
            foreach (var lifetime in owned.Values.Where(p => p.Active).ToArray())
            {
                try
                {
                    using (var process = Process.GetProcessById(lifetime.Id))
                    {
                        var start = process.StartTime.ToUniversalTime().ToFileTimeUtc();
                        if (lifetime.Start == 0) lifetime.Start = start;
                        if (lifetime.Start != start)
                        {
                            Log("pid-reused-not-observed", lifetime.Id, lifetime.Parent, lifetime.Start, boundary);
                            lifetime.Active = false;
                            continue;
                        }
                        var exited = process.HasExited;
                        var images = exited ? "" : string.Join(";", process.Modules.Cast<ProcessModule>()
                            .Where(m => m.FileName.StartsWith(scope, StringComparison.OrdinalIgnoreCase) &&
                                Path.GetFileName(m.FileName).Equals("e_sqlite3.dll", StringComparison.OrdinalIgnoreCase))
                            .Select(m => m.FileName));
                        if (images != lifetime.LastImage || boundary != "sample")
                            Log("image-snapshot", lifetime.Id, lifetime.Parent, start,
                                boundary + ";signalled=" + exited + ";images=" + images);
                        lifetime.LastImage = images;
                        if (exited)
                        {
                            lifetime.Active = false;
                            Log("process-signalled", lifetime.Id, lifetime.Parent, start, boundary);
                        }
                    }
                }
                catch (ArgumentException)
                {
                    lifetime.Active = false;
                    Log("process-unavailable", lifetime.Id, lifetime.Parent, lifetime.Start, boundary);
                }
                catch (InvalidOperationException error)
                {
                    Log("snapshot-unavailable", lifetime.Id, lifetime.Parent, lifetime.Start,
                        boundary + ";" + error.GetType().Name + ";hresult=" + error.HResult);
                }
                catch (Win32Exception error)
                {
                    Log("snapshot-unavailable", lifetime.Id, lifetime.Parent, lifetime.Start,
                        boundary + ";win32=" + error.NativeErrorCode);
                }
            }
        }
    }

    public void Boundary(string detail) { Log("boundary", 0, 0, 0, detail); Snapshot(detail); }

    public void Dispose()
    {
        timer.Dispose();
        Snapshot("observer-dispose");
        lock (gate) disposed = true;
        starts.Stop();
        stops.Stop();
        starts.Dispose();
        stops.Dispose();
        Log("trace-stop", 0, 0, 0, "All observer Process handles were scoped/disposed per snapshot; sampled/non-atomic.");
    }
}
