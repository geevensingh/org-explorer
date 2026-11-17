using System.Runtime.InteropServices;

namespace OrgExplorer.Desktop.Smoke;

internal static class ClipboardObservation
{
    internal static void AwaitText(string expected, Func<string> readText, Action<Func<bool>> awaitState)
    {
        COMException? lastUnavailable = null;
        var lastRead = "not attempted";
        try
        {
            awaitState(() =>
            {
                try
                {
                    var matches = readText() == expected;
                    lastRead = matches ? "available, expected text" : "available, different text";
                    return matches;
                }
                catch (COMException error) when (error.HResult == unchecked((int)0x800401D0))
                {
                    lastUnavailable = error;
                    lastRead = "unavailable: 0x800401D0 (CLIPBRD_E_CANT_OPEN)";
                    return false;
                }
            });
        }
        catch (TimeoutException error)
        {
            throw new TimeoutException(
                $"The actual clipboard did not receive the expected text within 20 seconds. Last read: {lastRead}.",
                lastUnavailable is null ? error : new AggregateException(error, lastUnavailable));
        }
    }
}
