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
        Await(() =>
        {
            var image = Find(window, "PersonPhoto");
            var bounds = image.Current.BoundingRectangle;
            if (image.Current.IsOffscreen || bounds.IsEmpty || bounds.Width < 1 || bounds.Height < 1) return false;
            var dc = GetDC(IntPtr.Zero);
            try
            {
                var color = GetPixel(dc, (int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2));
                return (color & 0xff) >= 240 && ((color >> 8) & 0xff) <= 20 && ((color >> 16) & 0xff) >= 240;
            }
            finally { ReleaseDC(IntPtr.Zero, dc); }
        });
    }

    private static void ExercisePhotoImports(AutomationElement window, string root)
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

        Preview("", 21);
        Await(() => Find(window, "ApplyImport").Current.IsEnabled);
        Click(window, "ApplyImport");
        AssertPhotoVisible(window);
        File.WriteAllText(Path.Combine(producer, "corrupt.png"), "This is not an image.");
        Preview(",\"photoPath\":\"corrupt.png\",\"displayName\":\"Must not apply\"", 22);
        Await(() => Find(window, "Status").Current.Name.Contains("Import rejected"));
        if (Find(window, "ApplyImport").Current.IsEnabled)
            throw new InvalidOperationException("Invalid supplied photo enabled Apply.");
        AssertPhotoVisible(window);
        if (Find(window, "DirectoryName").Current.Name != "Avery Example")
            throw new InvalidOperationException("Invalid image changed the Person.");
        Preview(",\"photoPath\":\"https://example.com/photo.png\"", 24);
        Await(() => Find(window, "Status").Current.Name.Contains("Import rejected"));
        if (Find(window, "ApplyImport").Current.IsEnabled)
            throw new InvalidOperationException("Remote photo URL enabled Apply.");
        AssertPhotoVisible(window);
        Preview(",\"photoPath\":null", 23);
        Await(() => Find(window, "ApplyImport").Current.IsEnabled);
        Click(window, "ApplyImport");
        Await(() => Find(window, "PhotoPlaceholder").Current.Name == "Photo unavailable");
        CreateJpegFixture(Path.Combine(producer, "avery.jpg"));
        Preview(",\"photoPath\":\"avery.jpg\"", 25);
        Await(() => Find(window, "ApplyImport").Current.IsEnabled);
        Click(window, "ApplyImport");
        AssertPhotoVisible(window);
        if (!Find(window, "PersonPhoto").Current.Name.Contains("image/jpeg"))
            throw new InvalidOperationException("Actual JPEG did not display on the profile.");
        Click(window, "ClearPhoto");
        Await(() => Find(window, "PhotoPlaceholder").Current.Name == "Photo unavailable");
        Console.WriteLine("PASS: actual PNG/JPEG pixels → export removal/reopen → omitted photo → corrupt/remote rejection → imported/local clear/placeholder.");
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr dc, int x, int y);
}
