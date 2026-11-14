using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void CreatePhotoFixture(string path)
    {
        byte[] pixels = [255, 0, 255, 255, 255, 0, 255, 255, 255, 0, 255, 255, 255, 0, 255, 255];
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8)));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static void CreateJpegFixture(string path)
    {
        byte[] pixels = [255, 0, 255, 255, 255, 0, 255, 255, 255, 0, 255, 255, 255, 0, 255, 255];
        var encoder = new JpegBitmapEncoder { QualityLevel = 95 };
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8)));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static void AssertPhotoVisible(AutomationElement window)
    {
        window.SetFocus();
        var scroll = (ScrollPattern)Find(window, "ProfileScroll").GetCurrentPattern(ScrollPattern.Pattern);
        if (scroll.Current.VerticallyScrollable) scroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
        Await(() =>
        {
            var image = Find(window, "PersonPhoto");
            var bounds = image.Current.BoundingRectangle;
            if (image.Current.IsOffscreen || bounds.IsEmpty || bounds.Width < 1 || bounds.Height < 1) return false;
            var dc = GetDC(IntPtr.Zero);
            try
            {
                var color = GetPixel(dc, (int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2));
                if (color == uint.MaxValue)
                    throw new InvalidOperationException("Actual UI pixels are unavailable. Desktop smoke requires an interactive, unlocked Windows desktop; image assertions cannot be skipped.");
                return (color & 0xff) >= 240 && ((color >> 8) & 0xff) <= 20 && ((color >> 16) & 0xff) >= 240;
            }
            finally { ReleaseDC(IntPtr.Zero, dc); }
        });
    }

    private static void ExercisePhotoPresentation(AutomationElement window, string root)
    {
        var producer = Path.Combine(root, "photo-updates");
        Directory.CreateDirectory(producer);
        var path = Path.Combine(producer, "update.json");
        void Preview(string payload, int batch)
        {
            File.WriteAllText(path, $$"""
                {"schemaVersion":1,"batchId":"10000000-0000-4000-8000-{{batch:D12}}",
                "observedAt":"2026-11-02T17:00:00Z","people":[{"email":"avery@example.com"{{payload}}}]}
                """);
            Set(window, "ImportPath", path);
            Click(window, "PreviewImport");
        }

        CreateJpegFixture(Path.Combine(producer, "avery.jpg"));
        Preview(",\"photoPath\":\"avery.jpg\"", 25);
        Await(() => Find(window, "ApplyImport").Current.IsEnabled);
        Click(window, "ApplyImport");
        AssertPhotoVisible(window);
        if (!Find(window, "PersonPhoto").Current.Name.Contains("image/jpeg"))
            throw new InvalidOperationException("Actual JPEG did not display on the profile.");
        Click(window, "ClearPhoto");
        Await(() => Find(window, "PhotoPlaceholder").Current.Name == "Photo unavailable");
        Await(() => Find(window, "ProfileTimeline").Current.Name.Contains("Photo:") &&
                    Find(window, "ProfileTimeline").Current.Name.Contains(".jpg → (clear)") &&
                    Find(window, "ProfileTimeline").Current.Name.Contains("Local edit"));
        if (!Find(window, "ProfileTimeline").Current.Name.Contains("Imported"))
            throw new InvalidOperationException("Photo timeline lost imported versus local-edit provenance.");
        ExercisePhotoFitting(window, producer);
        Console.WriteLine("PASS: actual copied PNG/JPEG pixels, local clear/placeholder and photo timeline.");
    }

    private static void ExercisePhotoFitting(AutomationElement window, string producer)
    {
        System.Windows.Rect? photoArea = null;
        foreach (var (width, height, expectedWidth, expectedHeight) in new[]
        {
            (300, 300, 144, 144),
            (300, 150, 144, 72),
            (150, 300, 72, 144),
            (72, 72, 144, 144),
            (72, 36, 144, 72)
        })
        {
            var path = Path.Combine(producer, $"edges-{width}x{height}.png");
            CreateEdgedPhotoFixture(path, width, height);
            ChoosePhotoFixture(window, path);
            Await(() => Find(window, "PersonPhoto").Current.Name.Contains($"{width} × {height} pixels"));
            photoArea = AssertPhotoFit(window, expectedWidth, expectedHeight, $"{width}x{height}", photoArea);
            NavigateToManager(window, "morgan@example.com");
            Await(() => !Find(window, "PhotoPlaceholder").Current.IsOffscreen &&
                Find(window, "PhotoPlaceholder").Current.Name == "Photo unavailable");
            SelectTreePerson(window, "avery@example.com");
            AssertPhotoFit(window, expectedWidth, expectedHeight, $"{width}x{height} after selection", photoArea);
        }
        Click(window, "ClearPhoto");
        Await(() => !Find(window, "PhotoPlaceholder").Current.IsOffscreen &&
            Find(window, "PhotoPlaceholder").Current.Name == "Photo unavailable");
        Console.WriteLine("PASS: DPI-normalized 144-DIP photo area, whole centered edge content, aspect ratio, down/upscaling, choose and selection.");
    }

    private static void ChoosePhotoFixture(AutomationElement window, string path)
    {
        var choose = Task.Run(() => Click(window, "ChoosePhoto"));
        AutomationElement? dialog = null;
        var condition = new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, window.Current.ProcessId),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window),
            new PropertyCondition(AutomationElement.NameProperty, "Choose a local Person photo"));
        Await(() => (dialog = window.FindFirst(TreeScope.Descendants, condition) ??
            AutomationElement.RootElement.FindFirst(TreeScope.Children, condition)) is not null);
        ControlReadiness.AwaitPattern(() =>
        {
            var fileName = Find(dialog!, "1148");
            return fileName.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)) ?? fileName;
        }, control => control.Current.IsEnabled,
            control => (ValuePattern)control.GetCurrentPattern(ValuePattern.Pattern), Await).SetValue(path);
        Click(dialog!, "1");
        Await(() => choose.IsCompleted);
        choose.GetAwaiter().GetResult();
        Await(() => Find(window, "Status").Current.Name == "Saved locally.");
    }

    private static void CreateEdgedPhotoFixture(string path, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var color = y < height / 8 ? Colors.Red :
                y >= height - height / 8 ? Colors.Lime :
                x < width / 8 ? Colors.Blue :
                x >= width - width / 8 ? Colors.Yellow : Colors.Magenta;
            var offset = (y * width + x) * 4;
            pixels[offset] = color.B;
            pixels[offset + 1] = color.G;
            pixels[offset + 2] = color.R;
            pixels[offset + 3] = 255;
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96,
            PixelFormats.Bgra32, null, pixels, width * 4)));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static System.Windows.Rect AssertPhotoFit(AutomationElement window, int expectedWidth, int expectedHeight,
        string source, System.Windows.Rect? photoArea)
    {
        window.SetFocus();
        var scroll = (ScrollPattern)Find(window, "ProfileScroll").GetCurrentPattern(ScrollPattern.Pattern);
        if (scroll.Current.VerticallyScrollable) scroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
        // UIA bounds are physical pixels; prevent GetPixel coordinate virtualization on mixed-DPI desktops.
        var previousDpi = SetThreadDpiAwarenessContext(new IntPtr(-4));
        if (previousDpi == IntPtr.Zero) throw new InvalidOperationException("Cannot observe physical photo pixels.");
        var failure = "Photo was not visible.";
        var observedArea = System.Windows.Rect.Empty;
        try
        {
            try
            {
                Await(() =>
                {
                    var image = Find(window, "PersonPhoto");
                    var bounds = image.Current.BoundingRectangle;
                    if (image.Current.IsOffscreen || bounds.IsEmpty) return false;
                    var dpi = GetDpiForWindow(new IntPtr(window.Current.NativeWindowHandle));
                    if (dpi == 0) throw new InvalidOperationException("Effective Windows photo DPI is unavailable.");
                    var scale = dpi / 96.0;
                    bool Near(double actual, double expected) => Math.Abs(actual - expected) <= 2;
                    failure = $"{source}: UIA image is {bounds.Width / scale:F1}x{bounds.Height / scale:F1} DIP; " +
                        $"expected {expectedWidth}x{expectedHeight}.";
                    if (!Near(bounds.Width / scale, expectedWidth) || !Near(bounds.Height / scale, expectedHeight)) return false;
                    // WPF Image exposes its fitted ink bounds, not its reserved layout slot.
                    // The first large square establishes that slot for subsequent non-square photos.
                    var area = photoArea ?? bounds;
                    observedArea = area;
                    var dc = GetDC(IntPtr.Zero);
                    try
                    {
                        uint Pixel(int x, int y)
                        {
                            var pixel = GetPixel(dc, x, y);
                            if (pixel == uint.MaxValue)
                                throw new InvalidOperationException("Actual photo pixels unavailable; an unlocked interactive desktop is required.");
                            return pixel;
                        }
                        bool Matches(uint pixel, Color color) =>
                            Math.Abs((int)(pixel & 0xff) - color.R) <= 20 &&
                            Math.Abs((int)((pixel >> 8) & 0xff) - color.G) <= 20 &&
                            Math.Abs((int)((pixel >> 16) & 0xff) - color.B) <= 20;
                        bool IsContent(uint pixel) => Matches(pixel, Colors.Red) || Matches(pixel, Colors.Lime) ||
                            Matches(pixel, Colors.Blue) || Matches(pixel, Colors.Yellow) || Matches(pixel, Colors.Magenta);
                        var centerX = (int)(area.Left + area.Width / 2);
                        var centerY = (int)(area.Top + area.Height / 2);
                        var columns = Enumerable.Range((int)Math.Ceiling(area.Left), (int)Math.Floor(area.Width))
                            .Where(x => IsContent(Pixel(x, centerY))).ToArray();
                        var rows = Enumerable.Range((int)Math.Ceiling(area.Top), (int)Math.Floor(area.Height))
                            .Where(y => IsContent(Pixel(centerX, y))).ToArray();
                        failure = $"{source}: distinguishable image content is missing.";
                        if (columns.Length == 0 || rows.Length == 0) return false;
                        var left = columns[0];
                        var top = rows[0];
                        var width = columns[^1] - left + 1;
                        var height = rows[^1] - top + 1;
                        failure = $"{source}: rendered content is {width / scale:F1}x{height / scale:F1} DIP; " +
                            $"expected {expectedWidth}x{expectedHeight}, centered with all four source edges.";
                        if (!Near(width / scale, expectedWidth) || !Near(height / scale, expectedHeight) ||
                            !Near((left - area.Left) / scale, (area.Right - left - width) / scale) ||
                            !Near((top - area.Top) / scale, (area.Bottom - top - height) / scale)) return false;
                        bool At(double x, double y, Color color) =>
                            Matches(Pixel((int)(left + width * x), (int)(top + height * y)), color);
                        foreach (var position in new[] { 0.25, 0.5, 0.75 })
                            if (!At(position, 0.04, Colors.Red) || !At(position, 0.96, Colors.Lime) ||
                                !At(0.04, position, Colors.Blue) || !At(0.96, position, Colors.Yellow)) return false;
                        if (!At(0.5, 0.5, Colors.Magenta)) return false;
                        // Sample the unused bands too: letterboxing must not hide cropped or repeated content.
                        foreach (var y in new[] { 0.04, 0.5, 0.96 })
                        foreach (var x in new[] { 0.04, 0.5, 0.96 })
                        {
                            var px = (int)(area.Left + area.Width * x);
                            var py = (int)(area.Top + area.Height * y);
                            if ((px < left - 1 || px > left + width || py < top - 1 || py > top + height) &&
                                IsContent(Pixel(px, py))) return false;
                        }
                        return true;
                    }
                    finally { ReleaseDC(IntPtr.Zero, dc); }
                });
            }
            catch (TimeoutException error) { throw new InvalidOperationException(failure, error); }
            return observedArea;
        }
        finally { SetThreadDpiAwarenessContext(previousDpi); }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr dc, int x, int y);
}
