using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void CaptureFailureEvidence(AutomationElement window, string state)
    {
        try { CaptureVisualEvidence(window, state); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or
            ElementNotAvailableException or InvalidOperationException or ArgumentException or COMException)
        {
            Console.Error.WriteLine($"Failure frame '{state}' could not be captured: {error}");
        }
    }

    private static void CaptureVisualEvidence(AutomationElement window, string state, bool captureMonitor = false)
    {
        if (!Regex.IsMatch(state, "^[a-z0-9]+(?:-[a-z0-9]+)*$"))
            throw new ArgumentException("Visual evidence requires a descriptive, path-safe state name.", nameof(state));
        var desktop = new Rect(GetSystemMetrics(76), GetSystemMetrics(77),
            GetSystemMetrics(78), GetSystemMetrics(79));
        var windowBounds = window.Current.BoundingRectangle;
        var bounds = Rect.Intersect(windowBounds, desktop);
        if (window.Current.IsOffscreen || bounds.IsEmpty || bounds.Width < 1 || bounds.Height < 1)
            throw new InvalidOperationException("Visual evidence requires a visible window on an unlocked interactive desktop.");
        if (captureMonitor)
        {
            var handle = (IntPtr)window.Current.NativeWindowHandle;
            var monitor = MonitorFromWindow(handle, 2);
            if (handle == IntPtr.Zero || monitor == IntPtr.Zero)
                throw new InvalidOperationException("Cannot identify the visible application's actual monitor.");
            var info = new ResizeMonitorInfo { Size = Marshal.SizeOf<ResizeMonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read the application monitor bounds.");
            var monitorBounds = new Rect(info.Monitor.Left, info.Monitor.Top,
                info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top);
            bounds = Rect.Intersect(monitorBounds, desktop);
            if (bounds.IsEmpty || bounds != monitorBounds)
                throw new InvalidOperationException("Visual evidence cannot capture the complete application monitor.");
        }
        var left = (int)Math.Floor(bounds.Left);
        var top = (int)Math.Floor(bounds.Top);
        var width = (int)Math.Ceiling(bounds.Right) - left;
        var height = (int)Math.Ceiling(bounds.Bottom) - top;
        var path = Path.Combine(Path.GetDirectoryName(diagnostics)!,
            Path.GetFileNameWithoutExtension(diagnostics) + "-" + state);
        if (File.Exists(path + ".png") || File.Exists(path + ".json"))
            throw new InvalidOperationException("Visual evidence must not overwrite an earlier observation: " + path);

        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read actual desktop pixels.");
        var memory = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            memory = CreateCompatibleDC(screen);
            if (memory == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            bitmap = CreateCompatibleBitmap(screen, width, height);
            if (bitmap == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            previous = SelectObject(memory, bitmap);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!BitBlt(memory, 0, 0, width, height, screen, left, top, 0x40CC0020))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Actual desktop pixel capture failed.");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(Imaging.CreateBitmapSourceFromHBitmap(
                bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions())));
            using (var output = File.Create(path + ".png")) encoder.Save(output);
        }
        finally
        {
            if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }

        using var process = Process.GetProcessById(window.Current.ProcessId);
        var executable = process.MainModule?.FileName
            ?? throw new InvalidOperationException("Cannot identify the application captured in visual evidence.");
        using var input = File.OpenRead(executable);
        File.WriteAllText(path + ".json", JsonSerializer.Serialize(new
        {
            state,
            scope = captureMonitor ? "application-monitor" : "application-window",
            capturedAt = DateTimeOffset.UtcNow,
            revision = Environment.GetEnvironmentVariable("GITHUB_SHA"),
            runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
            runAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
            executable,
            executableSha256 = Convert.ToHexString(SHA256.HashData(input)),
            dpi = GetDpiForWindow((IntPtr)window.Current.NativeWindowHandle),
            windowBounds = new { windowBounds.X, windowBounds.Y, windowBounds.Width, windowBounds.Height },
            capturedBounds = new { left, top, width, height },
            shellEvidenceLimit = captureMonitor
                ? "Actual monitor pixels include the taskbar region, not a reconstructed icon plate. Review visibility and the app button in the PNG. Start/setup surfaces were not opened or observed by this capture."
                : null,
            source = "Actual desktop pixels; not an offscreen control render or visual acceptance."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);
}
