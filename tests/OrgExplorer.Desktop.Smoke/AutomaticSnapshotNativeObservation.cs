using System.Diagnostics;
using System.Windows.Automation;

namespace OrgExplorer.Desktop.Smoke;

internal sealed class AutomaticSnapshotNativeObservation(AutomationElement window, Guid noteId,
    Func<SnapshotExportLifetime> lifetime)
{
    private enum NoteObservationPurpose { BeforeInput, AfterInput, ReturnedEditing }
    private const string Owner = "morgan@example.com", Heading = "Morgan Example", Title = "Automatic publication owner";
    private readonly CacheRequest textState = TextState();
    private readonly CacheRequest editorState = EditorState();
    private readonly AutomationElement identity = Program.Find(window, "Email");
    private readonly AutomationElement heading = Program.Find(window, "ProfileHeading");
    private readonly AutomationElement noteHeading = Program.Find(window, "TeamNoteHeading");
    private AutomationElement markdownEditor = Program.Find(window, "TeamNoteMarkdown");
    private readonly AutomationElement profileViewport = Program.Find(window, "ProfileScroll");
    private AutomationElement? synchronizationStatus, syncButton, settingsViewport;
    private readonly List<object> boundaries = [];
    internal SnapshotNativePublication? Publishing { get; private set; }
    internal SnapshotNativeNote? BeforeInput { get; private set; }
    internal SnapshotNativeNote? AfterInput { get; private set; }
    internal SnapshotNativeOwner? Avery { get; private set; }
    internal SnapshotNativeOwner? Morgan { get; private set; }
    internal SnapshotNativeNote? Returned { get; private set; }
    internal SnapshotNativePublication? LastPublication { get; private set; }
    internal SnapshotNativeNote? LastNote { get; private set; }
    internal SnapshotNativeOwner? LastOwner { get; private set; }
    internal SnapshotExportLifetime? LastLifetime { get; private set; }
    internal object? LastRejectedPublication { get; private set; }
    internal object? LastRejectedNote { get; private set; }
    internal object? LastRejectedOwner { get; private set; }
    internal object? LastReveal { get; private set; }
    internal int InputRequests { get; private set; }
    internal int SelectionRequests { get; private set; }
    internal int EditPreparationRequests { get; private set; }
    internal int EditorBindingGeneration { get; private set; } = 1;

    internal object Diagnostic => new
    {
        Publishing, BeforeInput, AfterInput, Avery, Morgan, Returned, LastPublication, LastNote, LastOwner, LastLifetime,
        LastRejectedPublication, LastRejectedNote, LastRejectedOwner, LastReveal, InputRequests, SelectionRequests,
        EditPreparationRequests, EditorBindingGeneration,
        timestampFrequency = Stopwatch.Frequency, boundaries,
        noteReadiness = "BeforeInput/ReturnedEditing require visible title and body. Clean committed owner return is Read; returned editing uses one explicit public Edit action and a fresh editor binding. AfterInput retains exact note metadata and fully visible writable body; ordinary title scrolling is not loss of editing usability.",
        boundary = "Driver observations of the first actual held publisher invocation. Entered is set inside its callback; Ended is set in that same callback's finally, including timeout/failure. Ended is never reset. Not product latency."
    };

    internal void Record(string stage)
    {
        if (boundaries.Count >= 48) throw new InvalidOperationException("Automatic native observation exceeded its bounded stage inventory.");
        boundaries.Add(new { stage, timestamp = Stopwatch.GetTimestamp(), lifetime = LastLifetime = lifetime() });
    }

    internal void RequireHeld(string stage)
    {
        LastLifetime = lifetime();
        if (!SnapshotNativeUsability.ExportHeld(LastLifetime))
            throw new InvalidOperationException("The same actual publisher invocation is no longer held: " + stage);
    }

    internal void RevealPublication()
    {
        synchronizationStatus ??= Program.Find(window, "SynchronizationStatus");
        syncButton ??= Program.Find(window, "SyncNow");
        settingsViewport ??= ScrollAncestor(synchronizationStatus);
        Reveal("publication", settingsViewport, synchronizationStatus, syncButton);
    }

    internal void RevealNote() => Reveal("owner-note", profileViewport, noteHeading, markdownEditor);

    internal void ObservePublishing()
    {
        Program.Await(() =>
        {
            RequireHeld("publishing-read");
            var status = synchronizationStatus!.GetUpdatedCache(textState);
            var button = syncButton!.GetUpdatedCache(textState);
            LastPublication = new(Text(status), Bounds(status), button.Cached.IsOffscreen, button.Cached.IsEnabled,
                Bounds(button), CurrentBounds(settingsViewport!), CurrentBounds(window));
            RequireHeld("publishing-read-completed");
            var ready = SnapshotNativeUsability.PublicationReady(LastPublication, LastLifetime!);
            if (!ready) LastRejectedPublication = new { frame = LastPublication, lifetime = LastLifetime, timestamp = Stopwatch.GetTimestamp() };
            return ready;
        });
        Publishing = LastPublication;
        Record("publishing-accepted");
    }

    internal void EnterWriting(string before, string value)
    {
        var binding = AwaitNote(before, NoteObservationPurpose.BeforeInput);
        BeforeInput = binding.Frame;
        Record("before-input-accepted");
        RequireHeld("input");
        InputRequests++;
        using (HarnessRuntime.Measure(HarnessRuntime.Work.NativeSetValue)) binding.Pattern.SetValue(value);
        AfterInput = AwaitNote(value, NoteObservationPurpose.AfterInput).Frame;
        Record("after-input-accepted");
    }

    internal void Select(string email, string heading, Action select)
    {
        RequireHeld("selection-started");
        SelectionRequests++;
        select();
        Program.Await(() =>
        {
            RequireHeld("destination-read");
            var owner = ReadOwner();
            RequireHeld("destination-read-completed");
            var ready = SnapshotNativeUsability.OwnerReady(owner, email, heading);
            if (!ready) LastRejectedOwner = new { frame = owner, lifetime = LastLifetime, timestamp = Stopwatch.GetTimestamp() };
            return ready;
        });
        if (email == "avery@example.com") Avery = LastOwner;
        else Morgan = LastOwner;
        Record("destination-accepted-" + email);
    }

    internal void ObserveReturned(string value)
    {
        Returned = AwaitNote(value, NoteObservationPurpose.ReturnedEditing).Frame;
        Record("returned-note-accepted");
    }

    internal void PrepareReturnedEditor()
    {
        RequireHeld("returned-edit-preparation");
        var currentOwner = ReadOwner();
        if (!SnapshotNativeUsability.OwnerReady(currentOwner, Owner, Heading) ||
            Program.Find(window, "TeamNoteHeading").Current.HelpText != noteId.ToString("D"))
            throw new InvalidOperationException("Returned Edit preparation must retain the exact current owner/note.");
        var tab = Program.Find(window, "TeamNoteEditTab");
        Reveal("returned-edit-tab", profileViewport, noteHeading, tab);
        var selection = ControlReadiness.AwaitPattern(() => Program.Find(window, "TeamNoteEditTab"),
            control => control.Current.IsEnabled && !control.Current.IsOffscreen &&
                SnapshotNativeUsability.BoundsValid(CurrentBounds(control)),
            control => (SelectionItemPattern)control.GetCurrentPattern(SelectionItemPattern.Pattern), Program.Await);
        RequireHeld("returned-edit-before-select");
        EditPreparationRequests++;
        using (HarnessRuntime.Measure(HarnessRuntime.Work.NativeInvoke)) selection.Select();
        Program.Await(() =>
        {
            RequireHeld("returned-edit-binding");
            var edit = Program.Find(window, "TeamNoteEditTab");
            if (!((SelectionItemPattern)edit.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected)
                return false;
            var current = Program.Find(window, "TeamNoteMarkdown");
            if (!current.Current.IsEnabled || !SnapshotNativeUsability.BoundsValid(CurrentBounds(current)))
                return false;
            markdownEditor = current;
            return true;
        });
        EditorBindingGeneration++;
        RequireHeld("returned-edit-bound");
        Record("returned-edit-prepared");
    }

    internal void Capture(string stage, Action capture, Action verify)
    {
        verify();
        RequireHeld(stage + "-before-capture");
        Record(stage + "-capture-started");
        capture();
        RequireHeld(stage + "-after-capture");
        verify();
        Record(stage + "-capture-completed");
    }

    private (SnapshotNativeNote Frame, ValuePattern Pattern) AwaitNote(string value, NoteObservationPurpose purpose)
    {
        ValuePattern? writing = null;
        Program.Await(() =>
        {
            RequireHeld("note-read");
            var owner = ReadOwner();
            var note = noteHeading.GetUpdatedCache(textState);
            var editor = markdownEditor.GetUpdatedCache(editorState);
            var bounds = editor.Cached.BoundingRectangle;
            var supports = editor.TryGetCachedPattern(ValuePattern.Pattern, out var pattern) && pattern is ValuePattern;
            LastNote = new(owner, Text(note), note.Cached.HelpText,
                new(editor.Cached.IsEnabled, editor.Cached.IsOffscreen, bounds.Width, bounds.Height, supports,
                    !Equals(editor.GetCachedPropertyValue(ValuePattern.IsReadOnlyProperty, true), false),
                    editor.GetCachedPropertyValue(ValuePattern.ValueProperty, true) as string),
                Bounds(note), Bounds(editor), CurrentBounds(profileViewport));
            RequireHeld("note-read-completed");
            var ready = purpose == NoteObservationPurpose.AfterInput
                ? SnapshotNativeUsability.NoteWritingObserved(LastNote, LastLifetime!, Owner, Heading, noteId.ToString("D"), Title, value)
                : SnapshotNativeUsability.NoteReady(LastNote, LastLifetime!, Owner, Heading, noteId.ToString("D"), Title, value);
            if (!ready)
            {
                LastRejectedNote = new { purpose = purpose.ToString(), frame = LastNote,
                    lifetime = LastLifetime, timestamp = Stopwatch.GetTimestamp() };
                return false;
            }
            writing = (ValuePattern)pattern!;
            return true;
        });
        return (LastNote!, writing!);
    }

    private SnapshotNativeOwner ReadOwner()
    {
        var currentIdentity = identity.GetUpdatedCache(textState);
        var currentHeading = heading.GetUpdatedCache(textState);
        return LastOwner = new(new(Text(currentIdentity), Text(currentHeading)),
            Bounds(currentIdentity), Bounds(currentHeading), CurrentBounds(window));
    }

    private static SnapshotVisibleText Text(AutomationElement observed)
    {
        var state = observed.Cached;
        return new(state.Name, state.IsOffscreen, state.BoundingRectangle.Width, state.BoundingRectangle.Height);
    }
    private static SnapshotNativeBounds Bounds(AutomationElement observed) => Rect(observed.Cached.BoundingRectangle);
    private static SnapshotNativeBounds CurrentBounds(AutomationElement element) => Rect(element.Current.BoundingRectangle);
    private static SnapshotNativeBounds Rect(System.Windows.Rect bounds) => new(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    private static CacheRequest TextState()
    {
        var state = new CacheRequest { TreeScope = TreeScope.Element };
        foreach (var property in new[] { AutomationElement.NameProperty, AutomationElement.HelpTextProperty,
            AutomationElement.IsOffscreenProperty, AutomationElement.IsEnabledProperty,
            AutomationElement.BoundingRectangleProperty }) state.Add(property);
        return state;
    }
    private static CacheRequest EditorState()
    {
        var state = TextState();
        state.Add(ValuePattern.Pattern);
        state.Add(ValuePattern.IsReadOnlyProperty);
        state.Add(ValuePattern.ValueProperty);
        return state;
    }
    private static AutomationElement ScrollAncestor(AutomationElement element)
    {
        for (var parent = TreeWalker.ControlViewWalker.GetParent(element); parent is not null;
            parent = TreeWalker.ControlViewWalker.GetParent(parent))
            if (parent.TryGetCurrentPattern(ScrollPattern.Pattern, out _)) return parent;
        throw new InvalidOperationException("The actual synchronization controls have no scrolling viewport.");
    }

    private void Reveal(string group, AutomationElement viewport, params AutomationElement[] targets)
    {
        var timer = Stopwatch.StartNew();
        void Budget()
        {
            if (timer.Elapsed >= TimeSpan.FromSeconds(20)) throw new TimeoutException("Intentional native reveal exceeded the existing 20-second observation budget.");
        }
        (SnapshotNativeBounds Viewport, (bool Offscreen, SnapshotNativeBounds Bounds)[] Targets) Geometry()
        {
            Budget();
            var visible = CurrentBounds(viewport);
            var observed = targets.Select(target => target.GetUpdatedCache(textState)).Select(target =>
                (Offscreen: target.Cached.IsOffscreen, Bounds: Bounds(target))).ToArray();
            LastReveal = new { group, timestamp = Stopwatch.GetTimestamp(), viewport = visible,
                targets = observed.Select(target => new { isOffscreen = target.Offscreen, bounds = target.Bounds }).ToArray() };
            return (visible, observed);
        }
        bool Visible()
        {
            var observed = Geometry();
            return observed.Targets.All(target => !target.Offscreen && SnapshotNativeUsability.Within(observed.Viewport, target.Bounds));
        }
        if (!Visible())
        {
            var observed = Geometry();
            var view = observed.Viewport;
            var bounds = observed.Targets.Select(target => target.Bounds).ToArray();
            if (!SnapshotNativeUsability.BoundsValid(view) || bounds.Any(value => !SnapshotNativeUsability.BoundsValid(value)))
                throw new InvalidOperationException("Intentional reveal requires finite positive actual control/viewport geometry.");
            var top = bounds.Min(value => value.Y);
            var bottom = bounds.Max(value => value.Y + value.Height);
            if (bottom - top > view.Height)
                throw new InvalidOperationException("The exact native control group does not fit its actual viewport.");
            var scrolling = (ScrollPattern)viewport.GetCurrentPattern(ScrollPattern.Pattern);
            var current = scrolling.Current;
            if (!current.VerticallyScrollable || !double.IsFinite(current.VerticalViewSize) ||
                current.VerticalViewSize is <= 0 or >= 100 || !double.IsFinite(current.VerticalScrollPercent))
                throw new InvalidOperationException("The actual viewport cannot reveal the requested native controls.");
            var extent = view.Height * (100 / current.VerticalViewSize - 1);
            var delta = (top + bottom - 2 * view.Y - view.Height) / 2;
            using (HarnessRuntime.Measure(HarnessRuntime.Work.NativeInvoke))
                scrolling.SetScrollPercent(ScrollPattern.NoScroll, Math.Clamp(current.VerticalScrollPercent + delta * 100 / extent, 0, 100));
        }
        Program.Await(Visible);
    }
}
