namespace OrgExplorer.Desktop.Smoke;

internal static class LocalWorkObservation
{
    internal sealed record Control(string Id, string Name = "", string Feedback = "", bool Offscreen = false);

    internal static bool IsIdle(Func<string> activity, Func<string> status, Func<string, Control?> find)
    {
        if (activity() != "Ready" || status().StartsWith("Pending", StringComparison.Ordinal)) return false;
        var email = find("Email");
        if (email is null)
        {
            if (Required("WorkspaceStart").Offscreen)
                throw new InvalidOperationException("Startup has neither a Person identity nor a purposeful starting view.");
            return true;
        }
        return email.Name.Length == 0 ||
            !Required("PersonNotesSurface").Feedback.StartsWith("Pending", StringComparison.Ordinal) &&
            !Required("TeamNotesSurface").Feedback.StartsWith("Pending", StringComparison.Ordinal);

        Control Required(string id) => find(id) ?? throw new InvalidOperationException("Missing actual UI control: " + id);
    }

    internal static bool Observe(Func<string> activity, Func<string> status, Func<IReadOnlyList<Control>> snapshot)
    {
        IReadOnlyList<Control>? current = null;
        return IsIdle(activity, status, id => (current ??= snapshot()).FirstOrDefault(control => control.Id == id));
    }
}
