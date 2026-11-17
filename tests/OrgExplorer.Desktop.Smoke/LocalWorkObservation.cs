namespace OrgExplorer.Desktop.Smoke;

internal static class LocalWorkObservation
{
    internal sealed record Control(string Id, string Name = "", string Feedback = "", bool Offscreen = false);
    internal sealed record ProfileSaveHold(string OwnerEmail, string Activity, bool WriterHeld);

    internal static void RequireSameHeldProfile(ProfileSaveHold before, ProfileSaveHold after, string expectedOwner)
    {
        if (before.OwnerEmail != expectedOwner || after.OwnerEmail != before.OwnerEmail ||
            before.Activity != "Saving complete profile\u2026" || after.Activity != before.Activity ||
            !before.WriterHeld || !after.WriterHeld)
            throw new InvalidOperationException("Profile navigation must complete while the same owner's save and actual SQLite writer remain held.");
    }

    internal static object HeldProfileNavigationEvidence(IReadOnlyList<double> samplesMs, ProfileSaveHold before,
        ProfileSaveHold after, string expectedOwner, bool exactDestinationReady)
    {
        RequireSameHeldProfile(before, after, expectedOwner);
        if (!exactDestinationReady)
            throw new InvalidOperationException("Held profile navigation did not expose its exact visible usable Person.");
        return new
        {
            boundary = "Native Person Select request through exact visible email and usable heading while a real SQLite writer holds the profile commit.",
            samplesMs, timingUse = "diagnostic",
            before = new { ownerEmail = before.OwnerEmail, activity = before.Activity, writerHeld = before.WriterHeld },
            after = new { ownerEmail = after.OwnerEmail, activity = after.Activity, writerHeld = after.WriterHeld },
            exactDestinationReady,
            criterion = "The exact usable Person must appear while the same owner's profile save and actual SQLite writer remain held and unpublished. This single elapsed sample is diagnostic, not a p95 estimate or per-selection deadline; the ordinary-navigation workload retains p95<=100ms acceptance."
        };
    }

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
