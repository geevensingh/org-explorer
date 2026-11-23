using System.Text.Json.Nodes;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class IncrementalDeliveryTests : IDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
        ".test-workspaces", Guid.NewGuid().ToString("N")));

    [Theory]
    [InlineData("photos")]
    [InlineData("acceptedImports")]
    [InlineData("history")]
    [InlineData("historical-photo")]
    [InlineData("person-history-consistency")]
    public void Incomplete_snapshot_preserves_the_last_complete_population_history_and_private_writing(string missing)
    {
        WorkspaceFixtures.CopyCurrent(Path.Combine(root, "source"));
        using var source = ApplicationWorkflow.CreateFileBacked();
        using var receiver = ApplicationWorkflow.CreateFileBacked();
        Assert.True(source.OpenWorkspace(Path.Combine(root, "source")), source.Status);
        Assert.True(receiver.OpenWorkspace(Path.Combine(root, "receiver")), receiver.Status);
        var outgoing = Path.Combine(root, "outgoing");
        WorkspaceFixtures.JoinSnapshot(source, receiver, outgoing);
        var capture = Assert.Single(receiver.GetHistoricalTeamContexts("avery@example.com"));
        Assert.True(source.EditPersonText("avery@example.com", PersonTextField.JobTitle, "Delivered engineer"), source.Status);
        Assert.True(source.SyncNow(), source.Synchronization.Error);
        var latest = Directory.GetFiles(outgoing, "*.oesnapshot").Single(path =>
            path.EndsWith(source.Synchronization.LocalRevision!.Value.ToString("D") + ".oesnapshot"));
        var original = File.ReadAllBytes(latest);
        WorkspaceFixtures.RewriteSnapshot(latest, data =>
        {
            if (missing == "historical-photo")
            {
                var row = data["historicalContexts"]!.AsArray().Single()!;
                var history = JsonNode.Parse(row["value"]!.GetValue<string>())!;
                history[0]!["reportingChain"]![0]!["photoPath"] = new string('a', 64) + ".png";
                row["value"] = history.ToJsonString();
            }
            else if (missing == "person-history-consistency")
            {
                var row = data["people"]!.AsArray().Single(p => p!["key"]!.GetValue<string>() == "avery@example.com")!;
                var profile = JsonNode.Parse(row["value"]!.GetValue<string>())!;
                profile["jobTitle"] = "Unconfirmed delivery";
                row["value"] = profile.ToJsonString();
            }
            else data[missing] = new JsonArray();
        });
        var rejected = File.ReadAllBytes(latest);
        Assert.False(receiver.SyncNow());
        Assert.NotNull(receiver.Synchronization.Error);
        Assert.Equal(5, receiver.People.Count);
        Assert.Equal("Engineer", receiver.GetRelationships("avery@example.com")!.Person.JobTitle);
        Assert.Equal("# Person writing\n**Preserved** café.", Assert.Single(receiver.GetPersonNotes("avery@example.com")).Markdown);
        Assert.Equal(capture.Id, Assert.Single(receiver.GetHistoricalTeamContexts("avery@example.com")).Id);
        Assert.Equal("# Frozen team\nOld team writing.", receiver.GetHistoricalTeamContexts("avery@example.com")[0].TeamNotes[0].Markdown);
        Assert.NotNull(receiver.GetPhoto("avery@example.com"));
        Assert.Equal(rejected, File.ReadAllBytes(latest));
        File.WriteAllBytes(latest, original);
        Assert.True(receiver.SyncNow(), receiver.Synchronization.Error);
        Assert.Equal("Delivered engineer", receiver.GetRelationships("avery@example.com")!.Person.JobTitle);
        Assert.Equal(capture.Id, Assert.Single(receiver.GetHistoricalTeamContexts("avery@example.com")).Id);
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        Assert.True(reopened.OpenWorkspace(Path.Combine(root, "receiver")), reopened.Status);
        Assert.Equal("Delivered engineer", reopened.GetRelationships("avery@example.com")!.Person.JobTitle);
        Assert.Equal(capture.Id, Assert.Single(reopened.GetHistoricalTeamContexts("avery@example.com")).Id);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
