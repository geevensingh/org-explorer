using System.Diagnostics;
using System.IO;
using Microsoft.Data.Sqlite;

namespace OrgExplorer.Desktop.Smoke;

internal sealed record SnapshotAdoptionActions(Action ShowEditor, Action EnterDraft,
    Action SelectPerson, Action ObservePerson);

internal sealed record SnapshotAdoptionMilestone(string Stage, long Timestamp, double ElapsedMs,
    bool ReaderHeld, bool JournalPresent);

internal sealed class SnapshotAdoptionObservation
{
    public List<SnapshotAdoptionMilestone> Milestones { get; } = [];
    public long TimestampFrequency { get; } = Stopwatch.Frequency;
    public string Outcome { get; set; } = "Interrupted";
    public string? ErrorType { get; set; }
    public string? Error { get; set; }
}

internal static class HeldSnapshotAdoption
{
    internal static SnapshotAdoptionObservation Run(string workspace,
        Func<SnapshotAdoptionActions> prepareActions, Action requestSync, Action observeAdopting,
        Action capture, Action<SnapshotAdoptionObservation> report)
    {
        var observation = new SnapshotAdoptionObservation();
        var started = Stopwatch.GetTimestamp();
        Exception? failure = null;
        SqliteConnection? connection = null;
        SqliteTransaction? reader = null;
        var journal = Path.Combine(workspace, "workspace.sqlite-journal");
        void Record(string stage)
        {
            var timestamp = Stopwatch.GetTimestamp();
            observation.Milestones.Add(new(stage, timestamp, Stopwatch.GetElapsedTime(started, timestamp).TotalMilliseconds,
                reader?.Connection is { } active && ReferenceEquals(active, connection), File.Exists(journal)));
        }
        void Step(string name, Action action)
        {
            Record(name + "-started");
            try { action(); Record(name + "-completed"); }
            catch { Record(name + "-failed"); throw; }
        }
        try
        {
            SnapshotAdoptionActions? actions = null;
            Step("prepare-controls", () => actions = prepareActions());
            connection = new SqliteConnection($"Data Source={Path.Combine(workspace, "workspace.sqlite")};Pooling=False");
            connection.Open();
            reader = connection.BeginTransaction(deferred: true);
            using (var read = connection.CreateCommand())
            {
                read.Transaction = reader;
                read.CommandText = "PRAGMA schema_version;";
                read.ExecuteScalar();
            }
            Record("reader-acquired");
            Step("sync-request", requestSync);
            Step("adopting-observed", observeAdopting);
            Step("show-editor", actions!.ShowEditor);
            Step("enter-draft", actions.EnterDraft);
            Step("select-person", actions.SelectPerson);
            Step("destination-observed", actions.ObservePerson);
            Record("held-verdict");
            if (!ReferenceEquals(reader.Connection, connection) || !File.Exists(journal))
                throw new InvalidOperationException("Native browsing/draft admission outlasted the actual held adoption transaction.");
            Step("capture", capture);
            if (!File.Exists(journal))
                throw new InvalidOperationException("Native adoption evidence was captured after its held transaction had ended.");
            Step("release-reader", reader.Rollback);
            observation.Outcome = "Passed";
            return observation;
        }
        catch (Exception error)
        {
            failure = error;
            observation.Outcome = "Failed";
            observation.ErrorType = error.GetType().Name;
            observation.Error = error.Message;
            throw;
        }
        finally
        {
            Record("outcome-observed");
            try { report(observation); }
            catch (Exception reportingError)
            {
                if (failure is not null)
                    throw new AggregateException("Held-adoption failure and its diagnostic output failure.", failure, reportingError);
                throw;
            }
            finally
            {
                reader?.Dispose();
                connection?.Dispose();
            }
        }
    }
}
