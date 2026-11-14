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
        Await(() =>
        {
            var bounds = AssertPhotoCircle(window);
            var dc = GetDC(IntPtr.Zero);
            try
            {
                var color = GetPixel(dc, (int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2));
                if (color == uint.MaxValue)
                    throw new InvalidOperationException("Actual UI pixels are unavailable. Desktop smoke requires an interactive, unlocked Windows desktop; image assertions cannot be skipped.");
                var corner = GetPixel(dc, (int)bounds.Left + 1, (int)bounds.Top + 1);
                if (corner == uint.MaxValue)
                    throw new InvalidOperationException("Actual circular photo-edge pixels are unavailable; hosted evidence cannot skip them.");
                return (color & 0xff) >= 240 && ((color >> 8) & 0xff) <= 20 && ((color >> 16) & 0xff) >= 240 &&
                    !((corner & 0xff) >= 240 && ((corner >> 8) & 0xff) <= 20 && ((corner >> 16) & 0xff) >= 240);
            }
            finally { ReleaseDC(IntPtr.Zero, dc); }
        });
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

    private static Rect AssertPhotoViewport(AutomationElement window)
    {
        var photo = Find(window, "Photo");
        var bounds = photo.Current.BoundingRectangle;
        var scale = DpiScale(window);
        if (photo.Current.IsOffscreen || bounds.IsEmpty || Math.Abs(bounds.Width / scale - 144) > 1 ||
            bounds.Height < bounds.Width - scale ||
            !IsVisible(window, "ChoosePhoto") && Math.Abs(bounds.Height / scale - 144) > 1)
            throw new InvalidOperationException($"Expected the fixed 144-DIP photo viewport above any edit actions: {bounds}, scale={scale}.");
        return new Rect(bounds.Left, bounds.Top, bounds.Width, bounds.Width);
    }

    private static Rect AssertPhotoCircle(AutomationElement window)
    {
        var image = Find(window, "PersonPhoto");
        var bounds = image.Current.BoundingRectangle;
        var scale = DpiScale(window);
        var viewport = AssertPhotoViewport(window);
        if (image.Current.IsOffscreen || bounds.IsEmpty ||
            bounds.Left > viewport.Left + scale || bounds.Top > viewport.Top + scale ||
            bounds.Right < viewport.Right - scale || bounds.Bottom < viewport.Bottom - scale ||
            Math.Abs(bounds.Left + bounds.Width / 2 - viewport.Left - viewport.Width / 2) > scale ||
            Math.Abs(bounds.Top + bounds.Height / 2 - viewport.Top - viewport.Height / 2) > scale)
            throw new InvalidOperationException($"Photo fill must be centered over the 144-DIP circular viewport: image={bounds}, viewport={viewport}.");
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
        Await(() =>
        {
            var circle = AssertPhotoCircle(window);
            if (!Find(window, "PersonPhoto").Current.Name.Contains($"{fixture.Width} × {fixture.Height} pixels"))
                return false;
            var scale = DpiScale(window);
            var image = Find(window, "PersonPhoto").Current.BoundingRectangle;
            if (Math.Abs(image.Width / scale - fixture.FilledWidth) > 1 ||
                Math.Abs(image.Height / scale - fixture.FilledHeight) > 1)
                throw new InvalidOperationException($"Expected {fixture.FilledWidth}x{fixture.FilledHeight} DIP pre-mask fill for " +
                    $"{fixture.Name}, not {image.Width / scale}x{image.Height / scale}.");
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
        });
        Console.WriteLine($"PASS: {fixture.Name}, source {fixture.Width}x{fixture.Height}, " +
            $"expected centered pre-mask fill {fixture.FilledWidth}x{fixture.FilledHeight} DIP, " +
            "centered square marker and axis bands within the final 144-DIP circle; diagonal exterior clipped.");
    }

    private static void AssertInitialsCircle(AutomationElement window, string initials, Rect? previousPhoto = null)
    {
        Await(() =>
        {
            var placeholder = Find(window, "PhotoPlaceholder");
            if (placeholder.Current.IsOffscreen ||
                placeholder.Current.Name != $"No local photo. Initials: {initials}") return false;
            var scale = DpiScale(window);
            var text = placeholder.Current.BoundingRectangle;
            var circle = AssertPhotoViewport(window);
            if (Math.Abs(text.Left + text.Width / 2 - circle.Left - circle.Width / 2) > scale ||
                Math.Abs(text.Top + text.Height / 2 - circle.Top - circle.Height / 2) > scale)
                throw new InvalidOperationException($"Initials must remain centered inside their photo viewport: text={text}, circle={circle}.");
            // A pending-edit notice can move the entire header without resizing its photo area.
            if (previousPhoto is { } previous &&
                (Math.Abs(circle.Width - previous.Width) > scale || Math.Abs(circle.Height - previous.Height) > scale))
                throw new InvalidOperationException($"Initials changed the photo footprint: photo={previous}, initials={circle}.");
            // TextBlock has no TextPattern. Its original 26.6-DIP Segoe UI line box scales to 68.4 DIP.
            if (Math.Abs(text.Height / scale - 68.4) > 1)
                throw new InvalidOperationException($"Initials must scale proportionally from 20 DIP at 56 to 144 DIP: text bounds={text}, scale={scale}.");
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
        });
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
