namespace OrgExplorer.TestFixtures;

public class SnapshotClock : TimeProvider
{
    private readonly object gate = new();
    private readonly List<ClockTimer> timers = [];
    private long ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref ticks);
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (gate)
        {
            var timer = new ClockTimer(this, callback, state);
            timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
    }
    public void Advance(TimeSpan amount)
    {
        ClockTimer[] due;
        lock (gate)
        {
            ticks += amount.Ticks;
            due = timers.Where(timer => timer.Due <= ticks).ToArray();
            foreach (var timer in due)
                timer.Due = timer.Period > 0
                    ? timer.Due + ((ticks - timer.Due) / timer.Period + 1) * timer.Period : long.MaxValue;
        }
        foreach (var timer in due) timer.Callback(timer.State);
    }
    private sealed class ClockTimer(SnapshotClock clock, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback;
        public object? State { get; } = state;
        public long Due = long.MaxValue, Period;
        private bool disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock.gate)
            {
                if (disposed) return false;
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.ticks + dueTime.Ticks;
                Period = period.Ticks;
                return true;
            }
        }
        public void Dispose()
        {
            lock (clock.gate) { disposed = true; Due = long.MaxValue; clock.timers.Remove(this); }
        }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
