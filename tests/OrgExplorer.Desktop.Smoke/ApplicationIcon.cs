using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OrgExplorer.TestFixtures;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void CaptureWindowIconEvidence(AutomationElement window, string executable, string state)
    {
        var handle = (IntPtr)window.Current.NativeWindowHandle;
        using var input = new MemoryStream(NativeIconResource.ReadExecutableIcon(executable), false);
        var expected = new IconBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource Read(uint kind)
        {
            if (SendIconMessage(handle, 0x7f, (IntPtr)kind, IntPtr.Zero, 2, 1000, out var icon) == IntPtr.Zero ||
                icon == IntPtr.Zero)
                throw new InvalidOperationException("The real WPF window did not expose its small/large native icon.");
            var actual = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var frame = expected.Frames.SingleOrDefault(frame =>
                frame.PixelWidth == actual.PixelWidth && frame.PixelHeight == actual.PixelHeight)
                ?? throw new InvalidOperationException("The actual window icon has no matching native C1 frame.");
            byte[] Pixels(BitmapSource source)
            {
                var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
                var bytes = new byte[converted.PixelWidth * converted.PixelHeight * 4];
                converted.CopyPixels(bytes, converted.PixelWidth * 4, 0);
                return bytes;
            }
            var actualPixels = Pixels(actual);
            var expectedPixels = Pixels(frame);
            if (actualPixels.Where((value, index) => Math.Abs(value - expectedPixels[index]) > 2).Any())
                throw new InvalidOperationException("The actual window/taskbar HICON differs from the produced C1 frame.");
            actual.Freeze();
            return actual;
        }
        var small = Read(0);
        var large = Read(1);
        var path = Path.Combine(Path.GetDirectoryName(diagnostics)!,
            Path.GetFileNameWithoutExtension(diagnostics) + "-native-icons-" + state + "-" + window.Current.ProcessId);
        static void Save(BitmapSource image, string path)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var output = new FileStream(path, FileMode.CreateNew);
            encoder.Save(output);
        }
        Save(small, path + "-small.png");
        Save(large, path + "-large.png");
        var plate = new DrawingVisual();
        using (var context = plate.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(243, 244, 246)), null, new Rect(0, 0, 128, 96));
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(32, 33, 36)), null, new Rect(128, 0, 128, 96));
            foreach (var x in new[] { 0, 128 })
            {
                context.DrawImage(small, new Rect(x + 16, 24, small.PixelWidth, small.PixelHeight));
                context.DrawImage(large, new Rect(x + 64, 24, large.PixelWidth, large.PixelHeight));
            }
        }
        var surroundings = new RenderTargetBitmap(256, 96, 96, 96, PixelFormats.Pbgra32);
        surroundings.Render(plate);
        Save(surroundings, path + "-light-dark.png");
        using var application = File.OpenRead(executable);
        File.WriteAllText(path + ".json", JsonSerializer.Serialize(new
        {
            state,
            revision = Environment.GetEnvironmentVariable("GITHUB_SHA"),
            runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
            runAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
            executable,
            executableSha256 = Convert.ToHexString(SHA256.HashData(application)),
            small = new { width = small.PixelWidth, height = small.PixelHeight },
            large = new { width = large.PixelWidth, height = large.PixelHeight },
            source = "Actual live WPF WM_GETICON pixels, unscaled on light/dark plates. Not a taskbar/Start screenshot or visual acceptance."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static extern IntPtr SendIconMessage(IntPtr window, uint message, IntPtr kind, IntPtr parameter,
        uint flags, uint timeout, out IntPtr icon);
}
