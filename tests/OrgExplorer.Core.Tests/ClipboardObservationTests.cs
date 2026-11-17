using System.Runtime.InteropServices;
using OrgExplorer.Desktop.Smoke;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class ClipboardObservationTests
{
    [Fact]
    [Trait("Category", "Runner")]
    public void Cannot_open_read_keeps_observing_until_the_exact_copied_text_is_available()
    {
        var reads = 0;
        var waits = 0;
        ClipboardObservation.AwaitText("007B\nFloor 2", () => ++reads switch
        {
            1 => throw new COMException("OpenClipboard Failed", unchecked((int)0x800401D0)),
            2 => "Copy has not been invoked.",
            3 => "007B\r\nFloor 2",
            _ => "007B\nFloor 2"
        }, condition =>
        {
            waits++;
            for (var poll = 0; poll < 4; poll++)
                if (condition()) return;
            throw new TimeoutException("The scripted observation expired.");
        });

        Assert.Equal(4, reads);
        Assert.Equal(1, waits);
    }

    [Theory]
    [Trait("Category", "Runner")]
    [InlineData("")]
    [InlineData("007B")]
    public void Permanently_unavailable_clipboard_expires_with_the_original_hresult_and_wait_failure(string expected)
    {
        var unavailable = new COMException("OpenClipboard Failed", unchecked((int)0x800401D0));
        var deadline = new TimeoutException("The scripted observation expired.");
        var reads = 0;
        var waits = 0;
        var error = Assert.Throws<TimeoutException>(() => ClipboardObservation.AwaitText(expected,
            () => { reads++; throw unavailable; }, condition =>
            {
                waits++;
                for (var poll = 0; poll < 4; poll++)
                    if (condition()) return;
                throw deadline;
            }));

        Assert.Equal(4, reads);
        Assert.Equal(1, waits);
        Assert.Contains("within 20 seconds", error.Message);
        Assert.Contains("Last read: unavailable", error.Message);
        Assert.Contains("0x800401D0 (CLIPBRD_E_CANT_OPEN)", error.Message);
        var causes = Assert.IsType<AggregateException>(error.InnerException);
        Assert.Collection(causes.InnerExceptions,
            cause => Assert.Same(deadline, cause),
            cause => Assert.Same(unavailable, cause));
    }

    [Theory]
    [Trait("Category", "Runner")]
    [InlineData(false)]
    [InlineData(true)]
    public void Readable_wrong_text_still_expires_even_after_a_cannot_open_read(bool initiallyUnavailable)
    {
        var unavailable = new COMException("OpenClipboard Failed", unchecked((int)0x800401D0));
        var deadline = new TimeoutException("The scripted observation expired.");
        var reads = 0;
        var error = Assert.Throws<TimeoutException>(() => ClipboardObservation.AwaitText("007B", () =>
        {
            if (++reads == 1 && initiallyUnavailable) throw unavailable;
            return "007b";
        }, condition =>
        {
            for (var poll = 0; poll < 4; poll++)
                if (condition()) return;
            throw deadline;
        }));

        Assert.Equal(4, reads);
        Assert.Contains("Last read: available, different text", error.Message);
        if (initiallyUnavailable)
        {
            var causes = Assert.IsType<AggregateException>(error.InnerException);
            Assert.Collection(causes.InnerExceptions,
                cause => Assert.Same(deadline, cause),
                cause => Assert.Same(unavailable, cause));
        }
        else Assert.Same(deadline, error.InnerException);
    }

    [Fact]
    [Trait("Category", "Runner")]
    public void Unexpected_read_errors_propagate_unchanged_without_more_reads_or_another_wait()
    {
        Exception[] errors =
        [
            new COMException("Cannot empty clipboard", unchecked((int)0x800401D1)),
            new COMException("Unspecified failure", unchecked((int)0x80004005)),
            new ExternalException("Not a COM read failure", unchecked((int)0x800401D0)),
            new InvalidOperationException("The read provider failed")
        ];
        foreach (var original in errors)
        {
            var reads = 0;
            var waits = 0;
            var observed = Assert.ThrowsAny<Exception>(() => ClipboardObservation.AwaitText("007B",
                () => { reads++; throw original; }, condition =>
                {
                    waits++;
                    for (var poll = 0; poll < 4; poll++)
                        if (condition()) return;
                    throw new TimeoutException("The scripted observation expired.");
                }));

            Assert.Same(original, observed);
            Assert.Equal(1, reads);
            Assert.Equal(1, waits);
        }
    }
}
