using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
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
        DpiScale(window, out _);

    private static double DpiScale(AutomationElement window, out IntPtr handle)
    {
        handle = (IntPtr)window.Current.NativeWindowHandle;
        return GetDpiForWindow(handle) / 96d;
    }

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
            AssertPersonCategory(window, "Employee");
            AssertTeamNameReading(window, null);
            AssertReportingStructure(window, 1, 1, 0);
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
            var controls = ControlObservation.Capture(window, "Email", "ProfileSummary", "NotesPane", "ProfileScroll",
                "DepartmentValue", "OfficeBuildingValue", "OfficeNumberValue", "OfficeStateValue", "OfficeCountryValue",
                "DirectoryName", "AliasValue", "LinkedInProfileUrlValue", "NavigateManager", "PeopleNavigation", "People");
            var identity = controls.Bounds("Email");
            var profile = controls.Bounds("ProfileSummary");
            var notes = controls.Bounds("NotesPane");
            var viewport = controls.Bounds("ProfileScroll");
            var photo = AssertPhotoCircle(window);
            if (profile.Left > notes.Left + 2 || profile.Bottom > notes.Top + 2 ||
                Math.Abs(profile.Width - notes.Width) > 2 || notes.Width / scale < 359 ||
                notes.Right > viewport.Right + 1 || identity.Top >= profile.Top)
                throw new InvalidOperationException($"Facts must precede full-width notes below anchored identity: identity={identity}, facts={profile}, notes={notes}.");
            foreach (var id in new[] { "DepartmentValue", "OfficeBuildingValue", "OfficeNumberValue", "OfficeStateValue",
                "OfficeCountryValue", "DirectoryName", "AliasValue", "LinkedInProfileUrlValue" })
                if (string.IsNullOrEmpty(controls[id].Cached.Name))
                    throw new InvalidOperationException($"Ordinary fact lost its populated or neutral-dash slot: {id}.");
            foreach (var id in new[] { "NavigateManager", "OfficeBuildingValue", "OfficeNumberValue" })
            {
                var context = controls.Bounds(id);
                if (!controls.IsVisible(id) || context.Left < photo.Right || context.Bottom > viewport.Top)
                    throw new InvalidOperationException($"Manager and Office must remain beside the photo above the scroll: {id}={context}.");
            }
            var officeOrder = window.FindAll(TreeScope.Descendants, new OrCondition(
                new PropertyCondition(AutomationElement.AutomationIdProperty, "OfficeBuildingValue"),
                new PropertyCondition(AutomationElement.AutomationIdProperty, "OfficeNumberValue")))
                .Cast<AutomationElement>().Select(element => element.Current.AutomationId);
            if (!officeOrder.SequenceEqual(new[] { "OfficeBuildingValue", "OfficeNumberValue" }))
                throw new InvalidOperationException("Office accessibility traversal must match its visual building/room order.");
            if (controls.Bounds("DepartmentValue").Top < viewport.Top)
                throw new InvalidOperationException("Department must be a labelled fact, not unlabelled identity text.");
            AssertTeamNameReading(window, null);
            AssertPersonCategory(window, "Employee");
            AssertReportingStructure(window, null);
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
            QueryAndSelect(window, "longreading", "v-longread@example.com", "Current", "Profile:");
            AssertPersonCategory(window, "Vendor");
            AssertTeamNameReading(window, null);
            AssertReportingStructure(window, null);
            QueryAndSelect(window, "007b", "avery@example.com", "Current", "Profile:");
            AssertPersonCategory(window, "Employee");
            AssertReportingTreeMenu(window, "morgan@example.com", "avery@example.com", stateName);
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
        using var observed = HarnessRuntime.Measure(HarnessRuntime.Work.Pixels);
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
        QueryAndSelect(window, "longreading", "v-longread@example.com", "Current", "Profile:");
        Await(() => Value(window, "SearchQuery") == "" && IsVisible(window, "People") &&
            Find(window, "Email").Current.Name == "v-longread@example.com");
        AssertPersonCategory(window, "Vendor");
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

    private static void AssertReportingTreeMenu(AutomationElement window, string managerEmail, string reportEmail, string state)
    {
        var panelWasOpen = IsVisible(window, "PeopleNavigation");
        var compact = IsVisible(window, "OpenPeople");
        var owner = CurrentPersonEmail(window);
        string[] Snapshot()
        {
            var ids = new[] { "Email", "DirectoryName", "ProfileEditingState", "DirectoryNameEditor", "Alias", "JobTitle",
                "Department", "OfficeBuilding", "OfficeNumber", "OfficeState", "OfficeCountry", "LinkedInProfileUrl",
                "PersonNoteHeading", "TeamNoteHeading", "PersonNoteTitle", "PersonNoteMarkdown", "TeamNoteTitle",
                "TeamNoteMarkdown", "PersonNoteStatus", "TeamNoteStatus" };
            using var observed = HarnessRuntime.Measure(HarnessRuntime.Work.UiaDiscovery);
            var cache = new CacheRequest { TreeScope = TreeScope.Element };
            cache.Add(AutomationElement.AutomationIdProperty);
            cache.Add(AutomationElement.NameProperty);
            cache.Add(AutomationElement.HelpTextProperty);
            cache.Add(ValuePattern.ValueProperty);
            AutomationElementCollection controls;
            using (cache.Activate())
                controls = window.FindAll(TreeScope.Descendants, new OrCondition(ids.Select(id =>
                    new PropertyCondition(AutomationElement.AutomationIdProperty, id)).ToArray()));
            return controls.Cast<AutomationElement>().OrderBy(element => element.Cached.AutomationId)
                .Select(element => $"{element.Cached.AutomationId}:{element.Cached.Name}:{element.Cached.HelpText}:" +
                    element.GetCachedPropertyValue(ValuePattern.ValueProperty)).ToArray();
        }
        var before = Snapshot();
        EnsurePeopleBrowsing(window);
        var treeScroll = (ScrollPattern)Find(window, "People").GetCurrentPattern(ScrollPattern.Pattern);
        ControlReadiness.ScrollToTopIfScrollable(() => treeScroll.Current.VerticallyScrollable,
            () => treeScroll.SetScrollPercent(ScrollPattern.NoScroll, 0));
        AutomationElement Row(string email) => Find(Find(window, "People"), email);
        ExpandCollapsePattern Expansion(string email) => (ExpandCollapsePattern)Row(email).GetCurrentPattern(ExpandCollapsePattern.Pattern);
        AutomationElement? Menu() => AutomationElement.RootElement.FindAll(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ProcessIdProperty, window.Current.ProcessId),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu),
                new PropertyCondition(AutomationElement.ClassNameProperty, "ContextMenu")))
            .Cast<AutomationElement>().FirstOrDefault(menu => !menu.Current.IsOffscreen);
        void RightClickRow(string email)
        {
            var bounds = Row(email).Current.BoundingRectangle;
            var viewport = Find(window, "People").Current.BoundingRectangle;
            var scale = DpiScale(window);
            var (x, y) = TreeItemRealization.ReportingHeaderPoint(
                (bounds.Left, bounds.Top, bounds.Width, bounds.Height),
                (viewport.Left, viewport.Top, viewport.Width, viewport.Height), scale);
            if (!viewport.Contains(new Point(x, y)) || Row(email).Current.IsOffscreen ||
                !SetCursorPos((int)x, (int)y))
                throw new InvalidOperationException($"The reporting row header is not reachable at {state}: {email}, {bounds}, {viewport}.");
            mouse_event(8, 0, 0, 0, 0);
            mouse_event(16, 0, 0, 0, 0);
        }
        void AssertRetained(TreeItemRealization.MenuRetentionBoundary boundary = TreeItemRealization.MenuRetentionBoundary.Command)
        {
            TreeItemRealization.AssertMenuRetention(boundary, () =>
            {
                if (CurrentPersonEmail(window) != owner || !before.SequenceEqual(Snapshot()) ||
                    !IsVisible(window, "People") || compact && !IsVisible(window, "ClosePeople"))
                    throw new InvalidOperationException($"A tree presentation action changed the profile/writing or dismissed compact People at {state}.");
            }, () =>
            {
                var bounds = Row(managerEmail).Current.BoundingRectangle;
                var viewport = Find(window, "People").Current.BoundingRectangle;
                if (Row(managerEmail).Current.IsOffscreen || bounds.Top < viewport.Top || bounds.Top + 20 > viewport.Bottom)
                    throw new InvalidOperationException($"Collapse subtree lost its target row at {state}.");
            });
        }
        void AssertNoMenu()
        {
            var observation = System.Diagnostics.Stopwatch.StartNew();
            Await(() => observation.ElapsedMilliseconds >= 200);
            if (Menu() is not null)
                throw new InvalidOperationException($"A leaf or fully reset branch opened an empty/no-op tree menu at {state}.");
            AssertRetained();
        }
        void CollapseFromMenu(bool keyboard)
        {
            AutomationElement? menu = null;
            Await(() => (menu = Menu()) is not null);
            var actions = menu!.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem)).Cast<AutomationElement>().ToArray();
            if (actions.Length != 1 || actions[0].Current.Name != "Collapse subtree" || !actions[0].Current.IsEnabled)
                throw new InvalidOperationException($"The tree menu contains a no-op, duplicate or unimplemented action at {state}.");
            if (CurrentPersonEmail(window) != owner || !before.SequenceEqual(Snapshot()))
                throw new InvalidOperationException($"Opening the tree menu navigated or submitted retained writing at {state}.");
            if (keyboard)
            {
                PressCopyKey(0x28);
                PressCopyKey(0x0D);
            }
            else PrimaryClick(() => actions[0]);
            Await(() => Menu() is null && Expansion(managerEmail).Current.ExpandCollapseState == ExpandCollapseState.Collapsed);
            AssertRetained();
        }
        var branch = Expansion(managerEmail);
        void ObserveExpansion(string phase, string boundary, string? input = null)
        {
            try
            {
                var cache = new CacheRequest { TreeScope = TreeScope.Element };
                foreach (var property in new[] { AutomationElement.AutomationIdProperty, AutomationElement.NameProperty,
                    AutomationElement.IsOffscreenProperty, AutomationElement.BoundingRectangleProperty,
                    AutomationElement.HasKeyboardFocusProperty, AutomationElement.IsExpandCollapsePatternAvailableProperty,
                    ExpandCollapsePattern.ExpandCollapseStateProperty })
                    cache.Add(property);
                AutomationElementCollection controls;
                using (cache.Activate())
                    controls = window.FindAll(TreeScope.Descendants, new OrCondition(
                        new[] { "Email", "People", "PeopleNavigation", "OpenPeople", "ProfileEditingState", managerEmail, reportEmail }
                            .Select(id => new PropertyCondition(AutomationElement.AutomationIdProperty, id)).ToArray()));
                var focus = AutomationElement.FocusedElement;
                Console.WriteLine("TREE-MENU-EXPANSION " + JsonSerializer.Serialize(new
                {
                    state, phase, boundary, input, managerEmail, reportEmail,
                    revision = Environment.GetEnvironmentVariable("GITHUB_SHA"),
                    runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
                    runAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
                    monotonicTimestamp = System.Diagnostics.Stopwatch.GetTimestamp(),
                    retainedProviderState = branch.Current.ExpandCollapseState.ToString(),
                    focusedId = focus?.Current.ProcessId == window.Current.ProcessId ? focus.Current.AutomationId : null,
                    controls = controls.Cast<AutomationElement>().Select(control => new
                    {
                        id = control.Cached.AutomationId, name = control.Cached.Name,
                        offscreen = control.Cached.IsOffscreen, focused = control.Cached.HasKeyboardFocus,
                        bounds = control.Cached.BoundingRectangle.ToString(),
                        expansion = (bool)control.GetCachedPropertyValue(AutomationElement.IsExpandCollapsePatternAvailableProperty)
                            ? control.GetCachedPropertyValue(ExpandCollapsePattern.ExpandCollapseStateProperty).ToString() : null
                    }).ToArray(),
                    interpretation = "Non-atomic native observations, not readiness or a confirmed cause. No actions are replayed."
                }));
            }
            catch (Exception error) when (error is ElementNotAvailableException or InvalidOperationException or COMException)
            {
                Console.Error.WriteLine($"TREE-MENU-EXPANSION-DIAGNOSTIC-FAULT state={state}; phase={phase}; boundary={boundary}; {error}");
            }
        }
        void ReexpandForMenu(string phase,
            TreeItemRealization.MenuRetentionBoundary retentionBoundary = TreeItemRealization.MenuRetentionBoundary.Command)
        {
            ObserveExpansion(phase, "before-request");
            try
            {
                var requested = false;
                TreeItemRealization.PrepareMenuBranch(
                    () => Expansion(managerEmail).Current.ExpandCollapseState == ExpandCollapseState.Expanded,
                    input =>
                    {
                        requested = true;
                        if (input == TreeItemRealization.MenuBranchInput.ExpandCollapseProvider)
                        {
                            branch.Expand();
                        }
                        else
                        {
                            var row = Row(managerEmail);
                            var bounds = row.Current.BoundingRectangle;
                            var viewport = Find(window, "People").Current.BoundingRectangle;
                            var scale = DpiScale(window);
                            var point = new Point(bounds.Left + 9 * scale, bounds.Top + 14 * scale);
                            if (row.Current.IsOffscreen || !viewport.Contains(point) ||
                                !SetCursorPos((int)point.X, (int)point.Y))
                                throw new InvalidOperationException($"The reporting disclosure arrow is not reachable at {state}: {managerEmail}, {bounds}, {viewport}.");
                            mouse_event(2, 0, 0, 0, 0);
                            mouse_event(4, 0, 0, 0, 0);
                        }
                        ObserveExpansion(phase, "returned-request", input.ToString());
                    }, Await);
                ObserveExpansion(phase, requested ? "prepared-state" : "already-expanded",
                    requested ? TreeItemRealization.MenuBranchInput.DisclosureArrow.ToString() : null);
                AssertRetained(retentionBoundary);
            }
            catch (Exception error) when (error is TimeoutException or ElementNotAvailableException or InvalidOperationException or COMException)
            {
                ObserveExpansion(phase, "failed-observation");
                CaptureFailureEvidence(window, state + "-" + phase + "-expansion-failure");
                throw;
            }
        }
        ReexpandForMenu("initial");
        Find(window, "SearchQuery").SetFocus();
        Await(() => Find(window, "SearchQuery").Current.HasKeyboardFocus);
        RightClickRow(reportEmail);
        AssertNoMenu();
        RightClickRow(managerEmail);
        CollapseFromMenu(keyboard: false);
        Await(() => Find(window, "SearchQuery").Current.HasKeyboardFocus);
        RightClickRow(managerEmail);
        AssertNoMenu();
        if (owner == reportEmail)
        {
            ReexpandForMenu("report-focus");
            Row(reportEmail).SetFocus();
            Await(() => Row(reportEmail).Current.HasKeyboardFocus);
            RightClickRow(managerEmail);
            CollapseFromMenu(keyboard: false);
            Await(() => Row(managerEmail).Current.HasKeyboardFocus);
            foreach (var appsKey in new[] { true, false })
            {
                ReexpandForMenu(appsKey ? "apps-menu" : "shift-f10-menu");
                if (appsKey) PressCopyKey(0x5D);
                else
                {
                    keybd_event(0x10, 0, 0, 0);
                    try { PressCopyKey(0x79); }
                    finally { keybd_event(0x10, 0, 2, 0); }
                }
                CollapseFromMenu(keyboard: true);
                Await(() => Row(managerEmail).Current.HasKeyboardFocus);
                PressCopyKey(0x5D);
                AssertNoMenu();
            }
        }
        CaptureVisualEvidence(window, state + "-tree-collapse-retained");
        Set(window, "SearchQuery", "tree action unmatched query");
        Await(() => !IsVisible(window, "People"));
        Click(window, "ClearSearch");
        Await(() => IsVisible(window, "People"));
        if (Expansion(managerEmail).Current.ExpandCollapseState != ExpandCollapseState.Collapsed)
            throw new InvalidOperationException($"Search-clear resurrected recursively reset tree expansion at {state}.");
        AssertRetained();
        ReexpandForMenu("restore-fixture", TreeItemRealization.MenuRetentionBoundary.OrdinaryFixtureRestoration);
        if (compact && !panelWasOpen)
        {
            Click(window, "ClosePeople");
            Await(() => !IsVisible(window, "PeopleNavigation"));
        }
    }

    private static void AssertResponsiveProfileForm(AutomationElement window)
    {
        var scale = DpiScale(window);
        Rect Bounds(string id) => Find(window, id).Current.BoundingRectangle;
        void AtSize(string state)
        {
            string? email = null;
            var scroll = ControlReadiness.AwaitPattern(
                () => ControlObservation.Capture(window, "ProfileScroll", "Email"),
                observed =>
                {
                    var control = observed["ProfileScroll"].Cached;
                    return control.IsEnabled && !control.IsOffscreen && !control.BoundingRectangle.IsEmpty &&
                        control.BoundingRectangle.Width > 0 && control.BoundingRectangle.Height > 0 &&
                        observed.IsVisible("Email") && !string.IsNullOrEmpty(email = observed["Email"].Cached.Name);
                }, observed => observed["ProfileScroll"].TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern)
                    ? (ScrollPattern)pattern : null, Await);
            AssertPersonCategory(window, CompactIdentityStateCases.FirstOrDefault(identity =>
                identity.Email == email).Category ?? "Employee");
            if (scroll.Current.HorizontallyScrollable)
                throw new InvalidOperationException("Inline profile editing requires horizontal page scrolling.");
            if (scroll.Current.VerticallyScrollable) scroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
            var controls = ControlObservation.Capture(window, "Email", "SaveProfile", "CancelProfile",
                "ProfileEditingState", "ProfileScroll", "ChoosePhoto", "ClearPhoto");
            var identity = controls.Bounds("Email");
            var save = controls.Bounds("SaveProfile");
            var cancel = controls.Bounds("CancelProfile");
            if (!controls.IsVisible("ProfileEditingState") || !controls.IsVisible("SaveProfile") ||
                !controls.IsVisible("CancelProfile") || identity.Bottom > save.Top ||
                Math.Max(save.Bottom, cancel.Bottom) > controls.Bounds("ProfileScroll").Top + scale)
                throw new InvalidOperationException("The contextual profile lost its anchored identity or explicit actions.");
            foreach (var id in new[] { "ChoosePhoto", "ClearPhoto" })
            {
                var action = controls[id].Cached;
                var bounds = action.BoundingRectangle;
                if (action.IsOffscreen || !action.IsEnabled || bounds.IsEmpty ||
                    bounds.Bottom > controls.Bounds("ProfileScroll").Top + scale)
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
                var field = ControlObservation.Capture(window, id, "ProfileScroll", "Email", "SaveProfile");
                var bounds = field.Bounds(id);
                var viewport = field.Bounds("ProfileScroll");
                if (!field[id].Cached.IsKeyboardFocusable || bounds.Width / scale < 180 ||
                    bounds.Left < viewport.Left - scale || bounds.Right > viewport.Right + scale ||
                    bounds.Top < viewport.Top - scale || bounds.Bottom > viewport.Bottom + scale ||
                    field.Bounds("Email") != identity || field.Bounds("SaveProfile") != save)
                    throw new InvalidOperationException($"Inline profile field {id} is clipped or moves identity/actions at {state}: {bounds}, viewport={viewport}.");
                if (id is not "ManagerChoice") PressCopyKey(0x09);
            }
            if (Bounds("ProfileSummary").Bottom > Bounds("NotesPane").Top + scale ||
                Find(window, "Email").Current.ControlType != ControlType.Text)
                throw new InvalidOperationException("Notes joined the profile draft or stable email became editable.");
            CaptureVisualEvidence(window, state + "-manager");
            AssertReportingTreeMenu(window, "casey@example.com", "avery@example.com", state);
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
            AssertPersonCategory(window, identityState.Category);
            if (identityState.Archived)
            {
                ResizeWindow(window, 1100, 900);
                var archivedHost = Bounds("AdaptiveWorkspace");
                var archivedPeoplePercent = RenderedPeoplePercent(window);
                DragDivider(window, "PeopleDivider", archivedHost.Right - 20);
                Await(() => Math.Abs(Bounds("ProfileScroll").Width / scale - 408) < 2);
                AssertPersonCategory(window, identityState.Category);
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
                CaptureVisualEvidence(window, PersonCaptureState("profile-reading-narrow", identityState.Email));
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
                AtSize(PersonCaptureState("profile-inline-minimum-700-500", identityState.Email));
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
        void AssertRestored(string phase)
        {
            Await(() => Find(window, "Email").Current.Name == "avery@example.com" &&
                Find(window, "PersonNoteHeading").Current.HelpText == note &&
                Value(window, "PersonNoteTitle") == "" && Value(window, "PersonNoteMarkdown") == draft);
            AssertNoteView(window, "Person", editing: true);
            AssertSelectionMatchesProfile(window);
            if (before != Find(window, "ProfileScroll").Current.BoundingRectangle ||
                !Find(window, "PersonNoteStatus").Current.Name.Contains("NOT saved", StringComparison.Ordinal))
            {
                CaptureFailureEvidence(window, phase + "-retained-overlay-failure");
                throw new InvalidOperationException("People browsing changed the viewport or lost the exact failed editor/owner. " +
                    $"{initialMetrics}; {Metrics(phase)}");
            }
        }
        if (Find(window, "Email").Current.Name != "avery@example.com" ||
            Value(window, "PersonNoteTitle") != "" ||
            !Find(window, "PersonNoteStatus").Current.Name.Contains("NOT saved", StringComparison.Ordinal))
            throw new InvalidOperationException("Retained overlay coverage requires Avery's exact failed Person-note editor.");
        EnsurePeopleBrowsing(window);
        var openedMetrics = Metrics("overlay-open");
        AssertRestored("overlay-open");
        SelectTreePerson(window, "casey@example.com", PersonSelection.Mode.Recovery);
        AssertSelectionMatchesProfile(window);
        if (IsVisible(window, "PeopleNavigation") || Find(window, "ProfileHeading").Current.IsOffscreen)
            throw new InvalidOperationException("Successful tree browsing did not dismiss the overlay and expose Casey's actual profile.");
        SelectTreePerson(window, "avery@example.com", PersonSelection.Mode.Recovery);
        AssertRestored("tree-owner-restored");
        EnsurePeopleBrowsing(window);
        if (!IsVisible(window, "PeopleNavigation"))
        {
            CaptureFailureEvidence(window, "reading-retained-overlay-failure");
            throw new InvalidOperationException("The retained Person-note editor lost its usable People overlay after returning to its owner. " +
                $"{initialMetrics}; {openedMetrics}; {Metrics("tree-owner-restored")}");
        }
        AssertRestored("tree-owner-overlay-restored");
        Console.WriteLine($"OVERLAY-OBSERVATION retained-tree: {initialMetrics}; {openedMetrics}; {Metrics("tree-owner-restored")}");
        CaptureVisualEvidence(window, "reading-overlay-exact-draft-retained");
        QueryAndSelect(window, "casey@example", "casey@example.com", "Current", "Profile:", allowPendingLocalWork: true);
        AssertSelectionMatchesProfile(window);
        if (Value(window, "SearchQuery") != "" || IsVisible(window, "PeopleNavigation") ||
            Find(window, "ProfileHeading").Current.IsOffscreen)
            throw new InvalidOperationException("Accepted grouped search did not clear the query and expose Casey's profile.");
        QueryAndSelect(window, "avery@example", "avery@example.com", "Current", "Profile:", allowPendingLocalWork: true,
            retainedScope: OrgExplorer.Workflow.NoteScope.Person);
        AssertRestored("search-owner-restored");
        Click(window, "OpenPeople");
        TypeSearchQuery(window, "avery@example");
        Await(() => IsVisible(window, "PeopleNavigation") && IsVisible(window, "SearchResults"));
        if (Value(window, "SearchQuery") != "avery@example")
        {
            CaptureFailureEvidence(window, "search-retained-overlay-failure");
            throw new InvalidOperationException("The unaccepted grouped query changed while retaining its owner editor. " +
                $"{initialMetrics}; {openedMetrics}; {Metrics("search-owner-restored")}");
        }
        AssertRestored("search-owner-overlay-restored");
        Console.WriteLine($"OVERLAY-OBSERVATION retained-search: {Metrics("search-owner-overlay-restored")}");
        CaptureVisualEvidence(window, "search-overlay-exact-draft-retained");
        Click(window, "ClearSearch");
        Await(() => IsVisible(window, "People") && Value(window, "SearchQuery") == "");
        AssertRestored("query-cleared");
        Click(window, "ClosePeople");
        Await(() => !IsVisible(window, "PeopleNavigation") && Find(window, "OpenPeople").Current.HasKeyboardFocus);
        AssertRestored("overlay-dismissed");
        var state = (WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern);
        state.SetWindowVisualState(WindowVisualState.Maximized);
        try { Await(() => !IsVisible(window, "OpenPeople")); }
        catch (TimeoutException error)
        {
            throw new TimeoutException("Returning to wide reading after retained-draft tree/search browsing " +
                "did not hide the compact People entry within 20 seconds. " +
                $"requestedState=Maximized; actualState={state.Current.WindowVisualState}; canMaximize={state.Current.CanMaximize}; " +
                $"windowPx={window.Current.BoundingRectangle}; hostPx={Find(window, "AdaptiveWorkspace").Current.BoundingRectangle}; " +
                $"viewportPx={Find(window, "ProfileScroll").Current.BoundingRectangle}; " +
                $"owner={Find(window, "Email").Current.Name}; title={Value(window, "PersonNoteTitle")}; " +
                $"status={Status(window)}; {NativeResizeBounds((IntPtr)window.Current.NativeWindowHandle)}", error);
        }
        AssertNoteView(window, "Person", editing: true);
        AssertSelectionMatchesProfile(window);
        if (Find(window, "Email").Current.Name != "avery@example.com" ||
            Find(window, "PersonNoteHeading").Current.HelpText != note ||
            Value(window, "PersonNoteTitle") != "" || Value(window, "PersonNoteMarkdown") != draft)
            throw new InvalidOperationException("Maximizing after retained-draft browsing lost the exact owner/editor/writing.");
    }
}
