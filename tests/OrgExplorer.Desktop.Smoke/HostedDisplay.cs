using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDeviceNative
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayMonitorNative
    {
        public int Size;
        public ResizeRect Monitor, WorkArea;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayModeNative
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

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayPoint { public int X, Y; }

    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDeviceNative result, uint flags);
    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsExW", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string device, int index, ref DisplayModeNative mode, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    private static extern bool GetDisplayMonitorInfo(IntPtr monitor, ref DisplayMonitorNative info);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(DisplayPoint point, uint flags);
    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettings(string device, ref DisplayModeNative mode, IntPtr window, uint flags, IntPtr parameters);

    private static void AssertDisplayInterop()
    {
        if (Marshal.SizeOf<DisplayDeviceNative>() != 840 || Marshal.SizeOf<DisplayMonitorNative>() != 104 ||
            Marshal.SizeOf<DisplayModeNative>() != 220 ||
            Marshal.OffsetOf<DisplayModeNative>(nameof(DisplayModeNative.Fields)).ToInt32() != 72 ||
            Marshal.OffsetOf<DisplayModeNative>(nameof(DisplayModeNative.Width)).ToInt32() != 172 ||
            Marshal.OffsetOf<DisplayModeNative>(nameof(DisplayModeNative.Frequency)).ToInt32() != 184)
            throw new InvalidOperationException("Native display structures do not match the Windows SDK ABI; no display operation is safe.");
    }

    private sealed class WindowsHostedDisplay : IHostedDisplay
    {
        private readonly Dictionary<HostedDisplayMode, DisplayModeNative> modes = [];

        private static DisplayModeNative NewMode() => new() { Size = (ushort)Marshal.SizeOf<DisplayModeNative>(), DriverExtra = 0 };
        private static HostedDisplayMode Describe(DisplayModeNative mode) =>
            new((int)mode.Width, (int)mode.Height, (int)mode.BitsPerPixel, (int)mode.Frequency);

        public HostedDisplayState Read()
        {
            AssertDisplayInterop();
            var monitor = MonitorFromPoint(new DisplayPoint { X = 0, Y = 0 }, 2);
            var info = new DisplayMonitorNative { Size = Marshal.SizeOf<DisplayMonitorNative>() };
            if (monitor == IntPtr.Zero || !GetDisplayMonitorInfo(monitor, ref info) || string.IsNullOrWhiteSpace(info.Device))
                throw new InvalidOperationException("Cannot identify the actual primary display monitor/device.");
            var current = NewMode();
            if (!EnumDisplaySettings(info.Device, -1, ref current, 0) ||
                GetDpiForMonitor(monitor, 0, out var x, out var y) != 0 || x != y)
                throw new InvalidOperationException("Cannot read actual current display mode and effective DPI.");
            return new(info.Device, Describe(current), info.Monitor.Right - info.Monitor.Left,
                info.Monitor.Bottom - info.Monitor.Top, info.WorkArea.Right - info.WorkArea.Left,
                info.WorkArea.Bottom - info.WorkArea.Top, (int)x, GetSystemMetrics(59), GetSystemMetrics(60));
        }

        public IReadOnlyList<HostedDisplayMode> AdvertisedModes(string device)
        {
            var found = false;
            var count = 0;
            for (; count < 16; count++)
            {
                var adapter = new DisplayDeviceNative { Size = Marshal.SizeOf<DisplayDeviceNative>() };
                if (!EnumDisplayDevices(null, (uint)count, ref adapter, 0)) break;
                if (adapter.Name.Equals(device, StringComparison.OrdinalIgnoreCase) && (adapter.Flags & 5) == 5)
                {
                    found = true;
                    Console.WriteLine($"Hosted primary display: {adapter.Name}; {adapter.Description}; flags=0x{adapter.Flags:X}.");
                }
            }
            if (!found || count == 16)
                throw new InvalidOperationException("Actual primary display device was not completely enumerated; no mode will be applied.");
            modes.Clear();
            for (var index = 0; index < 256; index++)
            {
                var mode = NewMode();
                if (!EnumDisplaySettings(device, index, ref mode, 0)) return modes.Keys.ToArray();
                modes.TryAdd(Describe(mode), mode);
            }
            throw new InvalidOperationException("Advertised display modes exceeded the bounded enumeration; no mode will be applied.");
        }

        public int Test(string device, HostedDisplayMode mode) => Change(device, mode, 2);
        public int Apply(string device, HostedDisplayMode mode) => Change(device, mode, 0);

        private int Change(string device, HostedDisplayMode requested, uint flags)
        {
            if (!modes.TryGetValue(requested, out var mode))
                throw new InvalidOperationException("Display mode was not advertised; synthetic mode changes are forbidden.");
            mode.DriverExtra = 0;
            mode.Fields &= 0x7C0000; // Resolution/color/frequency only; never change position or primary-device ownership.
            if ((mode.Fields & 0x180000) != 0x180000)
                throw new InvalidOperationException("Advertised display mode lacks valid width/height fields.");
            return ChangeDisplaySettings(device, ref mode, IntPtr.Zero, flags, IntPtr.Zero);
        }
    }

    private static int ConfigureDisplay(string command)
    {
        var directory = Environment.GetEnvironmentVariable("DESKTOP_RESULTS_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw new InvalidOperationException("Display verification requires an explicit absolute results directory.");
        var results = Path.GetFullPath(directory);
        Directory.CreateDirectory(results);
        var display = new WindowsHostedDisplay();
        HostedDisplayState state;
        if (command == "--prepare-high-resolution")
            state = HostedDisplayPreparation.Prepare(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"),
                Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT"), Environment.GetEnvironmentVariable("RUNNER_OS"),
                display, Await);
        else
        {
            state = display.Read();
            if (command == "--verify-high-resolution") HostedDisplayPreparation.Verify(state);
            else HostedDisplayPreparation.VerifyOrdinary(state);
        }
        File.WriteAllText(Path.Combine(results, "display.json"),
            JsonSerializer.Serialize(new { schemaVersion = 1, command, state }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS: {command}: {state}. Dynamic preparation is confined to the disposable hosted VM; no registry mode was written.");
        return 0;
    }
}
