using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OrgExplorer.TestFixtures;

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
            OpenImportView(window);
            Set(window, "ImportPath", import);
            Click(window, "PreviewImport");
            Await(() => Find(window, "ApplyImport").Current.IsEnabled);
            Click(window, "ApplyImport");
            Await(() => Find(window, "Status").Current.Name.Contains("Import applied"));
            ReturnToPeople(window);
            OpenPerson(window, "casey@example.com");
            AssertPhotoVisible(window);
            Click(window, "NewPersonNote");
            Await(() => Find(window, "PersonNoteMarkdown").Current.IsEnabled);
            Set(window, "PersonNoteMarkdown", "# Saved context");
            AwaitLocalWorkIdle(window);
            Click(window, "NewTeamNote");
            Set(window, "TeamNoteMarkdown", "# Saved owned Team context");
            NavigateToManager(window, "casey@example.com");
            BeginProfileEditing(window);
            Set(window, "JobTitle", "Original manager role");
            SaveProfileEditing(window);
            Click(window, "NewTeamNote");
            Set(window, "TeamNoteTitle", "Manager context");
            Set(window, "TeamNoteMarkdown", "# Saved inherited Team context");
            OpenPerson(window, "casey@example.com");
            SelectNote(window, "New Person note");
            var noteFile = Directory.GetFiles(Path.Combine(folder, "notes"), "*.md").Single();
            var teamFiles = Directory.GetFiles(Path.Combine(folder, "team-notes"), "*.md");
            var ownedTeamFile = teamFiles.Single(p => File.ReadAllText(p).Contains("avery@example.com"));
            var inheritedTeamFile = teamFiles.Single(p => File.ReadAllText(p).Contains("casey@example.com"));
            var documents = WorkspaceFixtures.IndexedDocuments(folder);
            var teamMetadataFile = documents["teamContext:casey@example.com"];
            var personFile = documents["people:avery@example.com"];
            var person = JsonNode.Parse(File.ReadAllText(personFile))!;
            person["preferredName"] = "External Avery";
            File.WriteAllText(personFile, person.ToJsonString());
            File.WriteAllText(noteFile, File.ReadAllText(noteFile).Replace("# Saved context", "# External context"));
            File.WriteAllText(ownedTeamFile, File.ReadAllText(ownedTeamFile).Replace("# Saved owned Team context", "# External owned Team context"));
            File.WriteAllText(inheritedTeamFile, File.ReadAllText(inheritedTeamFile).Replace("# Saved inherited Team context", "# External inherited Team context"));
            File.WriteAllText(teamMetadataFile, new JsonObject { ["casey@example.com"] = Guid.NewGuid() }.ToJsonString());
            Await(() => Find(window, "ProfileHeading").Current.Name == "External Avery");
            AssertNoteView(window, "Person", editing: false);
            Await(() => ReadingText(window).Contains("External context"));
            CompleteStorageMaintenance(window);
            SelectTeamNote(window, "New Team note");
            AssertNoteView(window, "Team", editing: false);
            Await(() => TeamReadingText(window).Contains("External owned Team context"));
            Await(() => Find(window, "InheritedTeamSummary").Current.Name.Contains("Needs review"));
            Await(() => ((TextPattern)Find(window, "InheritedTeamReading")
                .GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(-1).Contains("External inherited Team context"));
            var image = documents["photos:" + person["photoPath"]!.GetValue<string>()];
            var timestamp = File.GetLastWriteTimeUtc(image);
            byte[] greenPixels = [0, 255, 0, 255, 0, 255, 0, 255, 0, 255, 0, 255, 0, 255, 0, 255];
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, greenPixels, 8)));
            using (var file = File.Create(image)) encoder.Save(file);
            File.SetLastWriteTimeUtc(image, timestamp);
            var scroll = (ScrollPattern)Find(window, "ProfileScroll").GetCurrentPattern(ScrollPattern.Pattern);
            if (scroll.Current.VerticallyScrollable) scroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
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
            if (!ReadingText(window).Contains("External context") || ReadingText(window).Contains("Pending context"))
                throw new InvalidOperationException("Invalid delivery changed the retained Person-note reading.");
            if (Find(window, "EditPreferredName").Current.IsEnabled)
                throw new InvalidOperationException("Stale Workspace still claims writable current Person state.");
            File.WriteAllText(personFile, valid);
            Await(() => Find(window, "WorkspaceFreshness").Current.Name.Contains("Current validated"));
            Await(() => ReadingText(window).Contains("Pending context"));
            if (!Find(window, "InheritedTeamSummary").Current.Name.Contains("Needs review"))
                throw new InvalidOperationException("Recovery silently confirmed externally changed Team context.");
            CompleteStorageMaintenance(window);
            AssertFirstAutosaveDeliveryConflict(window, "Person", noteFile, personFile);
            AssertFirstAutosaveDeliveryConflict(window, "Team", ownedTeamFile, personFile);
            AssertExplicitProfileDeliveryConflict(window, personFile);
            Close(process);
            Console.WriteLine("PASS: real folder events update profile/notes/context/photo pixels, reject first Person/Team autosave conflicts without losing either writing, and recover through explicit discard.");
        }
        finally
        {
            StopApplication(process);
        }
    }

    private static void AssertExplicitProfileDeliveryConflict(AutomationElement window, string personFile)
    {
        AwaitLocalWorkIdle(window);
        BeginProfileEditing(window);
        var editor = (ValuePattern)Find(window, "OfficeNumber").GetCurrentPattern(ValuePattern.Pattern);
        var local = "Local profile draft";
        editor.SetValue(local);
        var profile = JsonNode.Parse(File.ReadAllText(personFile))!;
        profile["officeNumber"] = "Delivered office";
        profile["preferredName"] = "Delivered while typing profile";
        File.WriteAllText(personFile, profile.ToJsonString());
        var delivered = File.ReadAllBytes(personFile);
        var timer = Stopwatch.StartNew();
        var edits = 0;
        while (Find(window, "ProfileHeading").Current.Name != "Delivered while typing profile")
        {
            if (timer.Elapsed >= TimeSpan.FromSeconds(20))
                throw new TimeoutException("Profile delivery was not accepted while typing within 20 seconds.");
            local = $"Local profile draft {++edits}";
            editor.SetValue(local);
            Thread.Sleep(10);
        }
        AssertValue(window, "OfficeNumber", local);
        Click(window, "SaveProfile");
        Await(() => Find(window, "Status").Current.Name.Contains("changed externally", StringComparison.Ordinal));
        AssertRejectedRetry(window, "RetryProfileSave", "Saving complete profile…",
            () => Find(window, "Status").Current.Name.Contains("changed externally", StringComparison.Ordinal) &&
            Find(window, "Status").Current.Name.Contains("NOT saved", StringComparison.Ordinal) &&
            Find(window, "WorkflowActivity").Current.Name.StartsWith("Pending local edits", StringComparison.Ordinal) &&
            Find(window, "SaveProfile").Current.IsEnabled && Find(window, "RetryProfileSave").Current.IsEnabled &&
            window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "ProfileLeaveDecision")) is null);
        AssertValue(window, "OfficeNumber", local);
        if (Find(window, "Email").Current.Name != "avery@example.com")
            throw new InvalidOperationException("Rejected profile retry changed its owner.");
        if (!File.ReadAllBytes(personFile).SequenceEqual(delivered))
            throw new InvalidOperationException("Explicit profile save or retry overwrote the delivered field.");
        Click(window, "DiscardProfileDraft");
        AssertProfileValue(window, "OfficeNumber", "Delivered office");
        CompleteStorageMaintenance(window);
        AwaitLocalWorkIdle(window);
        if (!File.ReadAllBytes(personFile).SequenceEqual(delivered))
            throw new InvalidOperationException("Profile discard rewrote delivered bytes.");
        BeginProfileEditing(window);
        Set(window, "OfficeNumber", "Intentional delivered-version edit");
        SaveProfileEditing(window);
    }

    private static void AssertFirstAutosaveDeliveryConflict(
        AutomationElement window, string kind, string noteFile, string personFile)
    {
        AwaitLocalWorkIdle(window);
        SelectTab(window, $"{kind}NoteEditTab");
        var editor = (ValuePattern)Find(window, $"{kind}NoteMarkdown").GetCurrentPattern(ValuePattern.Pattern);
        var status = Find(window, $"{kind}NoteStatus");
        var original = File.ReadAllText(noteFile);
        var local = $"# Local {kind} draft";
        editor.SetValue(local);
        if (!status.Current.Name.StartsWith("Pending", StringComparison.Ordinal) || File.ReadAllText(noteFile) != original)
            throw new InvalidOperationException($"The first {kind} autosave was not still pending before delivery.");
        var delivered = original + $"\r\n# Delivered {kind} writing\r\n";
        File.WriteAllText(noteFile, delivered);
        var bytes = File.ReadAllBytes(noteFile);
        var marker = $"Delivered while typing {kind}";
        var profile = JsonNode.Parse(File.ReadAllText(personFile))!;
        profile["preferredName"] = marker;
        File.WriteAllText(personFile, profile.ToJsonString());

        var timer = Stopwatch.StartNew();
        var edits = 0;
        while (Find(window, "ProfileHeading").Current.Name != marker)
        {
            if (timer.Elapsed >= TimeSpan.FromSeconds(20))
                throw new TimeoutException($"External {kind} delivery was not accepted while typing within 20 seconds.");
            if (status.Current.Name.Contains("NOT saved", StringComparison.Ordinal) ||
                !File.ReadAllBytes(noteFile).SequenceEqual(bytes))
                throw new InvalidOperationException($"The first {kind} save ran before delivery acceptance was observed.");
            local = $"# Local {kind} draft {++edits}";
            editor.SetValue(local);
            Thread.Sleep(10);
        }
        Await(() => status.Current.Name.Contains("changed externally", StringComparison.Ordinal));
        AssertValue(window, $"{kind}NoteMarkdown", local);
        if (!File.ReadAllBytes(noteFile).SequenceEqual(bytes))
            throw new InvalidOperationException($"The first {kind} autosave overwrote delivered writing.");
        Click(window, "NavigateManager");
        Await(() => status.Current.Name.Contains("before leaving or closing", StringComparison.Ordinal));
        if (Find(window, "Email").Current.Name != "avery@example.com")
            throw new InvalidOperationException($"The rejected {kind} conflict navigation changed its owner.");
        AssertRejectedRetry(window, $"Retry{kind}NoteSave", $"Saving private {kind} note…",
            () => status.Current.Name.Contains("changed externally", StringComparison.Ordinal) &&
            status.Current.Name.Contains("NOT saved", StringComparison.Ordinal) &&
            Find(window, "WorkflowActivity").Current.Name.StartsWith("Pending local edits", StringComparison.Ordinal) &&
            Find(window, "EditProfile").Current.IsEnabled && Find(window, $"Retry{kind}NoteSave").Current.IsEnabled &&
            window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "ProfileLeaveDecision")) is null);
        AssertValue(window, $"{kind}NoteMarkdown", local);
        if (Find(window, "Email").Current.Name != "avery@example.com")
            throw new InvalidOperationException($"Rejected {kind} retry changed its owner.");
        if (!File.ReadAllBytes(noteFile).SequenceEqual(bytes))
            throw new InvalidOperationException($"{kind} retry overwrote delivered writing.");
        Click(window, $"Discard{kind}NoteDraft");
        CompleteStorageMaintenance(window);
        var deliveredMarkdown = delivered[(delivered.IndexOf("\n---\n", StringComparison.Ordinal) + 5)..];
        Await(() => Find(window, $"{kind}NoteHeading").Current.Name.Length > 0 &&
            Find(window, "WorkflowActivity").Current.Name == "Ready");
        AssertNoteView(window, kind, editing: false);
        SelectTab(window, $"{kind}NoteEditTab");
        Await(() => Value(window, $"{kind}NoteMarkdown") == deliveredMarkdown);
        AwaitLocalWorkIdle(window);
        if (!File.ReadAllBytes(noteFile).SequenceEqual(bytes))
            throw new InvalidOperationException($"{kind} discard rewrote the delivered document.");
        Set(window, $"{kind}NoteMarkdown", $"# Intentional {kind} edit after discard");
        Await(() => status.Current.Name.Contains("saved locally", StringComparison.Ordinal));
        if (!File.ReadAllText(noteFile).EndsWith($"# Intentional {kind} edit after discard", StringComparison.Ordinal))
            throw new InvalidOperationException($"An intentional {kind} edit after accepting delivery did not save.");
    }

    private static void CompleteStorageMaintenance(AutomationElement window, Action<Func<bool>>? awaitCompletion = null)
    {
        Click(window, "OpenSettings");
        Click(window, "RetryWorkspace");
        (awaitCompletion ?? Await)(() => Find(window, "Status").Current.Name.Contains("Storage maintenance completed") &&
            Find(window, "WorkflowActivity").Current.Name == "Ready");
        Click(window, "OpenSettings");
    }

    private static void AssertRejectedRetry(AutomationElement window, string button, string savingActivity,
        Func<bool> isRejected)
    {
        var retry = ControlReadiness.AwaitPattern(() => Find(window, button), control => control.Current.IsEnabled,
            control => (InvokePattern)control.GetCurrentPattern(InvokePattern.Pattern), Await);
        RetryRejection.AssertCompleted(
            changed => new ActivitySubscription(Find(window, "WorkflowActivity"), changed),
            retry.Invoke, savingActivity, isRejected, Await);
    }

    private sealed class ActivitySubscription : IDisposable
    {
        private readonly AutomationElement activity;
        private readonly AutomationPropertyChangedEventHandler handler;

        internal ActivitySubscription(AutomationElement activity, Action<string> changed)
        {
            this.activity = activity;
            handler = (_, e) =>
            {
                if (e.NewValue is string name) changed(name);
            };
            Automation.AddAutomationPropertyChangedEventHandler(activity, TreeScope.Element, handler,
                AutomationElement.NameProperty);
        }

        public void Dispose() => Automation.RemoveAutomationPropertyChangedEventHandler(activity, handler);
    }
}
