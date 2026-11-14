using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
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
        var observed = "photo not yet observed";
        AwaitPhoto(() =>
        {
            if (!TryPhotoCircle(window, out var bounds, out observed)) return false;
            var dc = GetDC(IntPtr.Zero);
            try
            {
                var color = GetPixel(dc, (int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2));
                if (color == uint.MaxValue)
                    throw new InvalidOperationException("Actual UI pixels are unavailable. Desktop smoke requires an interactive, unlocked Windows desktop; image assertions cannot be skipped.");
                var corner = GetPixel(dc, (int)bounds.Left + 1, (int)bounds.Top + 1);
                if (corner == uint.MaxValue)
                    throw new InvalidOperationException("Actual circular photo-edge pixels are unavailable; hosted evidence cannot skip them.");
                observed += $"; center={color:X8}, corner={corner:X8}";
                return (color & 0xff) >= 240 && ((color >> 8) & 0xff) <= 20 && ((color >> 16) & 0xff) >= 240 &&
                    !((corner & 0xff) >= 240 && ((corner >> 8) & 0xff) <= 20 && ((corner >> 16) & 0xff) >= 240);
            }
            finally { ReleaseDC(IntPtr.Zero, dc); }
        }, () => observed);
    }

    private sealed record PhotoCase(string Name, int Width, int Height, int FilledWidth, int FilledHeight,
        uint Left, uint Right, uint Top, uint Bottom);

    private const uint MagentaPixel = 0xff00ff;
    private const uint RedPixel = 0x0000ff;
    private const uint BluePixel = 0xff0000;
    private const uint YellowPixel = 0x00ffff;
    private const uint GreenPixel = 0x00a000;
    private const uint WhitePixel = 0xffffff;

    private static void CreateMarkedPhoto(string path, PhotoCase fixture)
    {
        var pixels = new byte[fixture.Width * fixture.Height * 4];
        for (var y = 0; y < fixture.Height; y++)
            for (var x = 0; x < fixture.Width; x++)
            {
                var color = x < fixture.Width * .2 ? RedPixel :
                    x >= fixture.Width * .8 ? BluePixel : MagentaPixel;
                if (y < fixture.Height * .2) color = YellowPixel;
                if (y >= fixture.Height * .8) color = GreenPixel;
                // A source-space square must stay square after centered fill.
                var halfMarker = Math.Min(fixture.Width, fixture.Height) * .1;
                if (Math.Abs(x + .5 - fixture.Width / 2d) < halfMarker &&
                    Math.Abs(y + .5 - fixture.Height / 2d) < halfMarker) color = WhitePixel;
                var offset = (y * fixture.Width + x) * 4;
                pixels[offset] = (byte)(color >> 16);
                pixels[offset + 1] = (byte)(color >> 8);
                pixels[offset + 2] = (byte)color;
                pixels[offset + 3] = 255;
            }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(fixture.Width, fixture.Height,
            96, 96, PixelFormats.Bgra32, null, pixels, fixture.Width * 4)));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static void AwaitPhoto(Func<bool> condition, Func<string> describe)
    {
        try { Await(condition); }
        catch (TimeoutException error)
        { throw new TimeoutException("Photo presentation did not settle: " + describe(), error); }
    }

    private static bool TryPhotoViewport(AutomationElement window, out Rect viewport, out string observed)
    {
        var photo = Find(window, "Photo");
        var bounds = photo.Current.BoundingRectangle;
        var scale = DpiScale(window);
        viewport = Rect.Empty;
        observed = $"viewport={bounds}, offscreen={photo.Current.IsOffscreen}, scale={scale}";
        if (photo.Current.IsOffscreen || bounds.IsEmpty || Math.Abs(bounds.Width / scale - 144) > 1 ||
            Math.Abs(bounds.Height / scale - 144) > 1)
            return false;
        viewport = bounds;
        return true;
    }

    private static bool TryPhotoCircle(AutomationElement window, out Rect viewport, out string observed)
    {
        if (!TryPhotoViewport(window, out viewport, out observed)) return false;
        var image = Find(window, "PersonPhoto");
        var bounds = image.Current.BoundingRectangle;
        var scale = DpiScale(window);
        observed += $"; accessible image={bounds}, offscreen={image.Current.IsOffscreen}";
        return !image.Current.IsOffscreen && !bounds.IsEmpty &&
            Math.Abs(bounds.Left - viewport.Left) <= scale && Math.Abs(bounds.Top - viewport.Top) <= scale &&
            Math.Abs(bounds.Width - viewport.Width) <= scale && Math.Abs(bounds.Height - viewport.Height) <= scale;
    }

    private static Rect AssertPhotoCircle(AutomationElement window)
    {
        var viewport = Rect.Empty;
        var observed = "photo not yet observed";
        AwaitPhoto(() => TryPhotoCircle(window, out viewport, out observed), () => observed);
        return viewport;
    }

    private static bool PixelMatches(IntPtr dc, Rect circle, double scale, double x, double y, uint expected)
    {
        var actual = GetPixel(dc, (int)Math.Round(circle.Left + circle.Width / 2 + x * scale),
            (int)Math.Round(circle.Top + circle.Height / 2 + y * scale));
        if (actual == uint.MaxValue)
            throw new InvalidOperationException("Actual photo pixels are unavailable; an unlocked isolated desktop is required.");
        return Math.Abs((int)(actual & 255) - (int)(expected & 255)) <= 20 &&
            Math.Abs((int)((actual >> 8) & 255) - (int)((expected >> 8) & 255)) <= 20 &&
            Math.Abs((int)((actual >> 16) & 255) - (int)((expected >> 16) & 255)) <= 20;
    }

    private static bool ReadPhotoPixels(Rect circle, double scale, Func<Func<double, double, uint, bool>, bool> observe)
    {
        var left = (int)Math.Floor(circle.Left - 10 * scale);
        var top = (int)Math.Floor(circle.Top - 10 * scale);
        var width = (int)Math.Ceiling(circle.Right + 10 * scale) - left;
        var height = (int)Math.Ceiling(circle.Bottom + 10 * scale) - top;
        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) throw new InvalidOperationException("Actual photo pixels are unavailable.");
        var memory = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            memory = CreateCompatibleDC(screen);
            bitmap = CreateCompatibleBitmap(screen, width, height);
            if (memory == IntPtr.Zero || bitmap == IntPtr.Zero)
                throw new InvalidOperationException("Actual photo capture could not allocate its pixel buffer.");
            previous = SelectObject(memory, bitmap);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1) ||
                !BitBlt(memory, 0, 0, width, height, screen, left, top, 0x40CC0020))
                throw new InvalidOperationException("Actual desktop photo capture failed.");
            var local = new Rect(circle.Left - left, circle.Top - top, circle.Width, circle.Height);
            return observe((x, y, color) => PixelMatches(memory, local, scale, x, y, color));
        }
        finally
        {
            if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static bool CircleSpans(Func<double, double, uint, bool> at, double scale,
        uint left, uint right, uint top, uint bottom)
    {
        var edges = new int[4];
        for (var offset = (int)(55 * scale); offset <= 80 * scale; offset++)
        {
            if (at(-offset / scale, 0, left)) edges[0] = offset;
            if (at(offset / scale, 0, right)) edges[1] = offset;
            if (at(0, -offset / scale, top)) edges[2] = offset;
            if (at(0, offset / scale, bottom)) edges[3] = offset;
        }
        return edges.All(edge => edge > 0) &&
            Math.Abs((edges[0] + edges[1] + 1) / scale - 144) <= 2 / scale &&
            Math.Abs((edges[2] + edges[3] + 1) / scale - 144) <= 2 / scale;
    }

    private static void AssertMarkedPhoto(AutomationElement window, PhotoCase fixture)
    {
        var observed = fixture.Name + " not yet observed";
        AwaitPhoto(() =>
        {
            if (!TryPhotoCircle(window, out var circle, out observed)) return false;
            observed += $"; expected source={fixture.Width}x{fixture.Height}, name={Find(window, "PersonPhoto").Current.Name}";
            if (!Find(window, "PersonPhoto").Current.Name.Contains($"{fixture.Width} × {fixture.Height} pixels"))
                return false;
            var scale = DpiScale(window);
            return ReadPhotoPixels(circle, scale, At =>
            {
                bool Bands(double filled, bool horizontal, uint negative, uint positive)
                {
                    bool Sample(double offset, uint color) => horizontal ? At(offset, 0, color) : At(0, offset, color);
                    var boundary = filled * .3;
                    return boundary < 60
                        ? Sample(-boundary - 8, negative) && Sample(-boundary + 8, MagentaPixel) &&
                          Sample(boundary - 8, MagentaPixel) && Sample(boundary + 8, positive)
                        : Sample(-60, MagentaPixel) && Sample(60, MagentaPixel);
                }
                return At(0, 0, WhitePixel) &&
                    At(-10, 0, WhitePixel) && At(10, 0, WhitePixel) &&
                    At(0, -10, WhitePixel) && At(0, 10, WhitePixel) &&
                    At(-20, 0, MagentaPixel) && At(20, 0, MagentaPixel) &&
                    At(0, -20, MagentaPixel) && At(0, 20, MagentaPixel) &&
                    At(-48, 0, fixture.Left) && At(48, 0, fixture.Right) &&
                    At(0, -48, fixture.Top) && At(0, 48, fixture.Bottom) &&
                    Bands(fixture.FilledWidth, true, RedPixel, BluePixel) &&
                    Bands(fixture.FilledHeight, false, YellowPixel, GreenPixel) &&
                    At(-48, -48, fixture.Top == MagentaPixel ? fixture.Left : fixture.Top) &&
                    At(48, 48, fixture.Bottom == MagentaPixel ? fixture.Right : fixture.Bottom) &&
                    At(-60, -48, WhitePixel) && At(60, -48, WhitePixel) &&
                    At(-60, 48, WhitePixel) && At(60, 48, WhitePixel) &&
                    CircleSpans(At, scale, fixture.Left, fixture.Right, fixture.Top, fixture.Bottom);
            });
        }, () => observed);
        Console.WriteLine($"PASS: {fixture.Name}, source {fixture.Width}x{fixture.Height}, " +
            $"expected centered pre-mask fill {fixture.FilledWidth}x{fixture.FilledHeight} DIP, " +
            "centered square marker and axis bands within the final 144-DIP circle; diagonal exterior clipped.");
    }

    private static void AssertInitialsCircle(AutomationElement window, string initials, Rect? previousPhoto = null)
    {
        var observed = "initials not yet observed";
        AwaitPhoto(() =>
        {
            var placeholder = Find(window, "PhotoPlaceholder");
            observed = $"initials={placeholder.Current.Name}, offscreen={placeholder.Current.IsOffscreen}";
            if (placeholder.Current.IsOffscreen ||
                placeholder.Current.Name != $"No local photo. Initials: {initials}") return false;
            var scale = DpiScale(window);
            var text = placeholder.Current.BoundingRectangle;
            var viewportReady = TryPhotoViewport(window, out var circle, out var viewportObserved);
            observed += $"; text={text}; {viewportObserved}";
            if (!viewportReady) return false;
            if (text.IsEmpty || Math.Abs(text.Left + text.Width / 2 - circle.Left - circle.Width / 2) > scale ||
                Math.Abs(text.Top + text.Height / 2 - circle.Top - circle.Height / 2) > scale)
                return false;
            // A pending-edit notice can move the entire header without resizing its photo area.
            if (previousPhoto is { } previous &&
                (Math.Abs(circle.Width - previous.Width) > scale || Math.Abs(circle.Height - previous.Height) > scale))
                return false;
            // TextBlock has no TextPattern. Its original 26.6-DIP Segoe UI line box scales to 68.4 DIP.
            if (Math.Abs(text.Height / scale - 68.4) > 1)
                return false;
            return ReadPhotoPixels(circle, scale, At =>
            {
                const uint background = 0x794b21;
                if (!At(-68, 0, background) || !At(68, 0, background) ||
                    !At(0, -68, background) || !At(0, 68, background) ||
                    !At(-48, -48, background) || !At(48, 48, background) ||
                    !At(-60, -48, WhitePixel) || !At(60, 48, WhitePixel) ||
                    !At(-74, 0, WhitePixel) || !At(74, 0, WhitePixel)) return false;
                var inkTop = 144d;
                var inkBottom = -144d;
                var inkLeft = 144d;
                var inkRight = -144d;
                for (var y = -40; y <= 40; y += 2)
                    for (var x = -54; x <= 54; x += 2)
                        if (At(x, y, WhitePixel))
                        {
                            inkTop = Math.Min(inkTop, y);
                            inkBottom = Math.Max(inkBottom, y);
                            inkLeft = Math.Min(inkLeft, x);
                            inkRight = Math.Max(inkRight, x);
                        }
                return inkBottom - inkTop >= 30 && inkRight - inkLeft >= 20 &&
                    CircleSpans(At, scale, background, background, background, background);
            });
        }, () => observed);
    }

    private static void ChooseProfilePhoto(AutomationElement window, string path)
    {
        Click(window, "ChoosePhoto");
        AutomationElement? Picker()
        {
            var condition = new AndCondition(
                new PropertyCondition(AutomationElement.ProcessIdProperty, window.Current.ProcessId),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window),
                new PropertyCondition(AutomationElement.NameProperty, "Choose a local Person photo"));
            return AutomationElement.RootElement.FindFirst(TreeScope.Children, condition) ??
                window.FindFirst(TreeScope.Descendants, condition);
        }
        AutomationElement? picker = null;
        Await(() => (picker = Picker()) is not null && !picker.Current.IsOffscreen);
        AutomationElement? FileName() => Find(picker!, "1148").FindFirst(TreeScope.Subtree,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        var entry = ControlReadiness.AwaitPattern(FileName,
            control => control is not null && control.Current.IsEnabled && !control.Current.IsOffscreen,
            control => control!.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) ? (ValuePattern)pattern : null, Await);
        entry.SetValue(Path.GetFullPath(path));
        if (entry.Current.Value != Path.GetFullPath(path))
            throw new InvalidOperationException("Native photo picker did not retain the requested file.");
        Click(picker!, "1", ControlType.Button);
        Await(() => Picker() is null);
        Await(() => Find(window, "ProfilePhotoDraft").Current.Name.Contains(Path.GetFileName(path)));
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
        Await(() => Status(window).Contains("Import applied"));
        ReturnToPeople(window);
        AssertPhotoVisible(window);
        if (!Find(window, "PersonPhoto").Current.Name.Contains("image/jpeg"))
            throw new InvalidOperationException("Actual JPEG did not display on the profile.");
        BeginProfileEditing(window);
        var photoBounds = AssertPhotoCircle(window);
        Click(window, "ClearPhoto");
        AssertPhotoVisible(window);
        SaveProfileEditing(window);
        AssertInitialsCircle(window, "A", photoBounds);
        CaptureVisualEvidence(window, "profile-reading-cleared-photo-initials");
        ExpandHistory(window);
        Await(() => Find(window, "ProfileTimeline").Current.Name.Contains("Photo:") &&
                    Find(window, "ProfileTimeline").Current.Name.Contains(".jpg → (clear)") &&
                    Find(window, "ProfileTimeline").Current.Name.Contains("Local edit"));
        if (!Find(window, "ProfileTimeline").Current.Name.Contains("Imported"))
            throw new InvalidOperationException("Photo timeline lost imported versus local-edit provenance.");
        PhotoCase[] fixtures =
        [
            new("large-square", 300, 300, 144, 144, RedPixel, BluePixel, YellowPixel, GreenPixel),
            new("landscape", 300, 150, 288, 144, MagentaPixel, MagentaPixel, YellowPixel, GreenPixel),
            new("portrait", 150, 300, 144, 288, RedPixel, BluePixel, MagentaPixel, MagentaPixel),
            new("small-square", 72, 72, 144, 144, RedPixel, BluePixel, YellowPixel, GreenPixel),
            new("small-landscape", 72, 36, 288, 144, MagentaPixel, MagentaPixel, YellowPixel, GreenPixel)
        ];
        foreach (var fixture in fixtures)
            CreateMarkedPhoto(Path.Combine(producer, fixture.Name + ".png"), fixture);
        BeginProfileEditing(window);
        ChooseProfilePhoto(window, Path.Combine(producer, fixtures[0].Name + ".png"));
        AssertInitialsCircle(window, "A", photoBounds);
        CancelProfileEditing(window);
        AssertInitialsCircle(window, "A", photoBounds);
        foreach (var fixture in fixtures)
        {
            BeginProfileEditing(window);
            ChooseProfilePhoto(window, Path.Combine(producer, fixture.Name + ".png"));
            SaveProfileEditing(window);
            AssertMarkedPhoto(window, fixture);
            CaptureVisualEvidence(window, "profile-photo-" + fixture.Name);
        }
        photoBounds = AssertPhotoCircle(window);
        NavigateToManager(window, "morgan@example.com");
        AssertInitialsCircle(window, "ME", photoBounds);
        SelectTreePerson(window, "avery@example.com");
        Await(() => Find(window, "Email").Current.Name == "avery@example.com");
        AssertMarkedPhoto(window, fixtures[^1]);
        BeginProfileEditing(window);
        Click(window, "ClearPhoto");
        AssertMarkedPhoto(window, fixtures[^1]);
        CancelProfileEditing(window);
        AssertMarkedPhoto(window, fixtures[^1]);
        BeginProfileEditing(window);
        Click(window, "ClearPhoto");
        SaveProfileEditing(window);
        AssertInitialsCircle(window, "A", photoBounds);
        Console.WriteLine("PASS: copied PNG/JPEG, centered circular reduction/enlargement, matching readable initials, " +
            "explicit choose/clear Save and Cancel, Person selection and imported/local photo provenance.");
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr dc, int x, int y);
}
