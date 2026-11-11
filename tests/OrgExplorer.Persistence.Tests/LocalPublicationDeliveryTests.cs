using OrgExplorer.Workflow;
using Xunit;

namespace OrgExplorer.Persistence.Tests;

public sealed class LocalPublicationDeliveryTests
{
    [Fact]
    public void Accepted_import_source_reopens_with_the_same_numeric_version_rules_as_preview()
    {
        var folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
            ".test-workspaces", Guid.NewGuid().ToString("N")));
        using var app = ApplicationWorkflow.CreateFileBacked();
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        try
        {
            Assert.True(app.OpenWorkspace(folder), app.Status);
            const string batch = """
                {"schemaVersion":1.0,"batchId":"20000000-0013-4000-8000-999999999996",
                 "observedAt":"2026-11-06T17:00:00Z","people":[
                 {"email":"avery@example.com","displayName":"Avery","managerEmail":null}]}
                """;
            var preview = app.PreviewImport(batch);
            Assert.True(preview.CanApply, app.Status);
            Assert.True(app.ApplyImport(preview.Id), app.Status);
            Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
            Assert.Equal("Avery", Assert.Single(reopened.People).DirectoryName);
            Assert.Contains("no-op", Assert.Single(reopened.PreviewImport(batch).Changes));
        }
        finally
        {
            reopened.Dispose();
            app.Dispose();
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Theory]
    [InlineData("person", false, false, false)]
    [InlineData("person", true, false, false)]
    [InlineData("team", false, false, false)]
    [InlineData("team", true, false, false)]
    [InlineData("generation", false, false, false)]
    [InlineData("generation", true, false, false)]
    [InlineData("person", false, true, false)]
    [InlineData("team", true, true, false)]
    [InlineData("generation", false, true, false)]
    [InlineData("person", false, false, true)]
    [InlineData("person", true, false, true)]
    [InlineData("team", false, false, true)]
    [InlineData("team", true, false, true)]
    [InlineData("generation", false, false, true)]
    [InlineData("generation", true, false, true)]
    [InlineData("person", false, true, true)]
    [InlineData("team", true, true, true)]
    [InlineData("generation", false, true, true)]
    public void A_local_publication_accepts_only_its_validated_base_and_own_changes(
        string localChange, bool deliveredTeamNote, bool invalidDelivery, bool incremental)
    {
        var folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
            ".test-workspaces", Guid.NewGuid().ToString("N")));
        var publisher = new DeliveringPublisher();
        using var app = incremental ? ApplicationWorkflow.CreateIncrementalFileBacked(filePublisher: publisher) :
            ApplicationWorkflow.CreateFileBacked(filePublisher: publisher);
        try
        {
            Assert.True(app.OpenWorkspace(folder), app.Status);
            Assert.True(app.ApplyImport(app.PreviewImport("""
                {"schemaVersion":1,"batchId":"20000000-0013-4000-8000-999999999995",
                 "observedAt":"2026-11-06T17:00:00Z","people":[
                 {"email":"avery@example.com","displayName":"Avery","managerEmail":null}]}
                """).Id), app.Status);
            var first = localChange == "team"
                ? app.CreateTeamNote("avery@example.com", "First", "Local original")!.Id
                : app.CreatePersonNote("avery@example.com", "First", "Local original")!.Id;
            var second = deliveredTeamNote
                ? app.CreateTeamNote("avery@example.com", "Second", "Provider original")!.Id
                : app.CreatePersonNote("avery@example.com", "Second", "Provider original")!.Id;
            var secondPath = Path.Combine(folder, deliveredTeamNote ? "team-notes" : "notes", second + ".md");
            var validDelivery = File.ReadAllText(secondPath).Replace("Provider original", "Provider delivered");
            var delivered = invalidDelivery ? validDelivery.Replace("avery@example.com", "unknown@example.com") : validDelivery;
            publisher.PublicationPath = localChange == "generation" ? Path.Combine(folder, "workspace.json") :
                Path.Combine(folder, localChange == "team" ? "team-notes" : "notes", first + ".md");
            publisher.Deliver = () => File.WriteAllText(secondPath, delivered);
            var saved = localChange switch
            {
                "person" => app.EditPersonNote(first, "First", "Local saved"),
                "team" => app.EditTeamNote(first, "First", "Local saved"),
                _ => app.EditFavorite("avery@example.com", true)
            };
            Assert.True(saved, app.Status);
            Assert.Null(publisher.Deliver);
            Assert.Equal(delivered, File.ReadAllText(secondPath));
            Assert.Equal(!invalidDelivery, app.RefreshWorkspace());
            Assert.Equal(!invalidDelivery, app.IsWorkspaceCurrent);
            Assert.Equal(invalidDelivery ? "Provider original" : "Provider delivered", ReadDelivered());
            if (invalidDelivery)
            {
                Assert.False(EditDelivered("Must not overwrite invalid delivery"));
                Assert.Equal(delivered, File.ReadAllText(secondPath));
                File.WriteAllText(secondPath, validDelivery);
                Assert.True(app.RefreshWorkspace(), app.Status);
                app.DiscardUnsavedChanges(second);
            }
            using (File.Open(secondPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.False(EditDelivered("A retained unsaved draft"));
            Assert.False(app.CanSafelyClose);
            File.WriteAllText(secondPath, validDelivery + "\nNewer provider writing");
            Assert.True(app.RefreshWorkspace(), app.Status);
            Assert.False(app.CanSafelyClose);
            Assert.False(EditDelivered("A retained draft cannot overwrite the refreshed version"));
            app.DiscardUnsavedChanges(second);
            Assert.True(EditDelivered("Edited delivered version"), app.Status);
            using var reopened = incremental ? ApplicationWorkflow.CreateIncrementalFileBacked() :
                ApplicationWorkflow.CreateFileBacked();
            Assert.True(reopened.OpenWorkspace(folder), reopened.Status);
            Assert.Equal("Edited delivered version", deliveredTeamNote
                ? reopened.GetTeamNotes("avery@example.com").Single(n => n.Id == second).Markdown
                : reopened.GetPersonNotes("avery@example.com").Single(n => n.Id == second).Markdown);

            bool EditDelivered(string markdown) => deliveredTeamNote
                ? app.EditTeamNote(second, "Second", markdown) : app.EditPersonNote(second, "Second", markdown);
            string ReadDelivered() => deliveredTeamNote
                ? app.GetTeamNotes("avery@example.com").Single(n => n.Id == second).Markdown
                : app.GetPersonNotes("avery@example.com").Single(n => n.Id == second).Markdown;
        }
        finally
        {
            app.Dispose();
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    private sealed class DeliveringPublisher : IWorkspaceFilePublisher
    {
        public Action? Deliver { get; set; }
        public string? PublicationPath { get; set; }
        public void Publish(string staged, string destination, string? backup)
        {
            var delivery = destination == PublicationPath ? Deliver : null;
            if (delivery is not null) Deliver = null;
            delivery?.Invoke();
            if (File.Exists(destination)) File.Replace(staged, destination, backup);
            else File.Move(staged, destination);
        }
    }
}
