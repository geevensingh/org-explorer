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
            OpenImportView(window);
            Set(window, "ImportPath", path);
            Click(window, "PreviewImport");
        }

        CreateJpegFixture(Path.Combine(producer, "avery.jpg"));
        Preview(",\"photoPath\":\"avery.jpg\"", 25);
        Await(() => Find(window, "ApplyImport").Current.IsEnabled);
        Click(window, "ApplyImport");
        Await(() => Find(window, "Status").Current.Name.Contains("Import applied"));
        ReturnToPeople(window);
        AssertPhotoVisible(window);
        if (!Find(window, "PersonPhoto").Current.Name.Contains("image/jpeg"))
            throw new InvalidOperationException("Actual JPEG did not display on the profile.");
        BeginProfileEditing(window);
        Click(window, "ClearPhoto");
        SaveProfileEditing(window);
        Await(() => Find(window, "PhotoPlaceholder").Current.Name == "Photo unavailable");
        ExpandHistory(window);
        Await(() => Find(window, "ProfileTimeline").Current.Name.Contains("Photo:") &&
                    Find(window, "ProfileTimeline").Current.Name.Contains(".jpg → (clear)") &&
                    Find(window, "ProfileTimeline").Current.Name.Contains("Local edit"));
        if (!Find(window, "ProfileTimeline").Current.Name.Contains("Imported"))
            throw new InvalidOperationException("Photo timeline lost imported versus local-edit provenance.");
        Console.WriteLine("PASS: actual copied PNG/JPEG pixels, local clear/placeholder and photo timeline.");
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr dc, int x, int y);
}
