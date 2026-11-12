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
        var fixtureDirectory = Environment.GetEnvironmentVariable("ORG_EXPLORER_UPDATE_FIXTURE")
            ?? throw new InvalidOperationException("The update-yield experiment requires the existing ephemeral trusted-update build.");
        var folder = Path.Combine(settings, "workspace");
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
        ((TabControl)notes.FindName("Views")).SelectedItem = notes.FindName("EditTab");
        var editor = (TextBox)notes.FindName("MarkdownEditor");
        var file = Path.Combine(folder, "notes", noteId + ".md");
        var original = File.ReadAllBytes(file);

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
        var guardFile = Path.Combine(Environment.GetEnvironmentVariable("DESKTOP_RESULTS_DIRECTORY") ?? fixtureDirectory,
            "safe-point-rejections.log");
        var handoff = Path.Combine(fixtureDirectory, "handoff.json");
        File.Delete(guardFile);
        File.Delete(handoff);

        var guidance = Control<TextBlock>("UpdateGuidance");
        var textProperty = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        var armed = false;
        var intercepted = false;
        var observed = false;
        var heldAtObservation = false;
        var bytesUnchanged = false;
        var observedGuard = "";
        Exception? experimentFailure = null;
        Task<bool>? saving = null;
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
                    publisher.HoldNextNote = true;
                    saving = (Task<bool>)notes.GetType().GetMethod("FlushPendingDraftAsync")!.Invoke(notes, null)!;
                    if (!publisher.Entered.Wait(TimeSpan.FromSeconds(5)))
                        throw new InvalidOperationException("The queued note save did not reach the held real-file publisher.");
                }
                catch (Exception error) { experimentFailure = error; }
                window.Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        observedGuard = File.Exists(guardFile) ? File.ReadAllText(guardFile) : "";
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
            editor.Text = "Final private writing queued in the update yield";
            Invoke("OpenSettingsButton");
            feedRelease.TrySetResult();
            PumpUntil(() => observed);
            if (experimentFailure is not null) throw experimentFailure;
            PumpUntil(() => saving is { IsCompleted: true } && Control<Button>("CheckForUpdatesButton").IsEnabled);
            if (!saving!.GetAwaiter().GetResult() || !intercepted || !heldAtObservation || !bytesUnchanged)
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
        var durable = File.ReadAllText(file).Contains("Final private writing queued in the update yield", StringComparison.Ordinal);
        var handedOff = File.Exists(handoff);
        if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
            throw new InvalidOperationException("The update-yield experiment must never show a window or create its native handle.");
        Console.WriteLine($"UPDATE-YIELD: intercepted={intercepted}; heldAtObservation={heldAtObservation}; " +
            $"bytesUnchangedBeforeRelease={bytesUnchanged}; finalWritingDurable={durable}; handoff={handedOff}; nativeHandle=0");
        Console.WriteLine(observedGuard.Length == 0 ? "UPDATE-YIELD: no rejecting guard before publication release." : observedGuard);
        if (!durable || !handedOff)
            throw new InvalidOperationException("The single update action must wait for the note save admitted during its yield, then hand off the durable writing. " +
                $"Observed guard: {observedGuard}");
        using var request = JsonDocument.Parse(File.ReadAllText(handoff));
        var hash = request.RootElement.GetProperty("workspaceFiles").GetProperty(Path.GetRelativePath(folder, file)).GetString();
        if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))) != hash)
            throw new InvalidOperationException("The installer boundary did not observe the final durable note bytes.");
    }
}
