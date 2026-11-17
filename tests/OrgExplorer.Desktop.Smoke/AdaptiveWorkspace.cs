using System.IO;
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
        var backdropHoverChecked = false;
        void Resize(double width, double height) => ResizeWindow(window, width, height);
        bool Visible(string id) => window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, id)) is { } element && !element.Current.IsOffscreen;
        Rect Bounds(string id) => Find(window, id).Current.BoundingRectangle;
        void PhotoAndInitials()
        {
            AssertPhotoVisible(window);
            var photo = AssertPhotoCircle(window);
            NavigateToManager(window, "morgan@example.com");
            AssertInitialsCircle(window, "M", photo);
            SelectTreePerson(window, "avery@example.com");
            AssertPhotoVisible(window);
            foreach (var id in new[] { "EditPreferredName", "ToggleFavorite", "EditProfile", "PersonMore" })
            {
                var observed = id + " not yet observed";
                AwaitPhoto(() =>
                {
                    if (!TryPhotoCircle(window, out var viewport, out observed)) return false;
                    var action = Find(window, id);
                    var bounds = action.Current.BoundingRectangle;
                    observed += $"; identity action {id}={bounds}";
                    return !action.Current.IsOffscreen && action.Current.IsEnabled && action.Current.IsKeyboardFocusable &&
                        !bounds.IsEmpty && bounds.Left >= viewport.Right &&
                        bounds.Right <= Bounds("ProfileScroll").Right + scale &&
                        bounds.Bottom <= Bounds("ProfileScroll").Top;
                }, () => observed);
            }
        }
        void AtSize(bool wide, string stateName)
        {
            Await(() => Visible("OpenPeople") != wide);
            PhotoAndInitials();
            var scroll = (ScrollPattern)Find(window, "ProfileScroll").GetCurrentPattern(ScrollPattern.Pattern);
            if (scroll.Current.HorizontallyScrollable)
                throw new InvalidOperationException("Ordinary adaptive profile reading requires horizontal scrolling.");
            if (scroll.Current.VerticallyScrollable) scroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
            var identity = Bounds("Email");
            var profile = Bounds("ProfileSummary");
            var notes = Bounds("NotesPane");
            var photo = AssertPhotoCircle(window);
            if (profile.Left > notes.Left + 2 || profile.Bottom > notes.Top + 2 ||
                Math.Abs(profile.Width - notes.Width) > 2 || notes.Width / scale < 359 ||
                notes.Right > Bounds("ProfileScroll").Right + 1 || identity.Top >= profile.Top)
                throw new InvalidOperationException($"Facts must precede full-width notes below anchored identity: identity={identity}, facts={profile}, notes={notes}.");
            foreach (var id in new[] { "DepartmentValue", "OfficeBuildingValue", "OfficeNumberValue", "OfficeStateValue",
                "OfficeCountryValue", "DirectoryName", "AliasValue", "LinkedInProfileUrlValue" })
                if (string.IsNullOrEmpty(Find(window, id).Current.Name))
                    throw new InvalidOperationException($"Ordinary fact lost its populated or neutral-dash slot: {id}.");
            foreach (var id in new[] { "NavigateManager", "OfficeBuildingValue", "OfficeNumberValue" })
            {
                var context = Bounds(id);
                if (!Visible(id) || context.Left < photo.Right || context.Bottom > Bounds("ProfileScroll").Top)
                    throw new InvalidOperationException($"Manager and Office must remain beside the photo above the scroll: {id}={context}.");
            }
            var officeOrder = window.FindAll(TreeScope.Descendants, new OrCondition(
                new PropertyCondition(AutomationElement.AutomationIdProperty, "OfficeBuildingValue"),
                new PropertyCondition(AutomationElement.AutomationIdProperty, "OfficeNumberValue")))
                .Cast<AutomationElement>().Select(element => element.Current.AutomationId);
            if (!officeOrder.SequenceEqual(new[] { "OfficeBuildingValue", "OfficeNumberValue" }))
                throw new InvalidOperationException("Office accessibility traversal must match its visual building/room order.");
            if (Bounds("DepartmentValue").Top < Bounds("ProfileScroll").Top)
                throw new InvalidOperationException("Department must be a labelled fact, not unlabelled identity text.");
            AssertTeamNameReading(window, null);
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
                var sample = new Rect(before.Left + before.Width * .6, before.Top + 8 * scale,
                    before.Width * .4 - 24 * scale, before.Height - 16 * scale);
                double? originalContrast = null;
                if (!backdropHoverChecked)
                {
                    originalContrast = ScreenContrast(sample);
                    if (originalContrast < 40)
                        throw new InvalidOperationException("The overlay fixture lacks readable Person content in its uncovered viewport.");
                }
                Click(window, "OpenPeople");
                Await(() => Visible("People"));
                var after = Bounds("ProfileScroll");
                if (before != after || Bounds("PeopleNavigation").Width / scale > 361)
                    throw new InvalidOperationException($"People overlay moved or narrowed the Person workspace: {before} -> {after}.");
                if (Bounds("PeopleNavigation").Left > Bounds("OpenPeople").Left + 2 * scale)
                    throw new InvalidOperationException("The compact People trigger and overlay do not share the left edge.");
                if (originalContrast is { } baselineContrast)
                {
                    if (sample.Left <= Bounds("PeopleNavigation").Right)
                        throw new InvalidOperationException("The backdrop pixel check overlaps the People panel.");
                    if (!SetCursorPos((int)(sample.Left + sample.Width / 2), (int)(sample.Top + sample.Height / 2)))
                        throw new InvalidOperationException("The hosted desktop could not hover the People backdrop.");
                    var hover = System.Diagnostics.Stopwatch.StartNew();
                    Await(() => hover.ElapsedMilliseconds >= 100 && ScreenContrast(sample) >= baselineContrast / 2);
                    backdropHoverChecked = true;
                }
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
        var anchoredPhoto = AssertPhotoCircle(window);
        var factsTop = Bounds("ProfileSummary").Top;
        personScroll.SetScrollPercent(ScrollPattern.NoScroll, 40);
        Await(() => personScroll.Current.VerticalScrollPercent > 39);
        if (Bounds("ProfileHeading") != anchoredHeading || Bounds("Email") != anchoredEmail ||
            AssertPhotoCircle(window) != anchoredPhoto ||
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
        PhotoAndInitials();
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

    private static double ScreenContrast(Rect bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new InvalidOperationException("The rendered backdrop sample is empty.");
        var left = (int)Math.Floor(bounds.Left);
        var top = (int)Math.Floor(bounds.Top);
        var width = (int)Math.Ceiling(bounds.Right) - left;
        var height = (int)Math.Ceiling(bounds.Bottom) - top;
        var dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) throw new InvalidOperationException("Actual desktop pixels are unavailable.");
        var memory = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            memory = CreateCompatibleDC(dc);
            bitmap = CreateCompatibleBitmap(dc, width, height);
            if (memory == IntPtr.Zero || bitmap == IntPtr.Zero)
                throw new InvalidOperationException("Actual contrast capture could not allocate its pixel buffer.");
            previous = SelectObject(memory, bitmap);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1) ||
                !BitBlt(memory, 0, 0, width, height, dc, left, top, 0x40CC0020))
                throw new InvalidOperationException("Actual desktop contrast capture failed.");
            var minimum = 255d;
            var maximum = 0d;
            for (var y = (int)Math.Ceiling(bounds.Top); y < bounds.Bottom; y += 6)
                for (var x = (int)Math.Ceiling(bounds.Left); x < bounds.Right; x += 6)
                {
                    var color = GetPixel(memory, x - left, y - top);
                    if (color == uint.MaxValue)
                        throw new InvalidOperationException("Actual backdrop pixels are unavailable on this desktop.");
                    var luminance = ((color & 255) + ((color >> 8) & 255) + ((color >> 16) & 255)) / 3d;
                    minimum = Math.Min(minimum, luminance);
                    maximum = Math.Max(maximum, luminance);
                }
            return maximum - minimum;
        }
        finally
        {
            if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, dc);
        }
    }

    private static void AssertLongAndMissingFacts(AutomationElement window, string workspace)
    {
        var ids = new[] { "DepartmentValue", "OfficeStateValue",
            "OfficeCountryValue", "DirectoryName", "AliasValue", "LinkedInProfileUrlValue" };
        var scroll = (ScrollPattern)Find(window, "ProfileScroll").GetCurrentPattern(ScrollPattern.Pattern);
        ControlReadiness.ScrollToTopIfScrollable(() => scroll.Current.VerticallyScrollable,
            () => scroll.SetScrollPercent(ScrollPattern.NoScroll, 0));
        var bytes = OrgExplorer.TestFixtures.WorkspaceFixtures.Bytes(workspace);
        foreach (var (id, expected) in new[] { ("Email", "avery@example.com"), ("JobTitleValue", "Scientist"),
            ("OfficeBuildingValue", "Juniper"), ("OfficeNumberValue", "007B"), ("DepartmentValue", "Research"),
            ("OfficeStateValue", "Ontario"), ("OfficeCountryValue", "Canada"), ("DirectoryName", "Avery Directory"),
            ("AliasValue", "avhandle"), ("LinkedInProfileUrlValue", "https://www.linkedin.com/in/fictitious-avery") })
            AssertPrimaryCopy(window, Find(window, id), expected, freshFeedback: id is "Email" or "DepartmentValue");
        foreach (var key in new byte[] { 0x0D, 0x20 })
            AssertFocusedCopy(window, Find(window, "Email"), "avery@example.com", key);
        AwaitCopyFeedbackGone(window);
        AssertWorkspaceBytes(workspace, bytes);
        var lefts = ids.Select(id => Find(window, id).Current.BoundingRectangle.Left).ToArray();
        QueryAndSelect(window, "longreading", "longread@example.com", "Current", "Profile:");
        Await(() => Value(window, "SearchQuery") == "" && IsVisible(window, "People") &&
            Find(window, "Email").Current.Name == "longread@example.com");
        ControlReadiness.ScrollToTopIfScrollable(() => scroll.Current.VerticallyScrollable,
            () => scroll.SetScrollPercent(ScrollPattern.NoScroll, 0));
        for (var index = 0; index < ids.Length; index++)
            if (Math.Abs(Find(window, ids[index]).Current.BoundingRectangle.Left - lefts[index]) > 2)
                throw new InvalidOperationException("Populated and missing facts repacked a width-stable labelled slot: " + ids[index]);
        foreach (var id in new[] { "DepartmentValue", "OfficeCountryValue", "LinkedInProfileUrlValue" })
            if (Find(window, id).Current.Name != "—")
                throw new InvalidOperationException("A missing ordinary fact lost its neutral-dash slot: " + id);
        const string building = "South campus research and collaboration building with a deliberately long descriptive location";
        AssertKeyboardCopy(window, "OfficeBuildingValue", building);
        AssertCopyHint(window, Find(window, "OfficeBuildingValue"), building);
        AssertPrimaryCopy(window, Find(window, "OfficeBuildingValue"), building);
        const string state = "A long state or province description that must wrap without shrinking the ordinary reading type";
        AssertCopyHint(window, Find(window, "OfficeStateValue"), state);
        AssertPrimaryCopy(window, Find(window, "OfficeStateValue"), state);
        if (Find(window, "OfficeStateValue").Current.BoundingRectangle.Height <= Find(window, "Email").Current.BoundingRectangle.Height * 1.5)
            throw new InvalidOperationException("The responsive copy fixture did not exercise actually wrapped state/province text.");
        foreach (var key in new byte[] { 0x0D, 0x20 })
            AssertFocusedCopy(window, Find(window, "OfficeStateValue"), state, key);
        AwaitCopyFeedbackGone(window);
        foreach (var id in new[] { "DepartmentValue", "OfficeCountryValue", "LinkedInProfileUrlValue" })
            AssertMissingCopy(window, Find(window, id));
        if (Find(window, "ProfileHeading").Current.Name !=
            "Alexandra Reference with a deliberately long Directory name for accessible Person reading" ||
            Find(window, "PhotoPlaceholder").Current.IsOffscreen || scroll.Current.HorizontallyScrollable)
            throw new InvalidOperationException("Long identity/facts lost full accessible text or initials, or require horizontal scrolling.");
        AssertInitialsCircle(window, "AR");
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
            var originalPosition = ReadBrowsingPosition(Find(window, "People"));
            var offset = originalPosition.Percent;
            var originalView = scroll.Current.VerticalViewSize;
            var originalBounds = Find(window, "People").Current.BoundingRectangle;
            var originalFocus = AutomationElement.FocusedElement?.Current.AutomationId;
            if (Find(window, "Email").Current.Name != ownerBeforeMeasurement)
                throw new InvalidOperationException("Measuring the browsing fixture changed its selected Person.");
            Console.WriteLine($"Browsing baseline measurement: compact={compact}; " +
                $"unmeasuredViewPercent={unmeasuredView}; measuredViewPercent={originalView}; targetPercent={offset}; " +
                $"focusBefore='{focusBeforeMeasurement}'; focusAfter='{originalFocus}'; owner='{ownerBeforeMeasurement}'; " +
                $"rows={DescribeBrowsingRows(originalPosition)}.");
            CaptureVisualEvidence(window, compact ? "search-browsing-before-compact" : "search-browsing-before-wide");
            Set(window, "SearchQuery", "orchid");
            Await(() => !IsVisible(window, "People") && !IsVisible(window, "Favorites"));
            if (Find(window, "SearchResults").Current.BoundingRectangle.Height <
                Find(window, "PeopleNavigation").Current.BoundingRectangle.Height - 150 * DpiScale(window))
                throw new InvalidOperationException("Search did not replace browsing with a full-height result list.");
            var beforeOwner = CurrentPersonEmail(window);
            var result = ControlReadiness.AwaitControl(
                () => Find(window, "SearchResults").FindFirst(TreeScope.Children,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "avery@example.com")),
                row => row is not null && row.Current.HelpText.Contains("Current") &&
                    row.Current.HelpText.Contains("Person note"), Await);
            ((SelectionItemPattern)result.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            if (CurrentPersonEmail(window) != beforeOwner)
                throw new InvalidOperationException("Highlighting grouped results changed the open Person.");
            Click(window, "ClearSearch");
            EnsurePeopleBrowsing(window);
            var observedOffset = double.NaN;
            var favoritesVisible = false;
            TreeItemRealization.BrowsingPosition? observedPosition = null;
            try
            {
                Await(() =>
                {
                    observedPosition = ReadBrowsingPosition(Find(window, "People"));
                    observedOffset = observedPosition.Percent;
                    favoritesVisible = IsVisible(window, "Favorites");
                    return TreeItemRealization.IsBrowsingPositionRestored(originalPosition, observedPosition) && favoritesVisible;
                });
            }
            catch (TimeoutException error)
            {
                var currentTree = Find(window, "People");
                var currentScroll = (ScrollPattern)currentTree.GetCurrentPattern(ScrollPattern.Pattern);
                var focused = AutomationElement.FocusedElement?.Current;
                CaptureVisualEvidence(window, compact ? "search-browsing-restoration-failed-compact" : "search-browsing-restoration-failed-wide");
                throw new TimeoutException("Search/clear did not restore the original visible Person rows and pixel positions within 20 seconds. " +
                    $"compact={compact}; expectedPercent={offset}; lastPercent={observedOffset}; " +
                    $"freshPercent={currentScroll.Current.VerticalScrollPercent}; " +
                    $"originalViewPercent={originalView}; oldViewPercent={scroll.Current.VerticalViewSize}; freshViewPercent={currentScroll.Current.VerticalViewSize}; " +
                    $"favoritesVisible={favoritesVisible}; query='{Value(window, "SearchQuery")}'; " +
                    $"originalTreeBounds={originalBounds}; treeBounds={currentTree.Current.BoundingRectangle}; " +
                    $"peopleBounds={Find(window, "PeopleNavigation").Current.BoundingRectangle}; " +
                    $"originalFocusedId='{originalFocus}'; focusedId='{focused?.AutomationId}'; focusedName='{focused?.Name}'; " +
                    $"owner='{Find(window, "Email").Current.Name}'; " +
                    $"originalRows={DescribeBrowsingRows(originalPosition)}; " +
                    $"observedRows={(observedPosition is null ? "unavailable" : DescribeBrowsingRows(observedPosition))}. " +
                    "No corrective actions were attempted.", error);
            }
            Console.WriteLine($"PASS: browsing restored every original visible Person at its original position; compact={compact}; " +
                $"originalPercent={offset}; observedPercent={observedOffset}; originalViewPercent={originalView}; " +
                $"observedViewPercent={scroll.Current.VerticalViewSize}.");
            CaptureVisualEvidence(window, compact ? "search-browsing-restored-compact" : "search-browsing-restored-wide");
            scroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
            Await(() => ((ExpandCollapsePattern)Find(Find(window, "People"), "morgan@example.com")
                .GetCurrentPattern(ExpandCollapsePattern.Pattern)).Current.ExpandCollapseState == ExpandCollapseState.Expanded);
        }
        ((WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern)).SetWindowVisualState(WindowVisualState.Maximized);
        Await(() => !IsVisible(window, "OpenPeople"));
    }

    private static TreeItemRealization.BrowsingPosition ReadBrowsingPosition(AutomationElement tree)
    {
        var viewport = tree.Current.BoundingRectangle;
        var rows = tree.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem))
            .Cast<AutomationElement>().Select(row => row.Current)
            .Where(row => !row.IsOffscreen && !row.BoundingRectangle.IsEmpty &&
                row.BoundingRectangle.IntersectsWith(viewport))
            .Select(row => new TreeItemRealization.BrowsingRow(row.AutomationId,
                row.BoundingRectangle.Left - viewport.Left, row.BoundingRectangle.Top - viewport.Top)).ToArray();
        if (rows.Length == 0 || rows.Any(row => string.IsNullOrEmpty(row.Email)))
            throw new InvalidOperationException("Browsing restoration requires actual visible Person identities and geometry.");
        return new(((ScrollPattern)tree.GetCurrentPattern(ScrollPattern.Pattern)).Current.VerticalScrollPercent, rows);
    }

    private static string DescribeBrowsingRows(TreeItemRealization.BrowsingPosition position) =>
        string.Join("; ", position.Rows.Select(row => $"{row.Email}:({row.Left:F1},{row.Top:F1})"));

    private static bool IsVisible(AutomationElement window, string id) =>
        window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id)) is { } element &&
        !element.Current.IsOffscreen;

    private static void EnsurePeopleBrowsing(AutomationElement window) =>
        ControlReadiness.PreparePeopleBrowsing(
            () => Value(window, "SearchQuery"), () => Set(window, "SearchQuery", ""),
            () => IsVisible(window, "OpenPeople") && !IsVisible(window, "PeopleNavigation"),
            () => Click(window, "OpenPeople"), () => IsVisible(window, "People"), Await);

    private static void AssertResponsiveProfileForm(AutomationElement window)
    {
        var scale = DpiScale(window);
        Rect Bounds(string id) => Find(window, id).Current.BoundingRectangle;
        void AtSize(string state)
        {
            var scroll = ControlReadiness.AwaitPattern(() => Find(window, "ProfileScroll"),
                control =>
                {
                    var observed = control.Current;
                    return observed.IsEnabled && !observed.IsOffscreen && !observed.BoundingRectangle.IsEmpty &&
                        observed.BoundingRectangle.Width > 0 && observed.BoundingRectangle.Height > 0;
                }, control => control.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern)
                    ? (ScrollPattern)pattern : null, Await);
            if (scroll.Current.HorizontallyScrollable)
                throw new InvalidOperationException("Inline profile editing requires horizontal page scrolling.");
            if (scroll.Current.VerticallyScrollable) scroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
            var identity = Bounds("Email");
            var save = Bounds("SaveProfile");
            var cancel = Bounds("CancelProfile");
            if (!IsVisible(window, "ProfileEditingState") || !IsVisible(window, "SaveProfile") ||
                !IsVisible(window, "CancelProfile") || identity.Bottom > save.Top ||
                Math.Max(save.Bottom, cancel.Bottom) > Bounds("ProfileScroll").Top + scale)
                throw new InvalidOperationException("The contextual profile lost its anchored identity or explicit actions.");
            foreach (var id in new[] { "ChoosePhoto", "ClearPhoto" })
            {
                var action = Find(window, id);
                var bounds = action.Current.BoundingRectangle;
                if (action.Current.IsOffscreen || !action.Current.IsEnabled || bounds.IsEmpty ||
                    bounds.Bottom > Bounds("ProfileScroll").Top + scale)
                    throw new InvalidOperationException($"Photo editing action is inaccessible above the shared scroll: {id}={bounds}.");
            }
            var actionOrder = window.FindAll(TreeScope.Descendants, new OrCondition(
                new PropertyCondition(AutomationElement.AutomationIdProperty, "ChoosePhoto"),
                new PropertyCondition(AutomationElement.AutomationIdProperty, "ClearPhoto"),
                new PropertyCondition(AutomationElement.AutomationIdProperty, "CancelProfile"),
                new PropertyCondition(AutomationElement.AutomationIdProperty, "SaveProfile")))
                .Cast<AutomationElement>().Select(element => element.Current.AutomationId);
            if (!actionOrder.SequenceEqual(new[] { "ChoosePhoto", "ClearPhoto", "CancelProfile", "SaveProfile" }))
                throw new InvalidOperationException("Profile action accessibility traversal must match its visual order.");
            CaptureVisualEvidence(window, state + "-top");
            Find(window, "DirectoryNameEditor").SetFocus();
            Await(() => Find(window, "DirectoryNameEditor").Current.HasKeyboardFocus);
            foreach (var id in new[] { "DirectoryNameEditor", "Alias", "JobTitle", "Department",
                "OfficeBuilding", "OfficeNumber", "OfficeState", "OfficeCountry", "LinkedInProfileUrl", "ManagerChoice" })
            {
                var editor = Find(window, id);
                Await(() => editor.Current.HasKeyboardFocus && !editor.Current.IsOffscreen);
                var bounds = editor.Current.BoundingRectangle;
                var viewport = Bounds("ProfileScroll");
                if (!editor.Current.IsKeyboardFocusable || bounds.Width / scale < 180 ||
                    bounds.Left < viewport.Left - scale || bounds.Right > viewport.Right + scale ||
                    bounds.Top < viewport.Top - scale || bounds.Bottom > viewport.Bottom + scale ||
                    Bounds("Email") != identity || Bounds("SaveProfile") != save)
                    throw new InvalidOperationException($"Inline profile field {id} is clipped or moves identity/actions at {state}: {bounds}, viewport={viewport}.");
                if (id is not "ManagerChoice") PressCopyKey(0x09);
            }
            if (Bounds("ProfileSummary").Bottom > Bounds("NotesPane").Top + scale ||
                Find(window, "Email").Current.ControlType != ControlType.Text)
                throw new InvalidOperationException("Notes joined the profile draft or stable email became editable.");
            CaptureVisualEvidence(window, state + "-manager");
        }
        ResizeWindow(window, 700, 500);
        AtSize("profile-inline-minimum-700-500");
        var savedOffice = Value(window, "OfficeNumber");
        Set(window, "OfficeNumber", "Unsaved minimum-window office");
        try
        {
            Await(() => IsVisible(window, "OperationalNotice"));
            AtSize("profile-inline-minimum-700-500-dirty");
        }
        finally { Set(window, "OfficeNumber", savedOffice); }
        CancelProfileEditing(window);
        foreach (var identityState in CompactIdentityStateCases)
        {
            QueryAndSelect(window, identityState.Email, identityState.Email,
                identityState.Archived ? "Archived" : "Current", "Profile:");
            if (identityState.Archived)
            {
                ResizeWindow(window, 1100, 900);
                var archivedHost = Bounds("AdaptiveWorkspace");
                var archivedPeoplePercent = RenderedPeoplePercent(window);
                DragDivider(window, "PeopleDivider", archivedHost.Right - 20);
                Await(() => Math.Abs(Bounds("ProfileScroll").Width / scale - 408) < 2);
                var photo = Find(window, "Photo").Current.BoundingRectangle;
                var marker = Find(window, "PersonArchivalStatus");
                if (marker.Current.IsOffscreen || marker.Current.Name != "Archived Person" ||
                    Bounds("ProfileScroll").Top - photo.Bottom > 17 * scale ||
                    marker.Current.BoundingRectangle.Right > Bounds("ProfileScroll").Right + scale)
                    throw new InvalidOperationException("Archived reading wraps or loses its visible marker at minimum Person allocation.");
                foreach (var id in new[] { "EditPreferredName", "ToggleFavorite", "EditProfile", "PersonMore" })
                {
                    var action = Find(window, id);
                    if (action.Current.IsOffscreen || !action.Current.IsEnabled || !action.Current.IsKeyboardFocusable ||
                        action.Current.BoundingRectangle.Bottom > photo.Bottom + scale)
                        throw new InvalidOperationException("Narrow archived reading displaced an identity action: " + id);
                }
                CaptureVisualEvidence(window, "profile-reading-narrow-" + identityState.Email.Split('@')[0]);
                DragDivider(window, "PeopleDivider", archivedHost.Left + (archivedHost.Width - 8 * scale) * archivedPeoplePercent / 100);
                ResizeWindow(window, 700, 500);
            }
            BeginProfileEditing(window);
            Find(window, "OfficeNumber").SetFocus();
            Await(() => Find(window, "OfficeNumber").Current.HasKeyboardFocus);
            Set(window, "OfficeNumber", "Unsaved compact-state office");
            try
            {
                Await(() => IsVisible(window, "OperationalNotice"));
                var heading = Find(window, "ProfileHeading");
                if (heading.Current.BoundingRectangle.Height / scale > 33 ||
                    !heading.Current.Name.Contains((identityState.PreferredName ?? identityState.DirectoryName).Split("\r\n").Last(),
                        StringComparison.Ordinal) ||
                    IsVisible(window, "PersonArchivalStatus") != identityState.Archived)
                    throw new InvalidOperationException("Compact saved-name or archived identity is not bounded and accessible.");
                AtSize("profile-inline-minimum-700-500-" + identityState.Email.Split('@')[0]);
            }
            finally { CancelProfileEditing(window); }
        }
        QueryAndSelect(window, "avery@example.com", "avery@example.com", "Current", "Profile:");
        BeginProfileEditing(window);
        ResizeWindow(window, 1100, 900);
        AtSize("profile-inline-ordinary-1100-900");
        foreach (var hostWidth in new[] { 959d, 960d, 961d })
        {
            var chrome = window.Current.BoundingRectangle.Width / scale - Bounds("AdaptiveWorkspace").Width / scale;
            ResizeWindow(window, hostWidth + chrome, 900);
            AtSize($"profile-inline-boundary-{hostWidth:0}");
        }
        var state = (WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern);
        state.SetWindowVisualState(WindowVisualState.Maximized);
        Await(() => state.Current.WindowVisualState == WindowVisualState.Maximized);
        AtSize("profile-inline-maximized");
        var host = Bounds("AdaptiveWorkspace");
        var peoplePercent = RenderedPeoplePercent(window);
        DragDivider(window, "PeopleDivider", host.Right - 20);
        Await(() => Math.Abs(Bounds("ProfileScroll").Width / scale - 408) < 2);
        AtSize("profile-inline-narrow-person");
        DragDivider(window, "PeopleDivider", host.Left + (host.Width - 8 * scale) * peoplePercent / 100);
        Await(() => Math.Abs(RenderedPeoplePercent(window) - peoplePercent) < 1);
    }

    private static void AssertResponsiveOwnedTeamName(AutomationElement window, string import)
    {
        NavigateToManager(window, "morgan@example.com");
        const string name = "Platform engineering and developer experience across the current reporting subtree";
        BeginProfileEditing(window);
        Set(window, "TeamNameEditor", name);
        SaveProfileEditing(window);
        File.WriteAllText(import, SearchImport("""
            {"email":"morgan@example.com","managerEmail":"alex@example.com"}
            """, 2));
        ApplyReviewedHistoryImport(window);
        NavigateToManager(window, "alex@example.com");
        BeginProfileEditing(window);
        Set(window, "TeamNameEditor", name);
        SaveProfileEditing(window);
        AssertTeamNameReading(window, name);
        SelectTreePerson(window, "morgan@example.com");
        var scale = DpiScale(window);
        void AtSize(string stateName)
        {
            AssertTeamNamePath(window, name, name);
            AssertPrimaryCopy(window, FindTeamNameSegment(window, 0), name,
                freshFeedback: stateName == "team-name-ordinary-1100-900");
            AssertPrimaryCopy(window, FindTeamNameSegment(window, 1), name,
                freshFeedback: stateName == "team-name-ordinary-1100-900");
            CaptureVisualEvidence(window, stateName + "-reading");
            BeginProfileEditing(window);
            var editor = Find(window, "TeamNameEditor");
            editor.SetFocus();
            Await(() => editor.Current.HasKeyboardFocus && !editor.Current.IsOffscreen);
            AssertValue(window, "TeamNameEditor", name);
            var bounds = editor.Current.BoundingRectangle;
            var viewport = Find(window, "ProfileScroll").Current.BoundingRectangle;
            if (!editor.Current.IsEnabled || bounds.Width / scale < 180 ||
                bounds.Left < viewport.Left - scale || bounds.Right > viewport.Right + scale ||
                bounds.Top < viewport.Top - scale || bounds.Bottom > viewport.Bottom + scale ||
                !IsVisible(window, "SaveProfile") || !IsVisible(window, "CancelProfile"))
                throw new InvalidOperationException($"Owned Team-name editing is clipped at {stateName}: {bounds}, viewport={viewport}.");
            CaptureVisualEvidence(window, stateName + "-editor");
            CancelProfileEditing(window);
        }
        ResizeWindow(window, 700, 500);
        AtSize("team-name-minimum-700-500");
        ResizeWindow(window, 1100, 900);
        AtSize("team-name-ordinary-1100-900");
        AssertTeamNameOwnerAttribution(window, "Morgan", "morgan@example.com");
        AssertTeamNameOwnerAttribution(window, "Alex", "alex@example.com", 1);
        AssertMouseCopy(window, FindTeamNameSegment(window, 0), name);
        AssertKeyboardCopy(window, FindTeamNameSegment(window, 1), name);
        foreach (var index in new[] { 0, 1 })
            foreach (var key in new byte[] { 0x0D, 0x20 })
                AssertFocusedCopy(window, FindTeamNameSegment(window, index), name, key);
        AwaitCopyFeedbackGone(window);
        foreach (var width in new[] { 959d, 960d, 961d })
        {
            var chrome = window.Current.BoundingRectangle.Width / scale -
                Find(window, "AdaptiveWorkspace").Current.BoundingRectangle.Width / scale;
            ResizeWindow(window, width + chrome, 900);
            AtSize($"team-name-boundary-{width:0}");
        }
        var state = (WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern);
        state.SetWindowVisualState(WindowVisualState.Maximized);
        Await(() => state.Current.WindowVisualState == WindowVisualState.Maximized);
        AtSize("team-name-maximized");
        var host = Find(window, "AdaptiveWorkspace").Current.BoundingRectangle;
        var percent = RenderedPeoplePercent(window);
        DragDivider(window, "PeopleDivider", host.Right - 20);
        Await(() => Math.Abs(Find(window, "ProfileScroll").Current.BoundingRectangle.Width / scale - 408) < 2);
        AtSize("team-name-narrow-person");
        DragDivider(window, "PeopleDivider", host.Left + (host.Width - 8 * scale) * percent / 100);
        Await(() => Math.Abs(RenderedPeoplePercent(window) - percent) < 1);
        ResizeWindow(window, 1100, 900);
        QueryAndSelect(window, "007b", "avery@example.com", "Current", "Profile:");
        AssertTeamNamePath(window, name, name);
        BeginProfileEditing(window);
        if (IsVisible(window, "TeamNameEditor"))
            throw new InvalidOperationException("Responsive inherited context exposed an owned-name editor on the report.");
        CancelProfileEditing(window);
        NavigateToManager(window, "morgan@example.com");
        BeginProfileEditing(window);
        Set(window, "TeamNameEditor", "");
        SaveProfileEditing(window);
        AssertTeamNameReading(window, name);
        NavigateToManager(window, "alex@example.com");
        BeginProfileEditing(window);
        Set(window, "TeamNameEditor", "");
        SaveProfileEditing(window);
        AssertTeamNameReading(window, null);
        File.WriteAllText(import, SearchImport("""{"email":"morgan@example.com","managerEmail":null}""", 89));
        ApplyReviewedHistoryImport(window);
        QueryAndSelect(window, "007b", "avery@example.com", "Current", "Profile:");
        AssertTeamNameReading(window, null);
    }

    private static void AssertProtectedPeopleOverlay(AutomationElement window)
    {
        ResizeWindow(window, 700, 900);
        var before = Find(window, "ProfileScroll").Current.BoundingRectangle;
        string Metrics(string phase) => $"{phase}: viewport={Find(window, "ProfileScroll").Current.BoundingRectangle}; " +
            $"owner={Find(window, "Email").Current.Name}; title='{Value(window, "PersonNoteTitle")}'; " +
            $"editor={Find(window, "PersonNoteMarkdown").Current.BoundingRectangle}; panel={IsVisible(window, "PeopleNavigation")}; " +
            $"note={Find(window, "PersonNoteHeading").Current.HelpText}; markdown='{Value(window, "PersonNoteMarkdown")}'; " +
            $"editing={((SelectionItemPattern)Find(window, "PersonNoteEditTab").GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected}; " +
            $"notice={Find(window, "OperationalNotice").Current.BoundingRectangle}; " +
            $"summary={Find(window, "NoticeSummary").Current.Name}";
        var initialMetrics = Metrics("immediately-after-resize");
        var note = Find(window, "PersonNoteHeading").Current.HelpText;
        var draft = Value(window, "PersonNoteMarkdown");
        EnsurePeopleBrowsing(window);
        var openedMetrics = Metrics("overlay-open");
        SelectTreePerson(window, "casey@example.com", expectNavigation: false);
        Await(() => Status(window).Contains("Person note") &&
            Status(window).Contains("Retry or discard"));
        if (!IsVisible(window, "PeopleNavigation") ||
            before != Find(window, "ProfileScroll").Current.BoundingRectangle ||
            Find(window, "Email").Current.Name != "avery@example.com" || Value(window, "PersonNoteTitle") != "" ||
            Find(window, "PersonNoteHeading").Current.HelpText != note || Value(window, "PersonNoteMarkdown") != draft)
        {
            var rejectedMetrics = Metrics("rejected-selection");
            CaptureFailureEvidence(window, "reading-rejected-overlay-failure");
            throw new InvalidOperationException("Rejected overlay navigation lost the original owner, exact failed editor, geometry or navigation panel. " +
                $"{initialMetrics}; {openedMetrics}; {rejectedMetrics}");
        }
        Console.WriteLine($"OVERLAY-OBSERVATION {initialMetrics}; {openedMetrics}; {Metrics("rejected-selection")}");
        AssertSelectionMatchesProfile(window);
        CaptureVisualEvidence(window, "reading-rejected-overlay-exact-draft-retained");
        QueryAndSelect(window, "casey@example", "casey@example.com", "Current", "Profile:", expectNavigation: false);
        Await(() => Status(window).Contains("Person note") &&
            Status(window).Contains("Retry or discard"));
        if (!IsVisible(window, "PeopleNavigation") || Value(window, "SearchQuery") != "casey@example" ||
            before != Find(window, "ProfileScroll").Current.BoundingRectangle ||
            Find(window, "Email").Current.Name != "avery@example.com" ||
            Find(window, "PersonNoteHeading").Current.HelpText != note || Value(window, "PersonNoteTitle") != "" ||
            Value(window, "PersonNoteMarkdown") != draft)
        {
            var rejectedMetrics = Metrics("rejected-search");
            CaptureFailureEvidence(window, "search-rejected-overlay-failure");
            throw new InvalidOperationException("Rejected search activation lost overlay/query, exact failed editor, owner or unchanged viewport. " +
                $"{initialMetrics}; {openedMetrics}; {rejectedMetrics}");
        }
        AssertNoteView(window, "Person", editing: true);
        CaptureVisualEvidence(window, "search-rejected-overlay-exact-draft-retained");
        Click(window, "ClearSearch");
        Await(() => IsVisible(window, "People"));
        Click(window, "ClosePeople");
        Await(() => !IsVisible(window, "PeopleNavigation") && Find(window, "OpenPeople").Current.HasKeyboardFocus);
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
                $"status={Status(window)}; {NativeResizeBounds((IntPtr)window.Current.NativeWindowHandle)}", error);
        }
    }
}
