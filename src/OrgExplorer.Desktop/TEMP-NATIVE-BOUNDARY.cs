// TEMP-NATIVE-BOUNDARY: observational diagnostic patch only; NEVER shipping ancestry.
using System.IO;
using System.Windows;
using OrgExplorer.TempNativeBoundary;
using System.Diagnostics;

namespace OrgExplorer.Desktop;

public partial class MainWindow
{
    private TempBoundaryObserver? tempNativeBoundary;

    private void InitializeTempNativeBoundary()
    {
        var name = Environment.GetEnvironmentVariable("TEMP_NATIVE_BOUNDARY_MAP");
        if (string.IsNullOrEmpty(name)) return;
        try
        {
            var map = BoundaryMap.OpenApplication(name, BoundaryClock.Current);
            tempNativeBoundary = new(map, () => selectedEmail, () =>
                PersonIdentity.Visibility == Visibility.Visible && PersonBody.Visibility == Visibility.Visible &&
                PersonBody.IsMeasureValid && PersonBody.IsArrangeValid &&
                PersonScroll.IsMeasureValid && PersonScroll.IsArrangeValid &&
                string.Equals(Email.Text, selectedEmail, StringComparison.OrdinalIgnoreCase));
            PersonBody.LayoutUpdated += tempNativeBoundary.LayoutUpdated;
            Closed += (_, _) =>
            {
                PersonBody.LayoutUpdated -= tempNativeBoundary.LayoutUpdated;
                tempNativeBoundary.Dispose();
            };
        }
        catch (Exception error) when (TempBoundaryObserver.Expected(error))
        {
            TempBoundaryObserver.Report(error);
        }
    }

    private sealed class TempBoundaryObserver(BoundaryMap map, Func<string?> selected, Func<bool> validLayout) : IDisposable
    {
        private BoundarySpan handler, pending;
        private bool exited, failed;

        internal static bool Expected(Exception error) => error is IOException or UnauthorizedAccessException or
            ObjectDisposedException or ArgumentException or NotSupportedException;

        internal static void Report(Exception error) =>
            Console.Error.WriteLine($"TEMP-NATIVE-BOUNDARY diagnosticFailure=true type={error.GetType().Name} hresult={error.HResult}");

        private void Fail(Exception error)
        {
            if (failed) return;
            failed = true;
            try { map.Fault(BoundaryFault.Mapping); }
            catch (Exception secondary) when (Expected(secondary)) { Report(secondary); }
            Report(error);
        }

        internal BoundarySpan BeginHandler(string? identity, int action, bool ignored)
        {
            var entered = Stopwatch.GetTimestamp();
            if (failed) return default;
            try
            {
                if (ignored) { map.Record(default, BoundaryEvent.Unrelated, flags: 1); return default; }
                if (handler.Valid) { map.Fault(BoundaryFault.Overlap); return default; }
                var next = map.BeginHandler(identity, action, entered);
                if (!next.Valid) return default;
                if (pending.Valid) map.Layout(pending, selected(), valid: true, superseded: true);
                handler = pending = next;
                exited = false;
                return next;
            }
            catch (Exception error) when (Expected(error)) { Fail(error); return default; }
        }

        internal void EndHandler(BoundarySpan captured)
        {
            var exitedAt = Stopwatch.GetTimestamp();
            if (failed || !captured.Valid) return;
            try
            {
                map.End(captured, exitedAt);
                if (handler.Id == captured.Id) handler = default;
                if (pending.Id == captured.Id) exited = true;
            }
            catch (Exception error) when (Expected(error)) { Fail(error); }
        }

        internal BoundarySpan BeginDispatch(string identity, bool navigation)
        {
            var entered = Stopwatch.GetTimestamp();
            if (failed) return default;
            try
            {
                if (!handler.Valid && pending.Valid)
                {
                    map.Layout(pending, selected(), valid: true, superseded: true);
                    pending = default;
                }
                return map.BeginDispatch(handler, identity, navigation, entered);
            }
            catch (Exception error) when (Expected(error)) { Fail(error); return default; }
        }

        internal void EndDispatch(BoundarySpan captured)
        {
            var exited = Stopwatch.GetTimestamp();
            if (failed || !captured.Valid) return;
            try { map.End(captured, exited); }
            catch (Exception error) when (Expected(error)) { Fail(error); }
        }

        internal void LayoutUpdated(object? sender, EventArgs e)
        {
            if (failed || !pending.Valid || !exited) return;
            try
            {
                if (map.Identity(selected()) != pending.Request.Target)
                {
                    map.Layout(pending, selected(), valid: true, superseded: true);
                    pending = default;
                }
                else if (validLayout())
                {
                    map.Layout(pending, selected(), valid: true, superseded: false);
                    pending = default;
                }
            }
            catch (Exception error) when (Expected(error)) { Fail(error); }
        }

        public void Dispose()
        {
            try { map.Dispose(); }
            catch (Exception error) when (Expected(error)) { Report(error); }
        }
    }
}
