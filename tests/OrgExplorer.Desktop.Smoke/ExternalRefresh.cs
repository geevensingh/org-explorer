using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void RunExternalRefreshFlow(string executable, string root)
    {
        var folder = Path.Combine(root, "external-workspace");
        var import = Path.Combine(root, "external-import.json");
        var photo = Path.Combine(root, "external.png");
        CreatePhotoFixture(photo);
        File.WriteAllText(import, """
            {"schemaVersion":1,"batchId":"80000000-0000-4000-8000-000000000001",
            "observedAt":"2026-11-01T17:00:00Z","people":[
            {"email":"avery@example.com","displayName":"Avery Example","managerEmail":"casey@example.com","photoPath":"external.png"},
            {"email":"casey@example.com","displayName":"Casey Example","managerEmail":null}]}
            """);
        using var process = Launch(executable);
        try
        {
            var window = Window(process);
            Set(window, "WorkspacePath", folder);
            Click(window, "OpenWorkspace");
            Set(window, "ImportPath", import);
            Click(window, "PreviewImport");
            Await(() => Find(window, "ApplyImport").Current.IsEnabled);
            Click(window, "ApplyImport");
            OpenPerson(window, "casey@example.com");
            AssertPhotoVisible(window);
            Click(window, "NewPersonNote");
            Await(() => Find(window, "PersonNoteMarkdown").Current.IsEnabled);
            Set(window, "PersonNoteMarkdown", "# Saved context");
            AwaitLocalWorkIdle(window);
            Click(window, "NewTeamNote");
            Set(window, "TeamNoteMarkdown", "# Saved owned Team context");
            NavigateToManager(window, "casey@example.com");
            Click(window, "NewTeamNote");
            Set(window, "TeamNoteTitle", "Manager context");
            Set(window, "TeamNoteMarkdown", "# Saved inherited Team context");
            OpenPerson(window, "casey@example.com");
            SelectNote(window, "New Person note");
            var noteFile = Directory.GetFiles(Path.Combine(folder, "notes"), "*.md").Single();
            var teamFiles = Directory.GetFiles(Path.Combine(folder, "team-notes"), "*.md");
            var ownedTeamFile = teamFiles.Single(p => File.ReadAllText(p).Contains("avery@example.com"));
            var inheritedTeamFile = teamFiles.Single(p => File.ReadAllText(p).Contains("casey@example.com"));
            var teamMetadataFile = Directory.GetFiles(folder, "team-context.json", SearchOption.AllDirectories).Single();
            var personFile = Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories)
                .Single(p => p.Contains(Path.DirectorySeparatorChar + "people" + Path.DirectorySeparatorChar)
                    && JsonNode.Parse(File.ReadAllText(p))?["email"]?.GetValue<string>() == "avery@example.com");
            var person = JsonNode.Parse(File.ReadAllText(personFile))!;
            person["preferredName"] = "External Avery";
            File.WriteAllText(personFile, person.ToJsonString());
            File.WriteAllText(noteFile, File.ReadAllText(noteFile).Replace("# Saved context", "# External context"));
            File.WriteAllText(ownedTeamFile, File.ReadAllText(ownedTeamFile).Replace("# Saved owned Team context", "# External owned Team context"));
            File.WriteAllText(inheritedTeamFile, File.ReadAllText(inheritedTeamFile).Replace("# Saved inherited Team context", "# External inherited Team context"));
            File.WriteAllText(teamMetadataFile, new JsonObject { ["casey@example.com"] = Guid.NewGuid() }.ToJsonString());
            Await(() => Find(window, "ProfileHeading").Current.Name == "External Avery");
            Await(() => ((ValuePattern)Find(window, "PersonNoteMarkdown").GetCurrentPattern(ValuePattern.Pattern)).Current.Value == "# External context");
            SelectTeamNote(window, "New Team note");
            Await(() => Value(window, "TeamNoteMarkdown") == "# External owned Team context");
            Await(() => Find(window, "InheritedTeamSummary").Current.Name.Contains("Needs review"));
            Await(() => ((TextPattern)Find(window, "InheritedTeamReading")
                .GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(-1).Contains("External inherited Team context"));
            var image = Directory.GetFiles(folder, person["photoPath"]!.GetValue<string>(), SearchOption.AllDirectories).Single();
            var timestamp = File.GetLastWriteTimeUtc(image);
            byte[] greenPixels = [0, 255, 0, 255, 0, 255, 0, 255, 0, 255, 0, 255, 0, 255, 0, 255];
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, greenPixels, 8)));
            using (var file = File.Create(image)) encoder.Save(file);
            File.SetLastWriteTimeUtc(image, timestamp);
            ((ScrollPattern)Find(window, "ProfileScroll").GetCurrentPattern(ScrollPattern.Pattern))
                .SetScrollPercent(ScrollPattern.NoScroll, 0);
            Await(() =>
            {
                var bounds = Find(window, "PersonPhoto").Current.BoundingRectangle;
                if (bounds.IsEmpty || Find(window, "PersonPhoto").Current.IsOffscreen) return false;
                var dc = GetDC(IntPtr.Zero);
                try
                {
                    var color = GetPixel(dc, (int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2));
                    return (color & 0xff) < 20 && ((color >> 8) & 0xff) > 240 && ((color >> 16) & 0xff) < 20;
                }
                finally { ReleaseDC(IntPtr.Zero, dc); }
            });
            var valid = File.ReadAllText(personFile);
            person["managerEmail"] = "missing@example.com";
            File.WriteAllText(personFile, person.ToJsonString());
            File.WriteAllText(noteFile, File.ReadAllText(noteFile).Replace("# External context", "# Pending context"));
            Await(() => Find(window, "WorkspaceFreshness").Current.Name.Contains("STALE"));
            if (Find(window, "ProfileHeading").Current.Name != "External Avery" ||
                !Find(window, "NavigateManager").Current.Name.Contains("Casey Example"))
                throw new InvalidOperationException("Invalid Workspace fabricated a new Person or reporting root.");
            AssertValue(window, "PersonNoteMarkdown", "# External context");
            if (Find(window, "PreferredName").Current.IsEnabled)
                throw new InvalidOperationException("Stale Workspace still claims writable current Person state.");
            File.WriteAllText(personFile, valid);
            Await(() => Find(window, "WorkspaceFreshness").Current.Name.Contains("Current validated"));
            Await(() => ((ValuePattern)Find(window, "PersonNoteMarkdown").GetCurrentPattern(ValuePattern.Pattern)).Current.Value == "# Pending context");
            var validMetadata = File.ReadAllText(teamMetadataFile);
            File.WriteAllText(teamMetadataFile, "Incomplete Team context delivery");
            File.WriteAllText(ownedTeamFile, File.ReadAllText(ownedTeamFile).Replace("# External owned Team context", "# Pending owned Team context"));
            Await(() => Find(window, "WorkspaceFreshness").Current.Name.Contains("STALE"));
            AssertValue(window, "TeamNoteMarkdown", "# External owned Team context");
            if (Find(window, "TeamNoteMarkdown").Current.IsEnabled || Find(window, "NewTeamNote").Current.IsEnabled)
                throw new InvalidOperationException("Invalid Team metadata left stale Team writing editable.");
            if (File.ReadAllText(teamMetadataFile) != "Incomplete Team context delivery")
                throw new InvalidOperationException("Invalid Team metadata was silently repaired.");
            File.WriteAllText(teamMetadataFile, validMetadata);
            Await(() => Find(window, "WorkspaceFreshness").Current.Name.Contains("Current validated"));
            Await(() => Value(window, "TeamNoteMarkdown") == "# Pending owned Team context");
            if (!Find(window, "InheritedTeamSummary").Current.Name.Contains("Needs review"))
                throw new InvalidOperationException("Recovery silently confirmed externally changed Team context.");
            Close(process);
            Console.WriteLine("PASS: real folder events update actual profile, Person/Team Markdown, inherited Team review metadata, same-path photo pixels, stale warnings, and coherent recovery without reload.");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(true); process.WaitForExit(5000); }
        }
    }
}
