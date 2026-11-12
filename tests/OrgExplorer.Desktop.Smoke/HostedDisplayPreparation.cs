namespace OrgExplorer.Desktop.Smoke;

internal sealed record HostedDisplayMode(int Width, int Height, int BitsPerPixel, int Frequency);
internal sealed record HostedDisplayState(string Device, HostedDisplayMode Mode, int MonitorWidth, int MonitorHeight,
    int WorkWidth, int WorkHeight, int Dpi, int MaximumTrackWidth, int MaximumTrackHeight);

internal interface IHostedDisplay
{
    HostedDisplayState Read();
    IReadOnlyList<HostedDisplayMode> AdvertisedModes(string device);
    int Test(string device, HostedDisplayMode mode);
    int Apply(string device, HostedDisplayMode mode);
}

internal static class HostedDisplayPreparation
{
    internal static HostedDisplayState Prepare(string? actions, string? environment, string? os,
        IHostedDisplay display, Action<Func<bool>> observe)
    {
        if (actions != "true" || environment != "github-hosted" || os != "Windows")
            throw new InvalidOperationException("High-resolution display preparation is restricted to disposable GitHub-hosted Windows runners.");
        var before = display.Read();
        if (before.Dpi != 96)
            throw new InvalidOperationException($"High-resolution display requires 96 DPI; refusing to alter DPI: {before}.");
        var mode = display.AdvertisedModes(before.Device)
            .Where(m => m.Width == 1920 && m.Height == 1080 && m.BitsPerPixel == 32 && m.Frequency > 0)
            .OrderBy(m => m.Frequency == before.Mode.Frequency ? 0 : 1).ThenBy(m => m.Frequency).FirstOrDefault()
            ?? throw new InvalidOperationException("The actual display does not advertise a 1920x1080 32-bpp mode; no synthetic mode will be applied.");
        var tested = display.Test(before.Device, mode);
        if (tested != 0)
            throw new InvalidOperationException($"Advertised display mode TEST rejected {mode}: driver result {tested}; no display change was applied.");
        var applied = display.Apply(before.Device, mode);
        if (applied != 0)
            throw new InvalidOperationException($"Dynamic display application failed for {mode}: driver result {applied}; restart/persistence is not permitted.");
        var actual = before;
        try
        {
            observe(() =>
            {
                actual = display.Read();
                return actual.Device == before.Device && actual.Mode == mode && HasCapacity(actual);
            });
        }
        catch (TimeoutException error)
        {
            throw new TimeoutException($"Applied display did not establish actual required capacity. Before: {before}; last: {actual}.", error);
        }
        Verify(actual);
        return actual;
    }

    internal static void Verify(HostedDisplayState actual)
    {
        if (!HasCapacity(actual))
            throw new InvalidOperationException($"High-resolution display prerequisites are not satisfied: {actual}. Required actual 1920x1080/32-bpp/96-DPI, work area >=1800x1000 and track capacity >=1100x900.");
    }

    internal static void VerifyOrdinary(HostedDisplayState actual)
    {
        if (actual.Mode is not { Width: 1024, Height: 768, BitsPerPixel: 32 } ||
            actual.MonitorWidth != 1024 || actual.MonitorHeight != 768 || actual.Dpi != 96)
            throw new InvalidOperationException($"Ordinary desktop requires actual unchanged 1024x768/32-bpp/96-DPI: {actual}.");
    }

    private static bool HasCapacity(HostedDisplayState actual) =>
        actual.Mode is { Width: 1920, Height: 1080, BitsPerPixel: 32 } &&
        actual.MonitorWidth == 1920 && actual.MonitorHeight == 1080 && actual.Dpi == 96 &&
        actual.WorkWidth >= 1800 && actual.WorkHeight >= 1000 &&
        actual.MaximumTrackWidth >= 1100 && actual.MaximumTrackHeight >= 900;
}
