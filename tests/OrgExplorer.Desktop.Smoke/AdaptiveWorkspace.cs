using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using OrgExplorer.Workflow;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [StructLayout(LayoutKind.Sequential)]
    private struct ResizeRect
    {
        public int Left, Top, Right, Bottom;
        public override readonly string ToString() =>
            $"[{Left},{Top}..{Right},{Bottom}; {Right - Left}x{Bottom - Top} px]";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ResizeMonitorInfo
    {
        public int Size;
        public ResizeRect Monitor, WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr window, out ResizeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(IntPtr window, out ResizeRect rect);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref ResizeMonitorInfo info);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);

    private static double DpiScale(AutomationElement window) =>
        GetDpiForWindow((IntPtr)window.Current.NativeWindowHandle) / 96d;

    private static void ResizeWindow(AutomationElement window, double width, double height)
    {
        var scale = DpiScale(window);
        var handle = (IntPtr)window.Current.NativeWindowHandle;
        var state = (WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern);
        var transform = (TransformPattern)window.GetCurrentPattern(TransformPattern.Pattern);
        var observed = window.Current.BoundingRectangle;
        var before = observed;
        var observedState = state.Current.WindowVisualState;
        var canResize = transform.Current.CanResize;
        var request = $"requestedDip={width}x{height}; requestedPx={width * scale}x{height * scale}; initialDpi={scale * 96}";
        state.SetWindowVisualState(WindowVisualState.Normal);
        transform.Resize(width * scale, height * scale);
        try
        {
            Await(() =>
            {
                observed = window.Current.BoundingRectangle;
                observedState = state.Current.WindowVisualState;
                canResize = transform.Current.CanResize;
                return Math.Abs(observed.Width / scale - width) < 2 && Math.Abs(observed.Height / scale - height) < 2;
            });
        }
        catch (TimeoutException error)
        {
            throw new TimeoutException(
                $"Responsive resize did not reach its requested size within 20 seconds. " +
                $"{request}; beforeUiaPx={before}; lastUiaPx={observed}; lastUiaDipAtInitialDpi={observed.Width / scale}x{observed.Height / scale}; " +
                $"lastState={observedState}; canResize={canResize}; {NativeResizeBounds(handle)}", error);
        }
    }

    private static string NativeResizeBounds(IntPtr handle)
    {
        var window = GetWindowRect(handle, out var bounds)
            ? bounds.ToString() : $"unavailable (GetWindowRect Win32={Marshal.GetLastWin32Error()})";
        var client = GetClientRect(handle, out var clientBounds)
            ? clientBounds.ToString() : $"unavailable (GetClientRect Win32={Marshal.GetLastWin32Error()})";
        var monitor = MonitorFromWindow(handle, 2);
        var info = new ResizeMonitorInfo { Size = Marshal.SizeOf<ResizeMonitorInfo>() };
        var screen = monitor == IntPtr.Zero ? "monitor unavailable (MonitorFromWindow returned NULL)" :
            GetMonitorInfo(monitor, ref info)
                ? $"monitorPx={info.Monitor}; workAreaPx={info.WorkArea}; monitorFlags={info.Flags}"
                : $"monitor unavailable (GetMonitorInfo Win32={Marshal.GetLastWin32Error()})";
        return $"nativeWindowPx={window}; nativeMaximized={IsZoomed(handle)}; clientLocalPx={client}; currentDpi={GetDpiForWindow(handle)}; {screen}; " +
            $"primaryDesktopPx={GetSystemMetrics(0)}x{GetSystemMetrics(1)}; " +
            $"virtualDesktopPx=[{GetSystemMetrics(76)},{GetSystemMetrics(77)}; {GetSystemMetrics(78)}x{GetSystemMetrics(79)}]; " +
            $"minimumTrackPx={GetSystemMetrics(34)}x{GetSystemMetrics(35)}; maximumTrackPx={GetSystemMetrics(59)}x{GetSystemMetrics(60)}";
    }

    private static double RenderedPeoplePercent(AutomationElement window)
    {
        var scale = DpiScale(window);
        var people = Find(window, "PeopleNavigation").Current.BoundingRectangle;
        var host = Find(window, "AdaptiveWorkspace").Current.BoundingRectangle;
        return 100 * people.Width / (host.Width - 8 * scale);
    }

    private static void DragDivider(AutomationElement window, string id, double targetX)
    {
        var bounds = Find(window, id).Current.BoundingRectangle;
        var viewport = Find(window, "ProfileScroll").Current.BoundingRectangle;
        var x = (int)(bounds.Left + bounds.Width / 2);
        var y = (int)Math.Max(bounds.Top + 20, viewport.Top + 20);
        var host = Find(window, "AdaptiveWorkspace").Current.BoundingRectangle;
        if (bounds.Left < host.Left - 1 || bounds.Right > host.Right + 1 ||
            y < bounds.Top || y >= bounds.Bottom || targetX < host.Left || targetX >= host.Right)
            throw new InvalidOperationException($"Rendered People divider is not reachable inside its host: divider={bounds}, host={host}, start={x},{y}, targetX={targetX}.");
        if (!SetCursorPos(x, y))
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

    private static double AssertAdaptiveWorkspace(AutomationElement window)
    {
        Set(window, "SearchQuery", "");
        var scale = DpiScale(window);
        var state = (WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern);
        void Resize(double width, double height) => ResizeWindow(window, width, height);
        bool Visible(string id) => window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, id)) is { } element && !element.Current.IsOffscreen;
        Rect Bounds(string id) => Find(window, id).Current.BoundingRectangle;
        void AtSize(bool wide, string stateName)
        {
            Await(() => Visible("OpenPeople") != wide);
            var scroll = (ScrollPattern)Find(window, "ProfileScroll").GetCurrentPattern(ScrollPattern.Pattern);
            if (scroll.Current.HorizontallyScrollable)
                throw new InvalidOperationException("Ordinary adaptive profile reading requires horizontal scrolling.");
            if (scroll.Current.VerticallyScrollable) scroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
            var identity = Bounds("Email");
            var profile = Bounds("ProfileSummary");
            var notes = Bounds("NotesPane");
            if (profile.Left > notes.Left + 2 || profile.Bottom > notes.Top + 2 ||
                Math.Abs(profile.Width - notes.Width) > 2 || notes.Width / scale < 359 ||
                notes.Right > Bounds("ProfileScroll").Right + 1 || identity.Top >= profile.Top)
                throw new InvalidOperationException($"Facts must precede full-width notes below anchored identity: identity={identity}, facts={profile}, notes={notes}.");
            foreach (var id in new[] { "OfficeBuildingValue", "OfficeNumberValue", "OfficeStateValue",
                "OfficeCountryValue", "DirectoryName", "AliasValue", "LinkedInProfileUrlValue" })
                if (string.IsNullOrEmpty(Find(window, id).Current.Name))
                    throw new InvalidOperationException($"Ordinary fact lost its populated or neutral-dash slot: {id}.");
            if (wide)
            {
                if (!Visible("People") || Bounds("PeopleNavigation").Width / scale < 239 ||
                    Math.Abs((Bounds("ProfileScroll").Left - Bounds("PeopleNavigation").Right) / scale - 20) > 1)
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
                if (Bounds("PeopleNavigation").Left > Bounds("OpenPeople").Left + 2 * scale)
                    throw new InvalidOperationException("The compact People trigger and overlay do not share the left edge.");
                CaptureVisualEvidence(window, stateName + "-left-overlay");
                Find(window, "ClosePeople").SetFocus();
                Await(() => Find(window, "ClosePeople").Current.HasKeyboardFocus);
                PressCopyKey(0x1B);
                Await(() => !Visible("People") && Find(window, "OpenPeople").Current.HasKeyboardFocus);
            }
            if (!Visible("SearchQuery") || !Visible("OpenSettings"))
                throw new InvalidOperationException("Adaptive navigation concealed search or Settings.");
            CaptureVisualEvidence(window, stateName);
            Console.WriteLine($"Adaptive measured: window={window.Current.BoundingRectangle}, profile={profile}, notes={notes}, wide={wide}, dpi={scale * 96}.");
        }
        Resize(700, 500);
        AtSize(false, "reading-minimum-700-500");
        Resize(1100, 900);
        AtSize(true, "reading-ordinary-1100-900");
        foreach (var hostWidth in new[] { 959d, 960d, 961d })
        {
            var chrome = window.Current.BoundingRectangle.Width / scale - Bounds("AdaptiveWorkspace").Width / scale;
            Resize(hostWidth + chrome, 900);
            AtSize(hostWidth >= 960, $"reading-boundary-{hostWidth:0}");
        }
        state.SetWindowVisualState(WindowVisualState.Maximized);
        Await(() => state.Current.WindowVisualState == WindowVisualState.Maximized);
        AtSize(true, "reading-maximized");
        var host = Bounds("AdaptiveWorkspace");
        DragDivider(window, "PeopleDivider", host.Left + 20);
        Await(() => Math.Abs(Bounds("PeopleNavigation").Width / scale - 240) < 2);
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
            throw new InvalidOperationException("The hosted maximized viewport is too narrow to prove the 600 DIP prose limit; a wider isolated desktop is required.");
        if (reading.Current.BoundingRectangle.Width / scale < 599)
            throw new InvalidOperationException("Wide note reading did not use its bounded 600 DIP surface.");
        var font = prose.GetAttributeValue(TextPattern.FontSizeAttribute);
        if (font is not double size || size < 10.5 ||
            !Equals(prose.GetAttributeValue(TextPattern.FontNameAttribute), "Segoe UI"))
            throw new InvalidOperationException("Actual rendered note prose lost readable Segoe UI typography.");
        var personScroll = (ScrollPattern)Find(window, "ProfileScroll").GetCurrentPattern(ScrollPattern.Pattern);
        if (!personScroll.Current.VerticallyScrollable)
            throw new InvalidOperationException("Long reading did not exercise the main Person content scroll.");
        personScroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
        var anchoredHeading = Bounds("ProfileHeading");
        var anchoredEmail = Bounds("Email");
        var factsTop = Bounds("ProfileSummary").Top;
        personScroll.SetScrollPercent(ScrollPattern.NoScroll, 40);
        Await(() => personScroll.Current.VerticalScrollPercent > 39);
        if (Bounds("ProfileHeading") != anchoredHeading || Bounds("Email") != anchoredEmail ||
            Bounds("ProfileSummary").Top >= factsTop - 100 * scale || reading.Current.IsOffscreen)
            throw new InvalidOperationException("Identity moved or long note reading failed to share the facts scroll.");
        if (reading.TryGetCurrentPattern(ScrollPattern.Pattern, out var inner) &&
            ((ScrollPattern)inner).Current.VerticallyScrollable)
            throw new InvalidOperationException("Read-mode notes still have an independent scrolling box.");
        CaptureVisualEvidence(window, "reading-anchored-identity-long-scroll");
        personScroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
        var divider = Find(window, "PeopleDivider");
        divider.SetFocus();
        Await(() => divider.Current.HasKeyboardFocus);
        var previousPeople = Bounds("PeopleNavigation").Width;
        PressCopyKey(0x27);
        Await(() => Math.Abs(Bounds("PeopleNavigation").Width - previousPeople - 10 * scale) < scale);
        DragDivider(window, "PeopleDivider", Bounds("AdaptiveWorkspace").Right - 20);
        Await(() => Math.Abs(Bounds("ProfileScroll").Width / scale - 408) < 2);
        if (Bounds("NotesPane").Width / scale < 359 ||
            Bounds("NotesPane").Right > Bounds("ProfileScroll").Right + 1)
            throw new InvalidOperationException("Maximum People drag pushed full-width reading outside its viewport.");
        divider.SetFocus();
        Await(() => divider.Current.HasKeyboardFocus);
        previousPeople = Bounds("PeopleNavigation").Width;
        PressCopyKey(0x25);
        Await(() => Math.Abs(Bounds("PeopleNavigation").Width - previousPeople + 10 * scale) < scale);
        host = Bounds("AdaptiveWorkspace");
        DragDivider(window, "PeopleDivider", host.Left + (host.Width - 8 * scale) * .27);
        Await(() => Math.Abs(RenderedPeoplePercent(window) - 27) < 1);
        var preferred = RenderedPeoplePercent(window);
        var widePeopleWidth = Bounds("PeopleNavigation").Width;
        Resize(1100, 900);
        AtSize(true, "reading-wide-balance-resized");
        var resized = RenderedPeoplePercent(window);
        if (Math.Abs(preferred - resized) > .5 ||
            Math.Abs(Bounds("PeopleNavigation").Width - widePeopleWidth) < 10 * scale)
            throw new InvalidOperationException("Wide resizing retained pixels instead of the user's percentage proportions.");
        Resize(700, 500);
        AtSize(false, "reading-compact-balance-retained");
        reading = Find(window, "PersonNoteReading");
        reading.SetFocus();
        prose = ((TextPattern)reading.GetCurrentPattern(TextPattern.Pattern)).DocumentRange
            .FindText("Private Person writing", false, false)!;
        prose.ScrollIntoView(true);
        if (!Equals(font, prose.GetAttributeValue(TextPattern.FontSizeAttribute)) || prose.GetBoundingRectangles().Length == 0)
            throw new InvalidOperationException("Compact note reading shrank typography or failed to expose actual prose.");
        state.SetWindowVisualState(WindowVisualState.Maximized);
        Await(() => state.Current.WindowVisualState == WindowVisualState.Maximized);
        AtSize(true, "reading-wide-balance-restored");
        var restored = RenderedPeoplePercent(window);
        if (Math.Abs(preferred - restored) > .5)
            throw new InvalidOperationException("Compact transition overwrote the user's wide percentage balance.");
        return preferred;
    }

    private static void AssertLongAndMissingFacts(AutomationElement window)
    {
        var ids = new[] { "NavigateManager", "OfficeBuildingValue", "OfficeNumberValue", "OfficeStateValue",
            "OfficeCountryValue", "DirectoryName", "AliasValue", "LinkedInProfileUrlValue" };
        var scroll = (ScrollPattern)Find(window, "ProfileScroll").GetCurrentPattern(ScrollPattern.Pattern);
        scroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
        var lefts = ids.Select(id => Find(window, id).Current.BoundingRectangle.Left).ToArray();
        QueryAndSelect(window, "longreading", "longread@example.com", "Current", "Profile:");
        Click(window, "ClearSearch");
        Await(() => Value(window, "SearchQuery") == "" && IsVisible(window, "People") &&
            Find(window, "Email").Current.Name == "longread@example.com");
        scroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
        for (var index = 0; index < ids.Length; index++)
            if (Math.Abs(Find(window, ids[index]).Current.BoundingRectangle.Left - lefts[index]) > 2)
                throw new InvalidOperationException("Populated and missing facts repacked a width-stable labelled slot: " + ids[index]);
        foreach (var id in new[] { "OfficeNumberValue", "OfficeCountryValue", "LinkedInProfileUrlValue" })
            if (Find(window, id).Current.Name != "—")
                throw new InvalidOperationException("A missing ordinary fact lost its neutral-dash slot: " + id);
        const string building = "South campus research and collaboration building with a deliberately long descriptive location";
        AssertKeyboardCopy(window, "OfficeBuildingValue", building);
        if (Find(window, "ProfileHeading").Current.Name !=
            "Alexandra Reference with a deliberately long Directory name for accessible Person reading" ||
            Find(window, "PhotoPlaceholder").Current.IsOffscreen || scroll.Current.HorizontallyScrollable)
            throw new InvalidOperationException("Long identity/facts lost full accessible text or compact initials, or require horizontal scrolling.");
        CaptureVisualEvidence(window, "reading-long-and-missing-facts-initials");
        QueryAndSelect(window, "007b", "avery@example.com", "Current", "Profile:");
    }

    private static void AssertSearchBrowsingRestoration(AutomationElement window)
    {
        foreach (var compact in new[] { false, true })
        {
            if (compact) ResizeWindow(window, 700, 900);
            EnsurePeopleBrowsing(window);
            var manager = Find(Find(window, "People"), "morgan@example.com");
            var branch = (ExpandCollapsePattern)manager.GetCurrentPattern(ExpandCollapsePattern.Pattern);
            TreeItemRealization.AwaitExpanded(initializeBranch: !compact,
                () => branch.Current.ExpandCollapseState == ExpandCollapseState.Expanded, branch.Expand, Await);
            var scroll = (ScrollPattern)Find(window, "People").GetCurrentPattern(ScrollPattern.Pattern);
            if (!scroll.Current.VerticallyScrollable)
                throw new InvalidOperationException("The representative browse fixture did not exercise a real reporting-tree viewport.");
            var unmeasuredView = scroll.Current.VerticalViewSize;
            var focusBeforeMeasurement = AutomationElement.FocusedElement?.Current.AutomationId;
            var ownerBeforeMeasurement = Find(window, "Email").Current.Name;
            TreeItemRealization.PrepareMeasuredScrollPosition(70,
                percent => scroll.SetScrollPercent(ScrollPattern.NoScroll, percent),
                () => scroll.Current.VerticalScrollPercent, Await);
            var offset = scroll.Current.VerticalScrollPercent;
            var originalView = scroll.Current.VerticalViewSize;
            var originalBounds = Find(window, "People").Current.BoundingRectangle;
            var originalFocus = AutomationElement.FocusedElement?.Current.AutomationId;
            if (Find(window, "Email").Current.Name != ownerBeforeMeasurement)
                throw new InvalidOperationException("Measuring the browsing fixture changed its selected Person.");
            Console.WriteLine($"Browsing baseline measurement: compact={compact}; " +
                $"unmeasuredViewPercent={unmeasuredView}; measuredViewPercent={originalView}; targetPercent={offset}; " +
                $"focusBefore='{focusBeforeMeasurement}'; focusAfter='{originalFocus}'; owner='{ownerBeforeMeasurement}'.");
            CaptureVisualEvidence(window, compact ? "search-browsing-before-compact" : "search-browsing-before-wide");
            Set(window, "SearchQuery", "orchid");
            Await(() => !IsVisible(window, "People") && !IsVisible(window, "Favorites"));
            if (Find(window, "SearchResults").Current.BoundingRectangle.Height <
                Find(window, "PeopleNavigation").Current.BoundingRectangle.Height - 150 * DpiScale(window))
                throw new InvalidOperationException("Search did not replace browsing with a full-height result list.");
            QueryAndSelect(window, "orchid", "avery@example.com", "Current", "Person note");
            if (compact) Await(() => !IsVisible(window, "PeopleNavigation"));
            EnsurePeopleBrowsing(window);
            var observedOffset = double.NaN;
            var favoritesVisible = false;
            try
            {
                Await(() =>
                {
                    observedOffset = scroll.Current.VerticalScrollPercent;
                    favoritesVisible = IsVisible(window, "Favorites");
                    return Math.Abs(observedOffset - offset) < 1 && favoritesVisible;
                });
            }
            catch (TimeoutException error)
            {
                var currentTree = Find(window, "People");
                var currentScroll = (ScrollPattern)currentTree.GetCurrentPattern(ScrollPattern.Pattern);
                var focused = AutomationElement.FocusedElement?.Current;
                CaptureVisualEvidence(window, compact ? "search-browsing-restoration-failed-compact" : "search-browsing-restoration-failed-wide");
                throw new TimeoutException("Search/clear did not restore the original browsing position within the unchanged 20 seconds. " +
                    $"compact={compact}; expectedPercent={offset}; lastPercent={observedOffset}; " +
                    $"freshPercent={currentScroll.Current.VerticalScrollPercent}; " +
                    $"originalViewPercent={originalView}; oldViewPercent={scroll.Current.VerticalViewSize}; freshViewPercent={currentScroll.Current.VerticalViewSize}; " +
                    $"favoritesVisible={favoritesVisible}; query='{Value(window, "SearchQuery")}'; " +
                    $"originalTreeBounds={originalBounds}; treeBounds={currentTree.Current.BoundingRectangle}; " +
                    $"peopleBounds={Find(window, "PeopleNavigation").Current.BoundingRectangle}; " +
                    $"originalFocusedId='{originalFocus}'; focusedId='{focused?.AutomationId}'; focusedName='{focused?.Name}'; " +
                    $"owner='{Find(window, "Email").Current.Name}'. No corrective actions were attempted.", error);
            }
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
        CaptureVisualEvidence(window, "reading-rejected-overlay-exact-draft-retained");
        Click(window, "ClosePeople");
        var state = (WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern);
        state.SetWindowVisualState(WindowVisualState.Maximized);
        try { Await(() => !IsVisible(window, "OpenPeople")); }
        catch (TimeoutException error)
        {
            throw new TimeoutException("Returning to wide reading after the completed single rejected People navigation " +
                "did not hide the compact People entry within 20 seconds. " +
                $"requestedState=Maximized; actualState={state.Current.WindowVisualState}; canMaximize={state.Current.CanMaximize}; " +
                $"windowPx={window.Current.BoundingRectangle}; hostPx={Find(window, "AdaptiveWorkspace").Current.BoundingRectangle}; " +
                $"viewportPx={Find(window, "ProfileScroll").Current.BoundingRectangle}; " +
                $"owner={Find(window, "Email").Current.Name}; title={Value(window, "PersonNoteTitle")}; " +
                $"status={Find(window, "Status").Current.Name}; {NativeResizeBounds((IntPtr)window.Current.NativeWindowHandle)}", error);
        }
    }
}
