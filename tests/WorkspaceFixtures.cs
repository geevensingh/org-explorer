using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using OrgExplorer.Workflow;

namespace OrgExplorer.TestFixtures;

internal static class WorkspaceFixtures
{
    private static readonly Lazy<Dictionary<string, byte[]>> Current = new(() =>
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
            ".test-workspaces", "current-fixture-" + Guid.NewGuid().ToString("N")));
        try
        {
            CreateCurrent(root);
            return Bytes(root);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    });

    internal static void CopyCurrent(string destination)
    {
        foreach (var (relative, bytes) in Current.Value)
        {
            var path = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
    }

    private static void CreateCurrent(string destination)
    {
        using var app = ApplicationWorkflow.CreateFileBacked();
        Require(app.OpenWorkspace(destination));
        Require(app.ApplyImport(app.PreviewImport("""
            {"schemaVersion":1,"batchId":"14000000-0000-4000-8000-000000000001",
             "observedAt":"2026-11-01T17:00:00Z","producer":"Fictitious legacy fixture","people":[
             {"email":"avery@example.com","displayName":"Avery Example","preferredName":"Ave","alias":"avery",
              "jobTitle":"Engineer","department":"Research","officeBuilding":"North","officeNumber":"007B",
              "officeState":"WA","officeCountry":"US","linkedInProfileUrl":"https://www.linkedin.com/in/fictitious-avery",
              "managerEmail":"morgan@example.com","isFavorite":true},
             {"email":"morgan@example.com","displayName":"Morgan Example","managerEmail":"casey@example.com","isFavorite":true},
             {"email":"casey@example.com","displayName":"Casey Example","managerEmail":null},
             {"email":"alex@example.com","displayName":"Alex Example","managerEmail":"casey@example.com"},
             {"email":"former@example.com","displayName":"Former Example","managerEmail":null,"archived":true,"isFavorite":true}]}
            """).Id));
        var photo = Path.Combine(destination, "fixture-source.png");
        File.WriteAllBytes(photo, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/jP8P8/AAb+Av4jsC+eAAAAAElFTkSuQmCC"));
        try { Require(app.EditPhoto("avery@example.com", photo)); }
        finally { File.Delete(photo); }
        Require(app.CreatePersonNote("avery@example.com", "Private Person context", "# Person writing\n**Preserved** café.") is not null);
        var archived = app.CreatePersonNote("former@example.com", "Archived writing", "Retained, not deleted.");
        Require(archived is not null);
        Require(app.ArchivePersonNote(archived!.Id));
        var team = app.CreateTeamNote("morgan@example.com", "Former team", "# Frozen team\nOld team writing.");
        Require(team is not null);
        Require(app.CreateTeamNote("casey@example.com", "Ancestor team", "All-ancestor writing.") is not null);
        Require(app.ApplyImport(app.PreviewImport("""
            {"schemaVersion":1,"batchId":"14000000-0000-4000-8000-000000000002",
             "observedAt":"2026-11-03T17:00:00Z","producer":"Fictitious transfer","people":[
             {"email":"avery@example.com","managerEmail":"alex@example.com","officeNumber":"0009C"}]}
            """).Id));
        Require(app.EditTeamNote(team!.Id, team.Title, "Later writing must not rewrite frozen history."));

        void Require(bool succeeded)
        {
            if (!succeeded) throw new InvalidOperationException(app.Status);
        }
    }

    internal static IDisposable LockLocalStore(string root)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "workspace.sqlite"),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 1
        }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "BEGIN IMMEDIATE;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    internal static void JoinSnapshot(IApplicationWorkflow source, IApplicationWorkflow receiver, string transport)
    {
        if (!source.ConfigureSynchronizationFolder(transport) || !source.SyncNow())
            throw new InvalidOperationException(source.Synchronization.Error);
        if (!receiver.ConfigureSynchronizationFolder(transport, joinExistingWorkspace: true) || !receiver.SyncNow())
            throw new InvalidOperationException(receiver.Synchronization.Error);
    }

    internal static void DeliverSnapshot(IApplicationWorkflow source, IApplicationWorkflow receiver)
    {
        if (!source.SyncNow()) throw new InvalidOperationException(source.Synchronization.Error);
        if (!receiver.SyncNow()) throw new InvalidOperationException(receiver.Synchronization.Error);
    }

    internal static void RewriteSnapshot(string path, Action<JsonObject> change)
    {
        var envelope = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        var content = JsonNode.Parse(Convert.FromBase64String(envelope["content"]!.GetValue<string>()))!.AsObject();
        change(content);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(content);
        envelope["content"] = Convert.ToBase64String(bytes);
        envelope["sha256"] = Convert.ToHexString(SHA256.HashData(bytes));
        File.WriteAllText(path, envelope.ToJsonString());
    }

    internal static string LatestSnapshot(IApplicationWorkflow app) =>
        Path.Combine(app.Synchronization.Folder!,
            $"{app.Synchronization.WorkspaceId:D}-{app.Synchronization.LocalRevision:D}.oesnapshot");

    internal static void EditSnapshotRow(JsonObject content, string family, string key, Action<JsonNode> change)
    {
        var row = content[family]!.AsArray().Single(row => row!["key"]!.GetValue<string>() == key)!;
        var value = JsonNode.Parse(row["value"]!.GetValue<string>())!;
        change(value);
        row["value"] = value.ToJsonString();
    }

    internal static void CopyVersionOne(string destination) => CopyVersion(1, destination);

    internal static void CopyVersion(int version, string destination)
    {
        System.IO.Compression.ZipFile.ExtractToDirectory(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", $"workspace-v{(version == 3 ? 2 : version)}.zip"), destination);
        if (version != 3) return;
        var path = Path.Combine(destination, "workspace.json");
        var selector = JsonNode.Parse(File.ReadAllBytes(path))!;
        selector["schemaVersion"] = 3;
        File.WriteAllText(path, selector.ToJsonString());
    }

    internal static void Copy(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    internal static Dictionary<string, byte[]> Bytes(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Where(p => !p.EndsWith(".workspace.lock", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(p => Path.GetRelativePath(root, p), File.ReadAllBytes);
}
