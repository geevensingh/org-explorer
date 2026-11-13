using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void VerifyUpdateYield(Window window, string settings, HeldNotePublisher publisher)
    {
        VerifyUnconfirmedPreferredNameUpdate(window, settings);
        VerifyUpdateYieldPublication(window, settings, publisher, preferredName: false);
        VerifyUpdateYieldPublication(window, settings, publisher, preferredName: true);
    }

    private static void VerifyUnconfirmedPreferredNameUpdate(Window window, string settings)
    {
        var fixtureDirectory = Environment.GetEnvironmentVariable("ORG_EXPLORER_UPDATE_FIXTURE")
            ?? throw new InvalidOperationException("The update drain requires the existing ephemeral trusted-update build.");
        var folder = Path.Combine(settings, "unconfirmed-name-workspace");
        using (var seed = ApplicationWorkflow.CreateFileBacked())
        {
            if (!seed.OpenWorkspace(folder) || !seed.ApplyImport(seed.PreviewImport("""
                {"schemaVersion":1,"batchId":"39000000-0000-4000-8000-000000000004",
                 "observedAt":"2026-11-01T17:00:00Z","people":[
                 {"email":"avery@example.com","displayName":"Avery","managerEmail":null}]}
                """).Id))
                throw new InvalidOperationException("Unconfirmed name fixture failed: " + seed.Status);
        }
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (Control<FrameworkElement>("SettingsPanel").Visibility != Visibility.Visible) Invoke("OpenSettingsButton");
        ChooseCompiledWorkspace(window, folder);
        PumpUntil(() => Control<TextBlock>("StatusText").Text == "Workspace opened.");
        var tree = Control<TreeView>("PeopleList");
        tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(null!, tree.Items[0], TreeView.SelectedItemChangedEvent));
        var original = WorkspaceFixtures.Bytes(folder);
        using var configuration = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtureDirectory, "trust.json")));
        var endpoint = new Uri(configuration.RootElement.GetProperty("manifestUrl").GetString()!);
        using var fixture = new SignedReleaseFixture();
        fixture.Key.ImportFromPem(File.ReadAllText(Path.Combine(fixtureDirectory, "ephemeral-private.pem")));
        fixture.Package = ControlledFullPackage("9999.0.0");
        File.WriteAllBytes(Path.Combine(fixtureDirectory, "package.nupkg"), fixture.Package);
        using var server = Task.Run(() => new LocalReleaseServer(
            () => fixture.Sign(fixture.Manifest("9999.0.0")), endpoint.Port)).GetAwaiter().GetResult();
        var feedRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.HoldResponse = feedRelease.Task;
        var handoff = Path.Combine(fixtureDirectory, "handoff.json");
        File.Delete(handoff);
        try
        {
            Invoke("OpenSettingsButton");
            Invoke("CheckForUpdatesButton");
            PumpUntil(() => server.RequestArrived.Task.IsCompleted);
            Invoke("OpenSettingsButton");
            Invoke("EditPreferredNameButton");
            Control<TextBox>("PreferredName").Text = "Unconfirmed update entry";
            feedRelease.TrySetResult();
            PumpUntil(() => Control<Button>("CheckForUpdatesButton").IsEnabled);
            if (!Control<TextBlock>("UpdateGuidance").Text.Contains("local writing was NOT safely saved", StringComparison.Ordinal) ||
                Control<FrameworkElement>("PreferredNameEditorPanel").Visibility != Visibility.Visible ||
                Control<TextBox>("PreferredName").Text != "Unconfirmed update entry" || File.Exists(handoff))
                throw new InvalidOperationException("The actual update command did not refuse unconfirmed Preferred-name writing without handoff.");
            AssertWorkspaceBytes(folder, original);
            using var reopened = ApplicationWorkflow.CreateFileBacked();
            if (!reopened.OpenWorkspace(folder) || reopened.GetRelationships("avery@example.com")!.Person.PreferredName is not null)
                throw new InvalidOperationException("Update draining published an unconfirmed Preferred name.");
            Invoke("CancelPreferredNameButton");
        }
        finally { feedRelease.TrySetResult(); }
        Console.WriteLine("PASS: actual signed update command refuses unconfirmed Preferred-name writing, retains its entry and leaves durable bytes unchanged without handoff.");
    }

    private static void VerifyUpdateYieldPublication(Window window, string settings, HeldNotePublisher publisher, bool preferredName)
    {
        var fixtureDirectory = Environment.GetEnvironmentVariable("ORG_EXPLORER_UPDATE_FIXTURE")
            ?? throw new InvalidOperationException("The update-yield experiment requires the existing ephemeral trusted-update build.");
        var folder = Path.Combine(settings, preferredName ? "preferred-name-workspace" : "workspace");
        publisher.Entered.Reset();
        publisher.Release.Reset();
        Guid noteId;
        using (var seed = ApplicationWorkflow.CreateFileBacked())
        {
            if (!seed.OpenWorkspace(folder) || !seed.ApplyImport(seed.PreviewImport("""
                    {"schemaVersion":1,"batchId":"39000000-0000-4000-8000-000000000002",
                     "observedAt":"2026-11-01T17:00:00Z","people":[
                     {"email":"avery@example.com","displayName":"Avery","managerEmail":null}]}
                    """).Id))
                throw new InvalidOperationException("Update-yield fixture failed: " + seed.Status);
            noteId = seed.CreatePersonNote("avery@example.com", "Continuing", "Before the update")!.Id;
        }
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (Control<FrameworkElement>("SettingsPanel").Visibility != Visibility.Visible)
            Invoke("OpenSettingsButton");
        ChooseCompiledWorkspace(window, folder);
        PumpUntil(() => Control<TextBlock>("StatusText").Text == "Workspace opened.");
        var tree = Control<TreeView>("PeopleList");
        tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(null!, tree.Items[0], TreeView.SelectedItemChangedEvent));
        var notes = Control<FrameworkElement>("PersonNotes");
        if (!preferredName) ((TabControl)notes.FindName("Views")).SelectedItem = notes.FindName("EditTab");
        var editor = preferredName ? Control<TextBox>("PreferredName") : (TextBox)notes.FindName("MarkdownEditor");
        var file = preferredName ? Path.Combine(folder, "workspace.json") : Path.Combine(folder, "notes", noteId + ".md");
        var original = File.ReadAllBytes(file);
        var finalWriting = preferredName ? "Explicit Preferred name queued in the update yield" : "Final private writing queued in the update yield";

        using var configuration = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtureDirectory, "trust.json")));
        var endpoint = new Uri(configuration.RootElement.GetProperty("manifestUrl").GetString()!);
        using var fixture = new SignedReleaseFixture();
        fixture.Key.ImportFromPem(File.ReadAllText(Path.Combine(fixtureDirectory, "ephemeral-private.pem")));
        fixture.Package = ControlledFullPackage("9999.0.0");
        File.WriteAllBytes(Path.Combine(fixtureDirectory, "package.nupkg"), fixture.Package);
        using var server = Task.Run(() => new LocalReleaseServer(
            () => fixture.Sign(fixture.Manifest("9999.0.0")), endpoint.Port)).GetAwaiter().GetResult();
        var feedRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.HoldResponse = feedRelease.Task;
        var handoff = Path.Combine(fixtureDirectory, "handoff.json");
        File.Delete(handoff);

        var import = Control<Button>("OpenImportButton");
        var enabledProperty = DependencyPropertyDescriptor.FromProperty(UIElement.IsEnabledProperty, typeof(Button));
        var intercepted = false;
        var observed = false;
        var heldAtObservation = false;
        var bytesUnchanged = false;
        var updatePendingAtObservation = false;
        var noEarlyHandoff = false;
        var importBlockedAtObservation = false;
        Exception? experimentFailure = null;
        Task<bool>? noteSave = null;
        bool SavePending() => preferredName ? Control<TextBlock>("WorkflowActivity").Text != "Ready" :
            noteSave is { IsCompleted: false };
        EventHandler intercept = (_, _) =>
        {
            if (import.IsEnabled || intercepted) return;
            intercepted = true;
            // The real handoff disables Import before draining; Normal work runs at its first dispatcher yield.
            window.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    publisher.HoldNextNote = !preferredName;
                    publisher.HoldNextProfile = preferredName;
                    if (preferredName)
                        Invoke("SavePreferredNameButton");
                    else noteSave = (Task<bool>)notes.GetType().GetMethod("FlushPendingDraftAsync")!.Invoke(notes, null)!;
                    if (!publisher.Entered.Wait(TimeSpan.FromSeconds(5)))
                        throw new InvalidOperationException("The queued explicit name/note save did not reach the held real-file publisher.");
                }
                catch (Exception error) { experimentFailure = error; }
                window.Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        updatePendingAtObservation = !Control<Button>("CheckForUpdatesButton").IsEnabled;
                        noEarlyHandoff = !File.Exists(handoff);
                        importBlockedAtObservation = !Control<Button>("OpenImportButton").IsEnabled;
                        heldAtObservation = publisher.Entered.IsSet && !publisher.Release.IsSet && SavePending();
                        bytesUnchanged = File.ReadAllBytes(file).SequenceEqual(original);
                    }
                    catch (Exception error) { experimentFailure = error; }
                    finally
                    {
                        observed = true;
                        publisher.Release.Set();
                    }
                }, DispatcherPriority.ContextIdle);
            }, DispatcherPriority.Normal);
        };
        enabledProperty.AddValueChanged(import, intercept);
        try
        {
            Invoke("OpenSettingsButton");
            Invoke("CheckForUpdatesButton");
            PumpUntil(() => server.RequestArrived.Task.IsCompleted);
            Invoke("OpenSettingsButton");
            if (preferredName) Invoke("EditPreferredNameButton");
            editor.Text = finalWriting;
            if (!preferredName) Invoke("OpenSettingsButton");
            feedRelease.TrySetResult();
            PumpUntil(() => observed);
            if (experimentFailure is not null) throw experimentFailure;
            PumpUntil(() => !SavePending() && Control<Button>("CheckForUpdatesButton").IsEnabled);
            if (noteSave is not null && !noteSave.GetAwaiter().GetResult() || !intercepted || !heldAtObservation ||
                !bytesUnchanged || !updatePendingAtObservation || !noEarlyHandoff || !importBlockedAtObservation)
                throw new InvalidOperationException("The experiment did not preserve the intended yield/publication barriers.");
        }
        finally
        {
            feedRelease.TrySetResult();
            publisher.Release.Set();
            enabledProperty.RemoveValueChanged(import, intercept);
            PumpUntil(() => !SavePending());
        }
        using var reopened = ApplicationWorkflow.CreateFileBacked();
        if (!reopened.OpenWorkspace(folder)) throw new InvalidOperationException(reopened.Status);
        var durable = preferredName ? reopened.GetRelationships("avery@example.com")!.Person.PreferredName == finalWriting :
            File.ReadAllText(file).Contains(finalWriting, StringComparison.Ordinal);
        var handedOff = File.Exists(handoff);
        if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
            throw new InvalidOperationException("The update-yield experiment must never show a window or create its native handle.");
        Console.WriteLine($"UPDATE-YIELD: preferredName={preferredName}; intercepted={intercepted}; heldAtObservation={heldAtObservation}; " +
            $"bytesUnchangedBeforeRelease={bytesUnchanged}; updatePendingBeforeRelease={updatePendingAtObservation}; " +
            $"noEarlyHandoff={noEarlyHandoff}; finalWritingDurable={durable}; handoff={handedOff}; nativeHandle=0");
        if (!durable || !handedOff)
            throw new InvalidOperationException("The single update action must wait for the explicit name/note save admitted during its yield, then hand off the durable writing.");
        using var request = JsonDocument.Parse(File.ReadAllText(handoff));
        var hash = request.RootElement.GetProperty("workspaceFiles").GetProperty(Path.GetRelativePath(folder, file)).GetString();
        if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))) != hash)
            throw new InvalidOperationException("The installer boundary did not observe the final durable writing bytes.");
    }
}
