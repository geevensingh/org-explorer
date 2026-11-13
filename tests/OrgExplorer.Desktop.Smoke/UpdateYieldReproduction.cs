using System.ComponentModel;
using System.Diagnostics;
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
        VerifyUpdateYieldPublication(window, settings, publisher, preferredName: false);
        VerifyUpdateYieldPublication(window, settings, publisher, preferredName: true);
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
        Control<TextBox>("WorkspacePath").Text = folder;
        Invoke("OpenWorkspaceButton");
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

        var guidance = Control<TextBlock>("UpdateGuidance");
        var textProperty = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        var armed = false;
        var intercepted = false;
        var observed = false;
        var heldAtObservation = false;
        var bytesUnchanged = false;
        var updatePendingAtObservation = false;
        var noEarlyHandoff = false;
        Exception? experimentFailure = null;
        Task? saving = null;
        Task<bool>? noteSave = null;
        EventHandler arm = (_, _) =>
        {
            if (guidance.Text.Contains("Waiting for pending local saves", StringComparison.Ordinal)) armed = true;
        };
        DispatcherHookEventHandler intercept = (_, e) =>
        {
            if (!armed || intercepted || !window.Dispatcher.CheckAccess() || e.Operation.Priority != DispatcherPriority.Background)
                return;
            // Select the real post-drain yield, not another background operation or a timer.
            if (!new StackTrace().GetFrames().Any(frame =>
                    frame.GetMethod()?.DeclaringType?.Name.Contains("<WaitForLocalWork>", StringComparison.Ordinal) == true))
                return;
            intercepted = true;
            window.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    publisher.HoldNextNote = !preferredName;
                    publisher.HoldNextProfile = preferredName;
                    if (preferredName)
                    {
                        Invoke("SavePreferredNameButton");
                        saving = ((TaskCompletionSource)window.GetType()
                            .GetField("workCompleted", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                            .GetValue(window)!).Task;
                    }
                    else saving = noteSave = (Task<bool>)notes.GetType().GetMethod("FlushPendingDraftAsync")!.Invoke(notes, null)!;
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
                        heldAtObservation = publisher.Entered.IsSet && !publisher.Release.IsSet && saving is { IsCompleted: false };
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
        textProperty.AddValueChanged(guidance, arm);
        window.Dispatcher.Hooks.OperationPosted += intercept;
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
            PumpUntil(() => saving is { IsCompleted: true } && Control<Button>("CheckForUpdatesButton").IsEnabled);
            if (noteSave is not null && !noteSave.GetAwaiter().GetResult() || !intercepted || !heldAtObservation ||
                !bytesUnchanged || !updatePendingAtObservation || !noEarlyHandoff)
                throw new InvalidOperationException("The experiment did not preserve the intended yield/publication barriers.");
        }
        finally
        {
            feedRelease.TrySetResult();
            publisher.Release.Set();
            window.Dispatcher.Hooks.OperationPosted -= intercept;
            textProperty.RemoveValueChanged(guidance, arm);
            if (saving is not null) PumpUntil(() => saving.IsCompleted);
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
