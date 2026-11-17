using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace OrgExplorer.Desktop.Smoke;

internal static class LocalWorkObservation
{
    internal sealed record Control(string Id, string Name = "", string Feedback = "", bool Offscreen = false);
    internal sealed record ProfileSaveHold(string OwnerEmail, string Activity, bool WriterHeld, string Status = "");
    internal sealed record OwnedProcessIdentity(int ProcessId, string ExecutableSha256);

    internal static void WithOwnedProcessEvidence(Process owned, Action action,
        Action<OwnedProcessIdentity, Exception?> write)
    {
        var executablePath = owned.MainModule?.FileName
            ?? throw new InvalidOperationException("The owned executable must be identified before held-profile work starts.");
        OwnedProcessIdentity identity;
        using (var executable = File.OpenRead(executablePath))
            identity = new(owned.Id, Convert.ToHexString(SHA256.HashData(executable)));
        Exception? primary = null;
        try { action(); }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            try
            {
                write(identity, primary);
            }
            catch (Exception error) when (primary is not null && error is IOException or UnauthorizedAccessException or
                InvalidOperationException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
            {
                Console.Error.WriteLine("HELD-PROFILE-EVIDENCE-FAULT " + error.GetType().FullName +
                    "; original failure remains " + primary.GetType().FullName + ".");
            }
        }
    }

    internal static void RequireSameHeldProfile(ProfileSaveHold before, ProfileSaveHold after, string expectedOwner)
    {
        if (before.OwnerEmail != expectedOwner || after.OwnerEmail != before.OwnerEmail ||
            !before.WriterHeld || !after.WriterHeld)
            throw new InvalidOperationException("Profile navigation lost its submitted owner or physical SQLite hold: " +
                $"expectedOwner={expectedOwner}; before={before}; after={after}.");
    }

    internal static object HeldProfileNavigationEvidence(IReadOnlyList<double> samplesMs, ProfileSaveHold before,
        ProfileSaveHold after, string expectedOwner, bool exactDestinationReady,
        bool originalStateDuringHold, bool committedStateAfterRelease)
    {
        RequireSameHeldProfile(before, after, expectedOwner);
        if (!exactDestinationReady || !originalStateDuringHold || !committedStateAfterRelease)
            throw new InvalidOperationException("Held profile navigation did not prove its exact destination and storage transition: " +
                $"destinationReady={exactDestinationReady}; originalStateDuringHold={originalStateDuringHold}; " +
                $"committedStateAfterRelease={committedStateAfterRelease}; before={before}; after={after}.");
        return new
        {
            boundary = "Native Person Select request through exact visible email and usable heading while a real SQLite writer holds the profile commit.",
            samplesMs, timingUse = "diagnostic",
            before = new { ownerEmail = before.OwnerEmail, activity = before.Activity, writerHeld = before.WriterHeld, status = before.Status },
            after = new { ownerEmail = after.OwnerEmail, activity = after.Activity, writerHeld = after.WriterHeld, status = after.Status },
            exactDestinationReady, originalStateDuringHold, committedStateAfterRelease,
            criterion = "The original single Save request must retain its submitted owner's exact old state during physical hold and commit the exact intended state after release; the exact usable destination must appear before release. UI activity/status are diagnostic presentation, not save identity. This single elapsed sample is diagnostic, not a p95 estimate or per-selection deadline; ordinary navigation retains p95<=100ms acceptance."
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
