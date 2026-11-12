using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);

    private static double DpiScale(AutomationElement window) =>
        GetDpiForWindow((IntPtr)window.Current.NativeWindowHandle) / 96d;

    private static void ResizeWindow(AutomationElement window, double width, double height)
    {
        var scale = DpiScale(window);
        ((WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern)).SetWindowVisualState(WindowVisualState.Normal);
        ((TransformPattern)window.GetCurrentPattern(TransformPattern.Pattern)).Resize(width * scale, height * scale);
        Await(() => Math.Abs(window.Current.BoundingRectangle.Width / scale - width) < 2);
    }

    private static PaneProportions RenderedPaneProportions(AutomationElement window)
    {
        var scale = DpiScale(window);
        var people = Find(window, "PeopleNavigation").Current.BoundingRectangle;
        var host = Find(window, "AdaptiveWorkspace").Current.BoundingRectangle;
        var profile = Find(window, "ProfileSummary").Current.BoundingRectangle;
        var notes = Find(window, "NotesPane").Current.BoundingRectangle;
        return new(100 * people.Width / (host.Width - 8 * scale),
            100 * profile.Width / (profile.Width + notes.Width));
    }

    private static void DragDivider(AutomationElement window, string id, double targetX)
    {
        var bounds = Find(window, id).Current.BoundingRectangle;
        var viewport = Find(window, "ProfileScroll").Current.BoundingRectangle;
        var y = (int)Math.Max(bounds.Top + 20, viewport.Top + 20);
        if (!SetCursorPos((int)(bounds.Left + bounds.Width / 2), y))
            throw new InvalidOperationException("The hosted interactive desktop could not position the divider pointer.");
        mouse_event(2, 0, 0, 0, 0);
        try
        {
            Thread.Sleep(100);
            if (!SetCursorPos((int)targetX, y)) throw new InvalidOperationException("Hosted divider drag could not move.");
            Thread.Sleep(100);
        }
        finally { mouse_event(4, 0, 0, 0, 0); }
    }

    private static PaneProportions AssertAdaptiveWorkspace(AutomationElement window)
    {
        Set(window, "SearchQuery", "");
        var scale = DpiScale(window);
        var state = (WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern);
        void Resize(double width, double height) => ResizeWindow(window, width, height);
        bool Visible(string id) => window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, id)) is { } element && !element.Current.IsOffscreen;
        Rect Bounds(string id) => Find(window, id).Current.BoundingRectangle;
        void AtSize(bool wide)
        {
            Await(() => Visible("OpenPeople") != wide);
            var scroll = (ScrollPattern)Find(window, "ProfileScroll").GetCurrentPattern(ScrollPattern.Pattern);
            if (scroll.Current.HorizontallyScrollable)
                throw new InvalidOperationException("Ordinary adaptive profile reading requires horizontal scrolling.");
            if (scroll.Current.VerticallyScrollable) scroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
            var identity = Bounds("Email");
            var profile = Bounds("ProfileSummary");
            var notes = Bounds("NotesPane");
            if (wide)
            {
                if (!Visible("People") || profile.Width / scale < 239 || notes.Width / scale < 359 ||
                    profile.Right > notes.Left + 1 || Math.Abs(profile.Top - notes.Top) > 2 ||
                    identity.Top >= profile.Top ||
                    Math.Abs((notes.Left - profile.Right) / scale - 8) > 1 ||
                    Math.Abs((Bounds("ProfileScroll").Left - Bounds("PeopleNavigation").Right) / scale - 8) > 1)
                    throw new InvalidOperationException($"Wide adaptive bounds are unusable: identity={identity}, profile={profile}, notes={notes}.");
            }
            else
            {
                if (Visible("People")) throw new InvalidOperationException("Compact reading retains persistent People columns.");
                var before = Bounds("ProfileScroll");
                Click(window, "OpenPeople");
                Await(() => Visible("People"));
                var after = Bounds("ProfileScroll");
                if (before != after || Bounds("PeopleNavigation").Width / scale > 361)
                    throw new InvalidOperationException($"People overlay moved or narrowed the Person workspace: {before} -> {after}.");
                Find(window, "ClosePeople").SetFocus();
                PressCopyKey(0x1B);
                Await(() => !Visible("People") && Find(window, "OpenPeople").Current.HasKeyboardFocus);
                if (profile.Left > notes.Left + 2 || profile.Top >= notes.Top)
                    throw new InvalidOperationException("Compact summary is not stacked above full-width notes.");
            }
            if (!Visible("SearchQuery") || !Visible("OpenSettings"))
                throw new InvalidOperationException("Adaptive navigation concealed search or Settings.");
            Console.WriteLine($"Adaptive measured: window={window.Current.BoundingRectangle}, profile={profile}, notes={notes}, wide={wide}, dpi={scale * 96}.");
        }
        Resize(700, 500);
        AtSize(false);
        Resize(1100, 900);
        AtSize(true);
        foreach (var hostWidth in new[] { 959d, 960d, 961d })
        {
            var chrome = window.Current.BoundingRectangle.Width / scale - Bounds("AdaptiveWorkspace").Width / scale;
            Resize(hostWidth + chrome, 900);
            AtSize(hostWidth >= 960);
        }
        state.SetWindowVisualState(WindowVisualState.Maximized);
        Await(() => state.Current.WindowVisualState == WindowVisualState.Maximized);
        AtSize(true);
        var host = Bounds("AdaptiveWorkspace");
        DragDivider(window, "PeopleDivider", host.Left + 20);
        Await(() => Math.Abs(Bounds("PeopleNavigation").Width / scale - 240) < 2);
        DragDivider(window, "ProfileDivider", Bounds("ProfileSummary").Left + 20);
        Await(() => Math.Abs(Bounds("ProfileSummary").Width / scale - 240) < 2);
        var reading = Find(window, "PersonNoteReading");
        reading.SetFocus();
        Await(() => !reading.Current.IsOffscreen);
        var text = (TextPattern)reading.GetCurrentPattern(TextPattern.Pattern);
        var prose = text.DocumentRange.FindText("Private Person writing", false, false)
            ?? throw new InvalidOperationException("Actual note prose is missing.");
        prose.ScrollIntoView(true);
        if (!text.DocumentRange.GetText(-1).Contains("Private Person writing") ||
            reading.Current.BoundingRectangle.Width / scale > 601 || prose.GetBoundingRectangles().Length == 0)
            throw new InvalidOperationException("Wide note reading lost visible prose or exceeded the 600 DIP reading surface.");
        if (Bounds("NotesPane").Width / scale <= 650)
            throw new InvalidOperationException("The hosted maximized viewport is too narrow to prove the 600 DIP prose limit after both pane minimums; a wider isolated desktop is required.");
        if (reading.Current.BoundingRectangle.Width / scale < 599)
            throw new InvalidOperationException("Wide note reading did not use its bounded 600 DIP surface.");
        var font = prose.GetAttributeValue(TextPattern.FontSizeAttribute);
        if (font is not double size || size < 10.5 ||
            !Equals(prose.GetAttributeValue(TextPattern.FontNameAttribute), "Segoe UI"))
            throw new InvalidOperationException("Actual rendered note prose lost readable Segoe UI typography.");
        var personScroll = (ScrollPattern)Find(window, "ProfileScroll").GetCurrentPattern(ScrollPattern.Pattern);
        if (personScroll.Current.VerticallyScrollable) personScroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
        DragDivider(window, "ProfileDivider", Bounds("ProfileScroll").Right - 20);
        Await(() => Math.Abs(Bounds("NotesPane").Width / scale - 360) < 2);
        var profileDivider = Find(window, "ProfileDivider");
        profileDivider.SetFocus();
        Await(() => profileDivider.Current.HasKeyboardFocus);
        var previousProfile = Bounds("ProfileSummary").Width;
        PressCopyKey(0x25);
        Await(() => Bounds("ProfileSummary").Width < previousProfile - 5 * scale);
        var divider = Find(window, "PeopleDivider");
        divider.SetFocus();
        Await(() => divider.Current.HasKeyboardFocus);
        var previousPeople = Bounds("PeopleNavigation").Width;
        PressCopyKey(0x27);
        Await(() => Bounds("PeopleNavigation").Width > previousPeople + 5 * scale);
        var preferred = RenderedPaneProportions(window);
        Resize(700, 500);
        AtSize(false);
        reading = Find(window, "PersonNoteReading");
        reading.SetFocus();
        prose = ((TextPattern)reading.GetCurrentPattern(TextPattern.Pattern)).DocumentRange
            .FindText("Private Person writing", false, false)!;
        prose.ScrollIntoView(true);
        if (!Equals(font, prose.GetAttributeValue(TextPattern.FontSizeAttribute)) || prose.GetBoundingRectangles().Length == 0)
            throw new InvalidOperationException("Compact note reading shrank typography or failed to expose actual prose.");
        state.SetWindowVisualState(WindowVisualState.Maximized);
        Await(() => state.Current.WindowVisualState == WindowVisualState.Maximized);
        AtSize(true);
        var restored = RenderedPaneProportions(window);
        if (Math.Abs(preferred.PeoplePercent - restored.PeoplePercent) > .5 ||
            Math.Abs(preferred.ProfilePercent - restored.ProfilePercent) > .5)
            throw new InvalidOperationException("Compact transition overwrote the user's wide percentage balance.");
        return preferred;
    }

    private static void AssertSearchBrowsingRestoration(AutomationElement window)
    {
        foreach (var compact in new[] { false, true })
        {
            if (compact) ResizeWindow(window, 700, 900);
            EnsurePeopleBrowsing(window);
            var manager = Find(Find(window, "People"), "morgan@example.com");
            var branch = (ExpandCollapsePattern)manager.GetCurrentPattern(ExpandCollapsePattern.Pattern);
            branch.Expand();
            var scroll = (ScrollPattern)Find(window, "People").GetCurrentPattern(ScrollPattern.Pattern);
            if (!scroll.Current.VerticallyScrollable)
                throw new InvalidOperationException("The representative browse fixture did not exercise a real reporting-tree viewport.");
            scroll.SetScrollPercent(ScrollPattern.NoScroll, 70);
            Await(() => Math.Abs(scroll.Current.VerticalScrollPercent - 70) < 1);
            var offset = scroll.Current.VerticalScrollPercent;
            Set(window, "SearchQuery", "orchid");
            Await(() => !IsVisible(window, "People") && !IsVisible(window, "Favorites"));
            if (Find(window, "SearchResults").Current.BoundingRectangle.Height <
                Find(window, "PeopleNavigation").Current.BoundingRectangle.Height - 150 * DpiScale(window))
                throw new InvalidOperationException("Search did not replace browsing with a full-height result list.");
            QueryAndSelect(window, "orchid", "avery@example.com", "Current", "Person note");
            if (compact) Await(() => !IsVisible(window, "PeopleNavigation"));
            EnsurePeopleBrowsing(window);
            Await(() => Math.Abs(scroll.Current.VerticalScrollPercent - offset) < 1 && IsVisible(window, "Favorites"));
            scroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
            Await(() => ((ExpandCollapsePattern)Find(Find(window, "People"), "morgan@example.com")
                .GetCurrentPattern(ExpandCollapsePattern.Pattern)).Current.ExpandCollapseState == ExpandCollapseState.Expanded);
        }
        ((WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern)).SetWindowVisualState(WindowVisualState.Maximized);
        Await(() => !IsVisible(window, "OpenPeople"));
    }

    private static bool IsVisible(AutomationElement window, string id) =>
        window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id)) is { } element &&
        !element.Current.IsOffscreen;

    private static void EnsurePeopleBrowsing(AutomationElement window)
    {
        if (Value(window, "SearchQuery").Length > 0) Set(window, "SearchQuery", "");
        if (IsVisible(window, "OpenPeople") && !IsVisible(window, "PeopleNavigation")) Click(window, "OpenPeople");
        Await(() => IsVisible(window, "People"));
    }

    private static void AssertProtectedPeopleOverlay(AutomationElement window)
    {
        ResizeWindow(window, 700, 900);
        var before = Find(window, "ProfileScroll").Current.BoundingRectangle;
        EnsurePeopleBrowsing(window);
        SelectTreePerson(window, "casey@example.com", expectNavigation: false);
        Await(() => Find(window, "Status").Current.Name.Contains("Person note") &&
            Find(window, "Status").Current.Name.Contains("Retry or discard"));
        if (!IsVisible(window, "PeopleNavigation") ||
            before != Find(window, "ProfileScroll").Current.BoundingRectangle ||
            Find(window, "Email").Current.Name != "avery@example.com" || Value(window, "PersonNoteTitle") != "")
            throw new InvalidOperationException("Rejected overlay navigation lost the original owner, exact failed editor, geometry or navigation panel.");
        AssertSelectionMatchesProfile(window);
        Click(window, "ClosePeople");
        ((WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern)).SetWindowVisualState(WindowVisualState.Maximized);
        Await(() => !IsVisible(window, "OpenPeople"));
    }
}
