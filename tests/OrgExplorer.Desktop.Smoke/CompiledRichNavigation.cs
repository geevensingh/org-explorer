using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using OrgExplorer.TestFixtures;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private static void DiagnoseCompiledRichNavigation(Type windowType, string root)
    {
        using var fixture = new ScaleFixture(Path.Combine(root, "rn"));
        fixture.Build();
        using var store = ApplicationWorkflow.CreateFileBacked();
        var window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [store, (Func<Window, string?>)(_ => fixture.Workspace), (Action<string>)(_ => { }), new SnapshotClock()], null)!;
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Invoke(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var model = (WorkspaceViewModel)Control<TreeView>("PeopleList").DataContext;
        Invoke("OpenSettingsButton");
        Invoke("OpenWorkspaceButton");
        PumpUntil(() => model.IsWorkspaceCurrent && Control<Button>("OpenWorkspaceButton").IsEnabled);
        var workload = ScaleFixture.RequireRichPopulation(model);
        var tree = Control<TreeView>("PeopleList");
        var pending = new Stack<ReportingNode>(model.ReportingRoots);
        var nodes = new Dictionary<string, ReportingNode>();
        while (pending.TryPop(out var node))
        {
            nodes.Add(node.Person.Email, node);
            foreach (var report in node.Reports) pending.Push(report);
        }
        var content = (FrameworkElement)window.Content;
        void Layout()
        {
            content.Measure(new Size(1100, 900));
            content.Arrange(new Rect(0, 0, 1100, 900));
            content.UpdateLayout();
        }
        void Select(string email) => tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(
            null!, nodes[email], TreeView.SelectedItemChangedEvent));
        var owner = ScaleFixture.Email(11);
        Select(owner);
        Layout();
        var samples = new List<object>();
        var choiceAssignments = 0;
        var choices = Control<ComboBox>("ManagerChoices");
        var choicesDescriptor = DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(ComboBox));
        EventHandler changedChoices = (_, _) => choiceAssignments++;
        choicesDescriptor.AddValueChanged(choices, changedChoices);
        var writing = new Dictionary<(NoteScope Scope, Guid Id), string>();
        foreach (var scope in new[] { NoteScope.Person, NoteScope.Team })
            for (var ordinal = 0; ordinal < (scope == NoteScope.Person ? 1 : 20); ordinal++)
            {
                var surface = Control<UserControl>(scope == NoteScope.Person ? "PersonNotes" : "TeamNotes");
                T Note<T>(string name) where T : FrameworkElement => (T)surface.FindName(name);
                Note<Button>("NewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var id = model.GetOwnedNotesView(scope).SelectedNote!.Id;
                Note<TextBox>("NoteTitle").Text = $"Compiled rich {scope} {ordinal:D2}";
                var prefix = $"Exact compiled rich {scope} {ordinal:D2}";
                Note<TextBox>("MarkdownEditor").Text = prefix;
                foreach (var destination in new[] { ScaleFixture.Target, owner })
                {
                    var allocated = GC.GetAllocatedBytesForCurrentThread();
                    var assignments = choiceAssignments;
                    var clock = Stopwatch.StartNew();
                    Select(destination);
                    var dispatchMs = clock.Elapsed.TotalMilliseconds;
                    Layout();
                    var throughLayoutMs = clock.Elapsed.TotalMilliseconds;
                    if (Control<TextBlock>("Email").Text != destination ||
                        model.PeopleNavigation.SelectedEmail != destination ||
                        Control<TextBlock>("ProfileHeading").Text != nodes[destination].Person.Heading ||
                        Control<Button>("EditProfileButton").IsEnabled == false)
                        throw new InvalidOperationException("The actual rich compiled navigation did not expose its exact usable owner/view.");
                    samples.Add(new { scope, ordinal, destination, dispatchMs, throughLayoutMs,
                        totalMs = clock.Elapsed.TotalMilliseconds,
                        managerChoiceAssignments = choiceAssignments - assignments,
                        allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated });
                }
                Note<TabControl>("Views").SelectedItem = Note<TabItem>("EditTab");
                for (var edit = 0; edit < 6; edit++)
                {
                    var spacing = Stopwatch.StartNew();
                    PumpUntil(() => spacing.ElapsedMilliseconds >= 250);
                    Note<TextBox>("MarkdownEditor").Text = prefix + $" continued input {edit}";
                }
                writing[(scope, id)] = prefix + " continued input 5";
            }
        PumpUntil(() => model.CanSafelyClose);
        choicesDescriptor.RemoveValueChanged(choices, changedChoices);
        Invoke("EditProfileButton");
        if (choices.Items.Count != model.People.Count || model.ProfileEdit?.Basis.Email != owner)
            throw new InvalidOperationException("Actual full-rich profile Edit omitted Manager candidates or changed its owner/base.");
        Invoke("CancelProfileButton");
        using (var independent = OpenSmokeWorkspace(fixture.Workspace))
        {
            ScaleFixture.RequireRichPopulation(independent, additionalPersonNotes: 1, additionalTeamNotes: 20);
            foreach (var ((scope, id), body) in writing)
                if (independent.BeginNoteDraft(scope, owner, id)?.Markdown != body ||
                    independent.BeginNoteDraft(scope, ScaleFixture.Target, id) is not null)
                    throw new InvalidOperationException("Rich compiled diagnosis lost exact independent owner/writing.");
        }
        if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
            throw new InvalidOperationException("Compiled rich diagnosis cannot create a native window.");
        Console.WriteLine("COMPILED-RICH-NAVIGATION " + JsonSerializer.Serialize(new
        {
            workload, sampleCount = samples.Count, samples,
            boundary = "Before actual compiled TreeView selection event through exact owner/header and complete1100x900 layout. Actual six250ms continued inputs per note; full rich population/resources/evidence independently reopened.",
            limit = "Exploratory actual BAML/public-real-store feedback only; not native UIA/provider/pixels, healthy WPF acceptance or a replacement for any42 native samples/100ms target."
        }));
        model.Dispose();
        window.Close();
    }
}
