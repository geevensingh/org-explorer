using System.Text.Json;

namespace OrgExplorer.Workflow;

public enum ProfileLeaveDecision { Save, Discard, Stay }

public sealed record ProfileEditSession(PersonProfile Basis, PersonProfile Draft,
    bool PhotoChanged = false, string? PhotoLocalPath = null)
{
    public bool IsDirty => Basis != Draft || PhotoChanged && (PhotoLocalPath is not null || Basis.PhotoPath is not null);
}

public partial interface IApplicationWorkflow
{
    ProfileEditSession? ProfileEdit { get; }
    bool BeginProfileEdit(string email);
    bool StageProfileText(PersonTextField field, string? value);
    bool StageProfileManager(string? managerEmail);
    bool StageProfilePhoto(string? localPath);
    bool SaveProfileEdit();
    bool TryLeaveProfileEdit(ProfileLeaveDecision decision);
    void CancelProfileEdit();
    void DiscardPersonChanges(string email);
}

public sealed partial class ApplicationWorkflow
{
    private ProfileEditSession? profileEdit;
    public ProfileEditSession? ProfileEdit => Volatile.Read(ref profileEdit);

    public bool BeginProfileEdit(string email)
    {
        lock (workspaceGate)
        {
            if (profileEdit is { IsDirty: true }) return Fail("Unsaved profile: Save, Discard or Stay before leaving.");
            if (!workspaceCurrent || !people.TryGetValue(email, out var person)) return Fail("Open a current Person first.");
            profileEdit = new(person, person);
            Status = "Editing profile. Changes are staged until Save; Cancel discards this profile draft only.";
            return true;
        }
    }

    public bool StageProfileText(PersonTextField field, string? value)
    {
        lock (workspaceGate)
        {
            if (profileEdit is null) return Fail("Choose Edit profile first.");
            if (!Enum.IsDefined(field) || field == PersonTextField.PreferredName)
                return Fail("Choose an ordinary profile field. Preferred name saves independently.");
            profileEdit = profileEdit with { Draft = WithText(profileEdit.Draft, field, value) };
            Status = "Unsaved profile draft. Save or Cancel.";
            return true;
        }
    }

    public bool StageProfileManager(string? managerEmail)
    {
        lock (workspaceGate)
        {
            if (profileEdit is null) return Fail("Choose Edit profile first.");
            profileEdit = profileEdit with { Draft = profileEdit.Draft with { ManagerEmail = managerEmail } };
            Status = "Manager staged in the unsaved profile draft.";
            return true;
        }
    }

    public bool StageProfilePhoto(string? localPath)
    {
        lock (workspaceGate)
        {
            if (profileEdit is null) return Fail("Choose Edit profile first.");
            profileEdit = profileEdit with { PhotoChanged = true, PhotoLocalPath = localPath };
            Status = "Photo staged in the unsaved profile draft. Save validates the local image; Cancel retains the saved photo.";
            return true;
        }
    }

    public bool TryLeaveProfileEdit(ProfileLeaveDecision decision)
    {
        lock (workspaceGate)
        {
            if (profileEdit is null) return true;
            if (!Enum.IsDefined(decision)) return Fail("Choose Save, Discard or Stay.");
            if (profileEdit.IsDirty)
            {
                if (decision == ProfileLeaveDecision.Stay) return Fail("Stayed on the unsaved profile draft.");
                if (decision == ProfileLeaveDecision.Save) return SaveProfileEdit();
            }
            CancelProfileEdit();
            return true;
        }
    }

    public void DiscardPersonChanges(string email)
    {
        lock (workspaceGate)
        {
            foreach (var key in failedSaves.Keys.Where(key =>
                key.StartsWith($"profile:{email.ToLowerInvariant()}:", StringComparison.Ordinal) ||
                key == $"photo:{email.ToLowerInvariant()}").ToArray())
                failedSaves.Remove(key);
            Status = "Unsaved Person quick changes discarded. Note drafts are retained.";
        }
    }

    public void CancelProfileEdit()
    {
        lock (workspaceGate)
        {
            if (profileEdit is null) return;
            profileEdit = null;
            failedSaves.Remove("profile-session");
            Status = "Profile draft discarded. Independently saved personal context is retained.";
        }
    }

    public bool SaveProfileEdit()
    {
        lock (workspaceGate)
        {
            if (profileEdit is not { } edit) return Fail("Choose Edit profile first.");
            try
            {
                using var access = memory ? null : CoordinateWorkspaceIo(workspace!);
                if (!RefreshWorkspaceCore())
                    return SaveFailed("profile-session", "Profile NOT saved. Restore valid local delivery and retry. " + Status);
                if (!people.TryGetValue(edit.Basis.Email, out var current) || current != edit.Basis)
                    return SaveFailed("profile-session",
                        "Profile changed externally after Edit profile began; changes are NOT saved locally. " +
                        "The draft is retained. Cancel/Discard it before editing the delivered profile; retry cannot overwrite it.");
                var next = edit.Draft with { PreferredName = current.PreferredName, IsFavorite = current.IsFavorite };
                foreach (var (field, property, _) in TextFields.Where(f => f.Field != PersonTextField.PreferredName))
                    next = WithText(next, field, ValidateText(JsonSerializer.SerializeToElement(ReadText(next, field)),
                        field, $"{next.Email}.{property}"));
                if (next.ManagerEmail is not null)
                    Email(JsonSerializer.SerializeToElement(next.ManagerEmail), $"{next.Email}.managerEmail");
                var proposedPhotos = new Dictionary<string, byte[]>(photos);
                PhotoResource? resource = null;
                if (edit.PhotoChanged)
                {
                    if (edit.PhotoLocalPath is { } path)
                    {
                        resource = ReadLocalPhoto(path, next.Email);
                        var name = resource.Hash.ToLowerInvariant() + resource.Extension;
                        proposedPhotos[name] = resource.Bytes;
                        next = next with { PhotoPath = name };
                    }
                    else next = next with { PhotoPath = null };
                }
                var proposed = new Dictionary<string, PersonProfile>(people, StringComparer.OrdinalIgnoreCase)
                { [next.Email] = next };
                ValidateGraph(proposed);
                var history = new Dictionary<string, PersonHistory>(profileHistory, StringComparer.OrdinalIgnoreCase);
                var recordedAt = timeProvider.GetUtcNow();
                foreach (var field in TextFields.Where(f => f.Field != PersonTextField.PreferredName)
                    .Select(f => f.Property).Concat(["managerEmail", "photoPath"]))
                {
                    var oldValue = ProfileValue(current, field);
                    var newValue = ProfileValue(next, field);
                    if (!SameProfileValue(field, oldValue, newValue))
                        AddEvidence(history, next.Email, oldValue, new FieldEvidence(field, newValue,
                            ProfileChangeSource.LocalEdit, null, recordedAt, null, null), true);
                }
                if (!Save(proposed, batches, proposedPhotos, history, "profile-session",
                    resource is null ? null : [resource])) return false;
                people = proposed;
                photos = proposedPhotos;
                profileHistory = history;
                InvalidateProjections();
                pending = null;
                profileEdit = null;
                Status = "Profile saved locally.";
                return true;
            }
            catch (Exception ex) when (StorageError(ex))
            {
                return SaveFailed("profile-session", "Profile NOT saved. Draft retained; correct the problem and retry, or Cancel. " + ex.Message);
            }
        }
    }

    private void AcceptIndependentProfileChange(PersonProfile before, PersonProfile after)
    {
        if (profileEdit is { } edit && edit.Basis == before)
            profileEdit = edit with
            {
                Basis = after,
                Draft = edit.Draft with { PreferredName = after.PreferredName, IsFavorite = after.IsFavorite }
            };
    }
}

public sealed partial class WorkspaceViewModel
{
    public ProfileEditSession? ProfileEdit => workflow.ProfileEdit;
    public bool BeginProfileEdit(string email) => Observe(() => workflow.BeginProfileEdit(email));
    public bool StageProfileText(PersonTextField field, string? value) => Observe(() => workflow.StageProfileText(field, value));
    public bool StageProfileManager(string? managerEmail) => Observe(() => workflow.StageProfileManager(managerEmail));
    public bool StageProfilePhoto(string? localPath) => Observe(() => workflow.StageProfilePhoto(localPath));
    public bool SaveProfileEdit() => Observe(workflow.SaveProfileEdit);
    public bool TryLeaveProfileEdit(ProfileLeaveDecision decision) => Observe(() => workflow.TryLeaveProfileEdit(decision));
    public void CancelProfileEdit() => Observe(() => { workflow.CancelProfileEdit(); return true; });
    public void DiscardPersonChanges(string email) => Observe(() => { workflow.DiscardPersonChanges(email); return true; });
}
