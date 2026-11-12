using OrgExplorer.Desktop.Smoke;
using Xunit;

namespace OrgExplorer.Core.Tests;

public sealed class HostedDisplayPreparationTests
{
    private static readonly HostedDisplayMode Normal = new(1024, 768, 32, 64);
    private static readonly HostedDisplayMode Large = new(1920, 1080, 32, 64);
    private static HostedDisplayState State(HostedDisplayMode mode) =>
        new(@"\\.\DISPLAY1", mode, mode.Width, mode.Height, mode.Width, mode.Height - 48, 96, mode.Width + 20, mode.Height + 20);

    [Theory]
    [InlineData("false", "github-hosted", "Windows")]
    [InlineData("true", "self-hosted", "Windows")]
    [InlineData("true", "github-hosted", "Linux")]
    public void Preparation_refuses_local_shared_and_non_Windows_hosts_before_display_access(string actions, string environment, string os)
    {
        var display = new Display();
        Assert.Throws<InvalidOperationException>(() =>
            HostedDisplayPreparation.Prepare(actions, environment, os, display, Observe));
        Assert.Empty(display.Effects);
    }

    [Fact]
    public void Advertised_tested_mode_is_applied_once_then_actual_capacity_must_be_observed()
    {
        var display = new Display();
        var actual = HostedDisplayPreparation.Prepare("true", "github-hosted", "Windows", display, Observe);
        Assert.Equal(Large, actual.Mode);
        Assert.Equal(["read", "enumerate", "test", "apply", "read"], display.Effects);
        Assert.Equal(Large, display.Tested);
        Assert.Equal(Large, display.Applied);
    }

    [Fact]
    public void Delayed_actual_capacity_is_observed_without_retesting_or_reapplying_the_mode()
    {
        var display = new Display { Delay = true };
        var actual = HostedDisplayPreparation.Prepare("true", "github-hosted", "Windows", display, ready =>
        {
            Assert.False(ready());
            Assert.True(ready());
        });
        Assert.Equal(Large, actual.Mode);
        Assert.Equal(1, display.Effects.Count(e => e == "test"));
        Assert.Equal(1, display.Effects.Count(e => e == "apply"));
    }

    [Fact]
    public void Ordinary_lane_requires_actual_1024_monitor_bounds_not_just_a_mode_description()
    {
        HostedDisplayPreparation.VerifyOrdinary(State(Normal));
        Assert.Throws<InvalidOperationException>(() => HostedDisplayPreparation.VerifyOrdinary(State(Large)));
        Assert.Throws<InvalidOperationException>(() =>
            HostedDisplayPreparation.VerifyOrdinary(State(Normal) with { MonitorWidth = 1920 }));
    }

    [Theory]
    [InlineData("unadvertised")]
    [InlineData("test")]
    [InlineData("apply")]
    [InlineData("unchanged")]
    [InlineData("dpi")]
    [InlineData("workarea")]
    [InlineData("track")]
    [InlineData("monitor")]
    [InlineData("frequency")]
    public void Missing_or_rejected_display_evidence_fails_closed_without_synthetic_modes_or_repeated_application(string failure)
    {
        var display = new Display { Failure = failure };
        var error = Assert.ThrowsAny<Exception>(() =>
            HostedDisplayPreparation.Prepare("true", "github-hosted", "Windows", display, Observe));
        Assert.Contains("display", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(display.Effects.Count(e => e == "apply") <= 1);
        if (failure is "unadvertised" or "test") Assert.DoesNotContain("apply", display.Effects);
    }

    private static void Observe(Func<bool> ready)
    {
        if (!ready()) throw new TimeoutException("display observation did not reach actual required capacity");
    }

    private sealed class Display : IHostedDisplay
    {
        public List<string> Effects { get; } = [];
        public string? Failure { get; init; }
        public bool Delay { get; init; }
        public HostedDisplayMode? Tested { get; private set; }
        public HostedDisplayMode? Applied { get; private set; }
        public IReadOnlyList<HostedDisplayMode> AdvertisedModes(string device)
        {
            Effects.Add("enumerate");
            return Failure == "unadvertised" ? [Normal] : [Normal, Large];
        }
        public HostedDisplayState Read()
        {
            Effects.Add("read");
            var state = State(Applied is null || Failure == "unchanged" ||
                Delay && Effects.Count(e => e == "read") == 2 ? Normal : Large);
            return Failure switch
            {
                "dpi" => state with { Dpi = 120 },
                "workarea" when Applied is not null => state with { WorkHeight = 720 },
                "track" when Applied is not null => state with { MaximumTrackWidth = 1044 },
                "monitor" when Applied is not null => state with { MonitorWidth = 1024 },
                "frequency" when Applied is not null => state with { Mode = Large with { Frequency = 60 } },
                _ => state
            };
        }
        public int Test(string device, HostedDisplayMode mode)
        {
            Effects.Add("test");
            Tested = mode;
            return Failure == "test" ? -2 : 0;
        }
        public int Apply(string device, HostedDisplayMode mode)
        {
            Effects.Add("apply");
            Applied = mode;
            return Failure == "apply" ? 1 : 0;
        }
    }
}
