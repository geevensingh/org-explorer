using System.Windows.Automation;
using Rect = System.Windows.Rect;

namespace OrgExplorer.Desktop.Smoke;

internal static partial class Program
{
    private sealed class ControlObservation
    {
        private readonly Dictionary<string, AutomationElement> controls = new(StringComparer.Ordinal);

        private ControlObservation(AutomationElementCollection elements)
        {
            foreach (AutomationElement element in elements)
                controls.TryAdd(element.Cached.AutomationId, element);
        }

        // Capture again after an action or on the next readiness poll; this is not a window-wide cache.
        internal static ControlObservation Capture(AutomationElement window, params string[] ids)
        {
            using var timing = ObserveUiTiming("control-observation");
            var conditions = ids.Distinct(StringComparer.Ordinal)
                .Select(id => (Condition)new PropertyCondition(AutomationElement.AutomationIdProperty, id)).ToArray();
            if (conditions.Length == 0) throw new ArgumentException("Supply the controls to observe.", nameof(ids));
            var request = new CacheRequest { TreeScope = TreeScope.Element };
            foreach (var property in new[] { AutomationElement.AutomationIdProperty, AutomationElement.NameProperty,
                AutomationElement.ControlTypeProperty, AutomationElement.IsEnabledProperty, AutomationElement.IsOffscreenProperty,
                AutomationElement.IsKeyboardFocusableProperty, AutomationElement.HasKeyboardFocusProperty,
                AutomationElement.BoundingRectangleProperty, AutomationElement.HelpTextProperty })
                request.Add(property);
            using (request.Activate())
                return new(window.FindAll(TreeScope.Descendants,
                    conditions.Length == 1 ? conditions[0] : new OrCondition(conditions)));
        }

        internal AutomationElement this[string id] => controls.TryGetValue(id, out var control)
            ? control : throw new InvalidOperationException("Missing actual UI control: " + id);

        internal bool IsVisible(string id) =>
            controls.TryGetValue(id, out var control) && !control.Cached.IsOffscreen;

        internal Rect Bounds(string id) => this[id].Cached.BoundingRectangle;
    }
}
