using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Reflection;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    // Test-harness routes only. Compiled replay stays invisible; no native UI or product behavior flag.
    private static int RunPassiveExitSupport(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("The no-wait exit support route takes no arguments.");
        var facts = PassiveLinkableFacts(out var note, out var digest, out _);
        var records = facts.Select((fact, index) => new PassiveSaveRecord(1, index + 1, Environment.ProcessId,
            1, Environment.CurrentManagedThreadId, Stopwatch.Frequency, index + 1, "support-test",
            "owned-lifecycle-support", true, fact)).ToArray();
        if (PassiveSaveCapture.LinkCommittedPrefix(records, "support-test", note, digest, true) is null ||
            PassiveSaveCapture.LinkCommittedPrefix(records, "support-test", note, digest, false) is not null)
            throw new InvalidOperationException("The lifecycle fixture does not exercise link completeness refusal.");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            diagnosticOnly = true, supportOnly = true, structurallyLinkable = true,
            note, requestDigest = digest, emittedRecords = facts.Length + PassiveSaveCapture.Capacity / 2
        }));
        foreach (var fact in facts) PassiveSaveCapture.Emit(fact);
        EmitUnclosedTail(note);
        return 0;
    }

    private static PassiveSaveFact[] PassiveLinkableFacts(out Guid note, out string digest, out string owner)
    {
        note = Guid.NewGuid();
        var incarnation = Guid.NewGuid();
        var call = Guid.NewGuid();
        var phase = Guid.NewGuid();
        digest = PassiveSaveCapture.Digest("Fictitious support request");
        owner = PassiveSaveCapture.OwnerDigest("support@example.com");
        var facts = new PassiveSaveFact[]
        {
            new(PassiveSaveStage.RequestBegin, NoteScope.Team, note, owner, Sample: 0, RequestDigest: digest),
            new(PassiveSaveStage.DraftUpdated, NoteScope.Team, note, owner, incarnation, Generation: 2, RequestDigest: digest),
            new(PassiveSaveStage.SaveSubmitted, NoteScope.Team, note, owner, incarnation,
                Generation: 2, SaveCall: call, SubmittedGeneration: 2, RequestDigest: digest),
            new(PassiveSaveStage.ActualCommit, SaveCall: call, Phase: phase, PhaseKind: WorkspaceStoragePhaseKind.Begin),
            new(PassiveSaveStage.ActualCommit, SaveCall: call, Phase: phase, PhaseKind: WorkspaceStoragePhaseKind.End),
            new(PassiveSaveStage.SaveAccepted, NoteScope.Team, note, owner, incarnation,
                SaveCall: call, SubmittedGeneration: 2, LatestGeneration: 2, Accepted: true,
                Retained: false, Removed: true, SuccessorBasis: false),
            new(PassiveSaveStage.FeedbackWrite, NoteScope.Team, note, owner,
                Feedback: PassiveSaveFeedback.ScopedLocalSave, SelectedNote: note, DisplayedNote: note,
                DisplayedOwnerDigest: owner)
        };
        return facts;
    }

    private static void EmitUnclosedTail(Guid note)
    {
        for (var index = 0; index < PassiveSaveCapture.Capacity / 2; index++)
            PassiveSaveCapture.Emit(new(PassiveSaveStage.NativePredicate, Note: note, Scope: NoteScope.Team,
                Sample: 0, Predicate: false));
    }

    private static int RunPassiveFiniteWorkSupport(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Finite work support takes no arguments.");
        var facts = PassiveLinkableFacts(out var note, out var digest, out var owner);
        foreach (var fact in facts) PassiveSaveCapture.Emit(fact);
        PassiveSaveFact Sample(PassiveSaveStage stage, int? index = null) => new(stage, NoteScope.Team, note,
            owner, Sample: 0, RequestDigest: digest, ContinuedInput: index, PrefixDigest: digest);
        PassiveSaveCapture.Emit(Sample(PassiveSaveStage.RequestEnd) with { InputReturned = true });
        for (var index = 0; index < 6; index++)
        {
            PassiveSaveCapture.Emit(Sample(PassiveSaveStage.ContinuedInputBegin, index));
            PassiveSaveCapture.Emit(Sample(PassiveSaveStage.ContinuedInputContent, index) with { Predicate = true });
            PassiveSaveCapture.Emit(Sample(PassiveSaveStage.ContinuedInputEnd, index));
        }
        PassiveSaveCapture.Emit(Sample(PassiveSaveStage.NativePredicate) with { Predicate = true });
        PassiveSaveCapture.Emit(facts[^1]); // Natural target presentation prefix through the ACK.
        PassiveSaveCapture.Emit(Sample(PassiveSaveStage.HarnessSampleClosed) with { Outcome = PassiveSaveOutcome.Completed });
        var directory = Environment.GetEnvironmentVariable("ORG_EXPLORER_DIAGNOSTIC_DIRECTORY")!;
        PassiveFiniteResult? proof = null;
        var work = Stopwatch.StartNew();
        // Controlled support work remains active; this is NOT an exit/Main drain wait.
        while (work.Elapsed < TimeSpan.FromSeconds(5))
        {
            var files = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.packet.jsonl", SearchOption.AllDirectories) : [];
            if (files.Length != 0)
            {
                var packets = files.Select(path => PassiveFiniteCapture.ReadClosed(path)).ToArray();
                var domain = packets.SelectMany(packet => packet.Records).Select(record => record.ClockDomain).FirstOrDefault();
                if (domain is not null)
                    proof = PassiveFiniteCapture.Evaluate(packets, new(note, NoteScope.Team, owner, digest,
                        Environment.GetEnvironmentVariable("GITHUB_SHA")!, Environment.GetEnvironmentVariable("GITHUB_RUN_ID")!,
                        Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT")!, domain, "support-test", "support-test", true));
                if (proof?.Outcome == PassiveFiniteOutcome.ObservationCovered) break;
            }
            Thread.Sleep(1);
        }
        if (proof?.Outcome != PassiveFiniteOutcome.ObservationCovered)
            throw new InvalidOperationException("Controlled work did not export a closed finite proof.");
        File.WriteAllText(Path.Combine(directory, "finite-work-proof.json"), JsonSerializer.Serialize(proof, PassiveFiniteCapture.Json));
        EmitUnclosedTail(note);
        // Both existing Main scopes still dispose only while returning, without WaitForDrain.
        return 0;
    }

    private sealed class PassiveProducerCounts : TraceListener
    {
        private readonly long[] counts = new long[Enum.GetValues<PassiveSaveStage>().Length];
        private long unscopedCommits;
        public override bool IsThreadSafe => true;
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { }
        public override void WriteLine(object? value, string? category)
        {
            if (category != PassiveSaveCapture.Category || value is not PassiveSaveFact fact) return;
            Interlocked.Increment(ref counts[(int)fact.Stage]);
            if (fact.Stage == PassiveSaveStage.ActualCommit && fact.SaveCall is null)
                Interlocked.Increment(ref unscopedCommits);
        }
        internal object Snapshot() => new
        {
            total = Enum.GetValues<PassiveSaveStage>().Sum(stage => Interlocked.Read(ref counts[(int)stage])),
            unscopedCommits = Interlocked.Read(ref unscopedCommits),
            stages = Enum.GetValues<PassiveSaveStage>().ToDictionary(stage => stage.ToString(),
                stage => Interlocked.Read(ref counts[(int)stage]))
        };
    }

    private static int RunPassiveRichCountSupport(string[] args)
    {
        if (args.Length != 2 || !Path.IsPathFullyQualified(args[1]))
            throw new ArgumentException("Rich producer support requires one owned absolute results directory.");
        var root = args[1];
        if (Directory.Exists(root)) throw new IOException("Do not reuse the rich producer support fixture.");
        Directory.CreateDirectory(root);
        using var counted = new PassiveProducerCounts();
        Trace.Listeners.Add(counted);
        try
        {
            using var fixture = new ScaleFixture(Path.Combine(root, "fixture")) { Log = _ => { } };
            fixture.Build();
            var setup = counted.Snapshot();
            using var model = new WorkspaceViewModel(ApplicationWorkflow.CreateFileBacked());
            if (!model.OpenWorkspace(fixture.Workspace)) throw new InvalidOperationException(model.Status);
            var owner = ScaleFixture.Email(11);
            var report = ScaleFixture.Target;
            if (!model.NavigatePerson(owner)) throw new InvalidOperationException(model.Status);
            var ids = new HashSet<Guid>();
            var samples = 0;
            foreach (var scope in new[] { NoteScope.Person, NoteScope.Team })
                for (var sampleIndex = 0; sampleIndex < (scope == NoteScope.Person ? 1 : 20); sampleIndex++)
                {
                    var note = model.CreateOwnedNoteDraft(scope, owner)
                        ?? throw new InvalidOperationException(model.Status);
                    if (!ids.Add(note.Id)) throw new InvalidOperationException("A support note reused an identity.");
                    var prefix = $"Support {scope} writing {sampleIndex}";
                    var writing = prefix;
                    using var sample = PassiveSaveCapture.BeginSample(scope, note.Id, owner,
                        scope == NoteScope.Person ? 0 : sampleIndex + 1, prefix);
                    NoteCommitObservation.Run(() => model.UpdateNoteDraft(scope, note.Id, "Support note", prefix),
                        () =>
                        {
                            using var read = ApplicationWorkflow.CreateFileBacked();
                            if (!read.OpenWorkspace(fixture.Workspace, WorkspaceOpenMode.ExistingOnly))
                                throw new InvalidOperationException(read.Status);
                            return read.BeginNoteDraft(scope, owner, note.Id)?.Markdown.StartsWith(prefix, StringComparison.Ordinal) == true;
                        }, observation =>
                        {
                            if (!model.NavigatePerson(report) || !model.NavigatePerson(owner))
                                throw new InvalidOperationException(model.Status);
                            observation.ContinueTyping(index =>
                            {
                                writing += $" {index}";
                                PassiveSaveCapture.EmitSample(PassiveSaveStage.ContinuedInputContent, index,
                                    requestDigest: PassiveSaveCapture.Digest(writing), predicate: true);
                                model.UpdateNoteDraft(scope, note.Id, "Support note", writing);
                            }, editCount: 6, spacingMs: 250);
                            observation.Complete();
                        });
                    if (!model.FlushNoteDraftsAsync().GetAwaiter().GetResult()) throw new InvalidOperationException(model.Status);
                    using var reopened = ApplicationWorkflow.CreateFileBacked();
                    if (!reopened.OpenWorkspace(fixture.Workspace, WorkspaceOpenMode.ExistingOnly) ||
                        reopened.BeginNoteDraft(scope, owner, note.Id)?.Markdown != writing ||
                        reopened.BeginNoteDraft(scope, report, note.Id) is not null)
                        throw new InvalidOperationException("The complete support writing did not reopen under its exact owner.");
                    samples++;
                }
            var population = ScaleFixture.RequireRichPopulation(model, additionalPersonNotes: 1, additionalTeamNotes: 20);
            File.WriteAllText(Path.Combine(root, "producer-counts.json"), JsonSerializer.Serialize(new
            {
                diagnosticOnly = true,
                scope = "Full rich actual filesystem/VM producers; NOT normal-EXE, compiled WPF, native UIA or required-role capacity proof",
                source = typeof(PassiveSaveCapture).Assembly.GetName().Version?.ToString(),
                samples, continuedInputs = 6, spacingMs = 250, population, setup,
                cumulative = counted.Snapshot(), dropped = PassiveSaveCapture.Current!.Dropped,
                nativeAndFeedbackCoverage = "Not exercised; must not infer hosted poll/write counts from this support replay."
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        finally { Trace.Listeners.Remove(counted); }
    }

    private static void VerifyPassiveRichCompiledCounts(Type type, string root, string results)
    {
        using var counted = new PassiveProducerCounts();
        Trace.Listeners.Add(counted);
        try
        {
            using var fixture = new ScaleFixture(Path.Combine(root, "rich")) { Log = _ => { } };
            fixture.Build();
            var setup = counted.Snapshot();
            var store = ApplicationWorkflow.CreateFileBacked();
            var window = (Window)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
                null, [store, (Func<Window, string?>)(_ => fixture.Workspace)], null)!;
            T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
            var model = (WorkspaceViewModel)Control<TreeView>("PeopleList").DataContext;
            Control<Button>("OpenSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Control<Button>("OpenWorkspaceButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => model.IsWorkspaceCurrent && Control<Button>("OpenWorkspaceButton").IsEnabled);
            IEnumerable<ReportingNode> Nodes(IEnumerable<ReportingNode> nodes) =>
                nodes.SelectMany(node => new[] { node }.Concat(Nodes(node.Reports)));
            void Select(string email) => Control<TreeView>("PeopleList").RaiseEvent(
                new RoutedPropertyChangedEventArgs<object>(null!, Nodes(model.ReportingRoots).Single(node => node.Person.Email == email),
                    TreeView.SelectedItemChangedEvent));
            var owner = ScaleFixture.Email(11);
            Select(owner);
            PumpUntil(() => Control<TextBlock>("Email").Text == owner);
            var samples = 0;
            foreach (var scope in new[] { NoteScope.Person, NoteScope.Team })
                for (var index = 0; index < (scope == NoteScope.Person ? 1 : 20); index++)
                {
                    var surface = Control<UserControl>(scope + "Notes");
                    T Note<T>(string name) where T : FrameworkElement => (T)surface.FindName(name);
                    Note<Button>("NewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var id = model.GetOwnedNotesView(scope).SelectedNote!.Id;
                    var prefix = $"Compiled support {scope} {index}";
                    var text = prefix;
                    Note<TextBox>("NoteTitle").Text = "Compiled support";
                    using var sample = PassiveSaveCapture.BeginSample(scope, id, owner, samples, prefix);
                    PassiveSaveCapture.EmitSample(PassiveSaveStage.RequestBegin);
                    Note<TextBox>("MarkdownEditor").Text = prefix;
                    PassiveSaveCapture.EmitSample(PassiveSaveStage.RequestEnd, inputReturned: true);
                    Select(ScaleFixture.Target); Select(owner);
                    for (var edit = 0; edit < 6; edit++)
                    {
                        if (edit != 0)
                        {
                            var gap = Stopwatch.StartNew();
                            PumpUntil(() => gap.Elapsed >= TimeSpan.FromMilliseconds(250));
                        }
                        PassiveSaveCapture.EmitSample(PassiveSaveStage.ContinuedInputBegin, edit);
                        text += $" {edit}";
                        PassiveSaveCapture.EmitSample(PassiveSaveStage.ContinuedInputContent, edit,
                            PassiveSaveCapture.Digest(text), predicate: true);
                        Note<TextBox>("MarkdownEditor").Text = text;
                        PassiveSaveCapture.EmitSample(PassiveSaveStage.ContinuedInputEnd, edit);
                    }
                    var flushing = model.FlushNoteDraftsAsync();
                    PumpUntil(() => flushing.IsCompleted);
                    if (!flushing.GetAwaiter().GetResult()) throw new InvalidOperationException(model.Status);
                    using var reopened = ApplicationWorkflow.CreateFileBacked();
                    if (!reopened.OpenWorkspace(fixture.Workspace, WorkspaceOpenMode.ExistingOnly) ||
                        reopened.BeginNoteDraft(scope, owner, id)?.Markdown != text ||
                        reopened.BeginNoteDraft(scope, ScaleFixture.Target, id) is not null)
                        throw new InvalidOperationException("Compiled writing did not reopen under its original owner.");
                    PassiveSaveCapture.EmitSample(PassiveSaveStage.HarnessSampleClosed, outcome: PassiveSaveOutcome.Completed);
                    samples++;
                }
            var population = ScaleFixture.RequireRichPopulation(model, additionalPersonNotes: 1, additionalTeamNotes: 20);
            if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("Compiled producer support created a native window.");
            File.WriteAllText(results, JsonSerializer.Serialize(new
            {
                diagnosticOnly = true, supportOnly = true, compiledProductionControls = true, nativeUIA = false,
                samples, continuedInputs = 6, spacingMs = 250, population, setup,
                cumulative = counted.Snapshot(), dropped = PassiveSaveCapture.Current!.Dropped,
                statement = "Actual compiled BAML/input handlers, VM, SQLite and feedback writers; not native EXE/UIA capacity or latency acceptance."
            }, new JsonSerializerOptions { WriteIndented = true }));
            window.Close();
        }
        finally { Trace.Listeners.Remove(counted); }
    }
}
