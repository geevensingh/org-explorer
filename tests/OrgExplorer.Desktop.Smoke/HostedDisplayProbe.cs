using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProbeDisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProbeMonitorInfo
    {
        public int Size;
        public ResizeRect Monitor, WorkArea;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProbeDisplayMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int PositionX, PositionY;
        public uint Orientation, FixedOutput;
        public short Color, Duplex, YResolution, TrueTypeOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Form;
        public ushort LogPixels;
        public uint BitsPerPixel, Width, Height, DisplayFlags, Frequency;
        public uint IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode)]
    private static extern bool EnumProbeDisplayDevices(string? device, uint index, ref ProbeDisplayDevice result, uint flags);

    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsExW", CharSet = CharSet.Unicode)]
    private static extern bool EnumProbeDisplaySettings(string device, int index, ref ProbeDisplayMode mode, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetProbeMonitorInfo(IntPtr monitor, ref ProbeMonitorInfo info);

    [DllImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW", CharSet = CharSet.Unicode)]
    private static extern int TestProbeDisplaySettings(string device, ref ProbeDisplayMode mode, IntPtr window, uint flags, IntPtr parameters);

    private static void AssertDisplayProbeInterop()
    {
        if (Marshal.SizeOf<ProbeDisplayDevice>() != 840 || Marshal.SizeOf<ProbeMonitorInfo>() != 104 ||
            Marshal.SizeOf<ProbeDisplayMode>() != 220 ||
            Marshal.OffsetOf<ProbeDisplayMode>(nameof(ProbeDisplayMode.Fields)).ToInt32() != 72 ||
            Marshal.OffsetOf<ProbeDisplayMode>(nameof(ProbeDisplayMode.Width)).ToInt32() != 172 ||
            Marshal.OffsetOf<ProbeDisplayMode>(nameof(ProbeDisplayMode.Frequency)).ToInt32() != 184)
            throw new InvalidOperationException("[DEBUG-hosted-display] Native Unicode display structures do not match the Windows SDK ABI; no driver probe is safe.");
    }

    private static ProbeDisplayMode NewProbeDisplayMode() =>
        new() { Size = (ushort)Marshal.SizeOf<ProbeDisplayMode>(), DriverExtra = 0 };

    private static string ProbeModeKey(ProbeDisplayMode mode) =>
        $"{mode.Width}x{mode.Height}; bpp={mode.BitsPerPixel}; Hz={mode.Frequency}; orientation={mode.Orientation}; flags=0x{mode.DisplayFlags:X}";

    private static ProbeMonitorInfo ReadProbeMonitor(IntPtr handle)
    {
        var monitor = MonitorFromWindow(handle, 2);
        var info = new ProbeMonitorInfo { Size = Marshal.SizeOf<ProbeMonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetProbeMonitorInfo(monitor, ref info) || string.IsNullOrWhiteSpace(info.Device))
            throw new InvalidOperationException("[DEBUG-hosted-display] Cannot identify the actual window monitor/device; no TEST request will be sent.");
        return info;
    }

    private static ProbeDisplayMode ReadProbeCurrentMode(string device)
    {
        var mode = NewProbeDisplayMode();
        if (!EnumProbeDisplaySettings(device, -1, ref mode, 0))
            throw new InvalidOperationException($"[DEBUG-hosted-display] ENUM_CURRENT_SETTINGS failed for {device}; no valid current-mode evidence.");
        return mode;
    }

    private static void ProbeHostedDisplay(AutomationElement window)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted" ||
            Environment.GetEnvironmentVariable("RUNNER_OS") != "Windows")
            throw new InvalidOperationException("[DEBUG-hosted-display] This temporary read-only probe is restricted to ephemeral GitHub-hosted Windows runners.");
        AssertDisplayProbeInterop();
        var timer = Stopwatch.StartNew();
        void CheckBudget()
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException("[DEBUG-hosted-display] Capability probe exceeded its 10-second checked budget; enumeration/TEST evidence is incomplete.");
        }
        var handle = (IntPtr)window.Current.NativeWindowHandle;
        var beforeMonitor = ReadProbeMonitor(handle);
        var before = ReadProbeCurrentMode(beforeMonitor.Device);
        var scale = DpiScale(window);
        Console.WriteLine($"[DEBUG-hosted-display] READ-ONLY: device={beforeMonitor.Device}; current={ProbeModeKey(before)}; " +
            $"position={before.PositionX},{before.PositionY}; dpi={scale * 96}; monitor={beforeMonitor.Monitor}; workArea={beforeMonitor.WorkArea}. " +
            "Enumeration flags=0 (monitor-compatible, current orientation); TEST flags=CDS_TEST(2) only; no apply, registry, reset or unsafe-mode flags.");
        try
        {
            var actualDeviceFound = false;
            var deviceCount = 0;
            for (; deviceCount < 16; deviceCount++)
            {
                CheckBudget();
                var device = new ProbeDisplayDevice { Size = Marshal.SizeOf<ProbeDisplayDevice>() };
                if (!EnumProbeDisplayDevices(null, (uint)deviceCount, ref device, 0)) break;
                var actual = device.Name.Equals(beforeMonitor.Device, StringComparison.OrdinalIgnoreCase);
                actualDeviceFound |= actual;
                Console.WriteLine($"[DEBUG-hosted-display] adapter[{deviceCount}]: name={device.Name}; description={device.Description}; flags=0x{device.Flags:X}; actualWindowDevice={actual}");
            }
            if (deviceCount == 16 || !actualDeviceFound)
                throw new InvalidOperationException("[DEBUG-hosted-display] Device enumeration incomplete or actual display was not advertised; no TEST request will be sent.");
            var modes = new List<ProbeDisplayMode>();
            for (var index = 0; index < 256; index++)
            {
                CheckBudget();
                var mode = NewProbeDisplayMode();
                if (!EnumProbeDisplaySettings(beforeMonitor.Device, index, ref mode, 0)) break;
                modes.Add(mode);
                Console.WriteLine($"[DEBUG-hosted-display] advertised[{index}]: {ProbeModeKey(mode)}; fields=0x{mode.Fields:X}; driverExtra={mode.DriverExtra}");
            }
            if (modes.Count is 0 or 256)
                throw new InvalidOperationException("[DEBUG-hosted-display] No modes returned or enumeration reached its 256-mode limit; capability evidence is incomplete.");
            var preferredWidth = (uint)Math.Ceiling(1920 * scale);
            var preferredHeight = (uint)Math.Ceiling(1080 * scale);
            var preferred = modes.FirstOrDefault(m => m.Width == preferredWidth && m.Height == preferredHeight &&
                m.BitsPerPixel == before.BitsPerPixel && m.Frequency == before.Frequency);
            var preferredAdvertised = preferred.Width != 0;
            if (!preferredAdvertised)
            {
                preferred = before;
                preferred.Width = preferredWidth;
                preferred.Height = preferredHeight;
                preferred.Fields |= 0x180000;
            }
            var smallest = modes.Where(m => m.Width >= Math.Ceiling(1280 * scale) && m.Height >= Math.Ceiling(1024 * scale) &&
                m.BitsPerPixel == before.BitsPerPixel).OrderBy(m => (ulong)m.Width * m.Height).FirstOrDefault();
            var roomy = modes.Where(m => m.Width >= Math.Ceiling(1440 * scale) && m.Height >= Math.Ceiling(1080 * scale) &&
                m.BitsPerPixel == before.BitsPerPixel).OrderBy(m => (ulong)m.Width * m.Height).FirstOrDefault();
            var largest = modes.Where(m => m.BitsPerPixel == before.BitsPerPixel)
                .OrderByDescending(m => (ulong)m.Width * m.Height).FirstOrDefault();
            var candidates = new[]
            {
                (Label: "current baseline", Mode: before, Advertised: modes.Any(m => ProbeModeKey(m) == ProbeModeKey(before))),
                (Label: "preferred 1920x1080 DIP", Mode: preferred, Advertised: preferredAdvertised),
                (Label: "smallest advertised >=1280x1024 DIP", Mode: smallest, Advertised: true),
                (Label: "roomy advertised >=1440x1080 DIP", Mode: roomy, Advertised: true),
                (Label: "largest advertised", Mode: largest, Advertised: true)
            }.Where(c => c.Mode.Width != 0).DistinctBy(c => ProbeModeKey(c.Mode)).Take(4).ToArray();
            foreach (var candidate in candidates)
            {
                CheckBudget();
                var mode = candidate.Mode;
                mode.Size = (ushort)Marshal.SizeOf<ProbeDisplayMode>();
                mode.DriverExtra = 0;
                mode.Fields &= 0x7C0000;
                if ((mode.Fields & 0x180000) != 0x180000)
                    throw new InvalidOperationException("[DEBUG-hosted-display] Candidate lacks valid width/height fields; no TEST request is safe.");
                // CDS_TEST only: this probe never applies or persists a display mode.
                var result = TestProbeDisplaySettings(beforeMonitor.Device, ref mode, IntPtr.Zero, 2, IntPtr.Zero);
                var meaning = result switch
                {
                    0 => "DISP_CHANGE_SUCCESSFUL (TEST accepted; not applied)",
                    1 => "DISP_CHANGE_RESTART (not usable without restart)",
                    -1 => "DISP_CHANGE_FAILED", -2 => "DISP_CHANGE_BADMODE", -3 => "DISP_CHANGE_NOTUPDATED",
                    -4 => "DISP_CHANGE_BADFLAGS", -5 => "DISP_CHANGE_BADPARAM", -6 => "DISP_CHANGE_BADDUALVIEW",
                    _ => "unknown driver result"
                };
                Console.WriteLine($"[DEBUG-hosted-display] TEST: {candidate.Label}; advertised={candidate.Advertised}; " +
                    $"{ProbeModeKey(mode)}; fields=0x{mode.Fields:X}; result={result} ({meaning})");
                CheckBudget();
            }
        }
        finally
        {
            var afterMonitor = ReadProbeMonitor(handle);
            var after = ReadProbeCurrentMode(beforeMonitor.Device);
            var unchanged = ProbeModeKey(before) == ProbeModeKey(after) && before.PositionX == after.PositionX &&
                before.PositionY == after.PositionY && beforeMonitor.Device == afterMonitor.Device &&
                beforeMonitor.Monitor.Equals(afterMonitor.Monitor) && beforeMonitor.WorkArea.Equals(afterMonitor.WorkArea);
            Console.WriteLine($"[DEBUG-hosted-display] FINISHED: elapsedMs={timer.Elapsed.TotalMilliseconds:F1}; unchanged={unchanged}; " +
                $"current={ProbeModeKey(after)}; {NativeResizeBounds(handle)}");
            if (!unchanged)
                throw new InvalidOperationException("[DEBUG-hosted-display] Current mode/monitor/work area changed during the read-only probe; no unchanged-display claim is valid.");
        }
    }
}
