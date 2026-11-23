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
    bool SaveProfileEdit(ProfileEditSession edit);
    void SelectProfilePerson(string email);
    bool TryLeaveProfileEdit(ProfileLeaveDecision decision);
    void CancelProfileEdit();
    void DiscardPersonChanges(string email);
}

public sealed partial class ApplicationWorkflow
{
    private ProfileEditSession? profileEdit;
    private readonly object profileDraftGate = new();
    private readonly Dictionary<string, ProfileEditSession> localProfileEdits = new(StringComparer.OrdinalIgnoreCase);
    private int localProfileRequests;
    private object ProfileDraftGate => profileDraftGate;
    public ProfileEditSession? ProfileEdit => Volatile.Read(ref profileEdit);
    private bool HasUnsavedProfiles
    {
        get
        {
            lock (profileDraftGate) return Volatile.Read(ref localProfileRequests) != 0 ||
                localProfileEdits.Values.Any(edit => edit.IsDirty);
        }
    }

    public void SelectProfilePerson(string email)
    {
        lock (profileDraftGate) profileEdit = localProfileEdits.GetValueOrDefault(email);
    }

    private void SetProfileEdit(ProfileEditSession edit)
    {
        profileEdit = edit;
        localProfileEdits[edit.Basis.Email] = edit;
    }

    public bool BeginProfileEdit(string email)
    {
        lock (ProfileDraftGate)
        {
            if (!AllowLocalDraftEdit()) return false;
            if (!workspaceCurrent || GetRelationships(email)?.Person is not { } person) return Fail("Open a current Person first.");
            SetProfileEdit(localProfileEdits.TryGetValue(email, out var retained) ? retained : new(person, person));
            Status = "Editing profile. Changes are staged until Save; Cancel discards this profile draft only.";
            return true;
        }
    }

    public bool StageProfileText(PersonTextField field, string? value)
    {
        lock (ProfileDraftGate)
        {
            if (!AllowLocalDraftEdit()) return false;
            if (profileEdit is null) return Fail("Choose Edit profile first.");
            if (!Enum.IsDefined(field) || field == PersonTextField.PreferredName)
                return Fail("Choose an ordinary profile field. Preferred name saves independently.");
            SetProfileEdit(profileEdit with { Draft = WithText(profileEdit.Draft, field, value) });
            Status = "Unsaved profile draft. Save or Cancel.";
            return true;
        }
    }

    public bool StageProfileManager(string? managerEmail)
    {
        lock (ProfileDraftGate)
        {
            if (!AllowLocalDraftEdit()) return false;
            if (profileEdit is null) return Fail("Choose Edit profile first.");
            SetProfileEdit(profileEdit with { Draft = profileEdit.Draft with { ManagerEmail = managerEmail } });
            Status = "Manager staged in the unsaved profile draft.";
            return true;
        }
    }

    public bool StageProfilePhoto(string? localPath)
    {
        lock (ProfileDraftGate)
        {
            if (!AllowLocalDraftEdit()) return false;
            if (profileEdit is null) return Fail("Choose Edit profile first.");
            SetProfileEdit(profileEdit with { PhotoChanged = true, PhotoLocalPath = localPath });
            Status = "Photo staged in the unsaved profile draft. Save validates the local image; Cancel retains the saved photo.";
            return true;
        }
    }

    public bool TryLeaveProfileEdit(ProfileLeaveDecision decision)
    {
        lock (workspaceGate)
        {
            return ProfileEdit is not { } edit || TryLeaveProfileEdit(edit.Basis.Email, decision);
        }
    }

    private bool TryLeaveProfileEdit(string owner, ProfileLeaveDecision decision)
    {
        ProfileEditSession edit;
        lock (profileDraftGate)
        {
            if (!localProfileEdits.TryGetValue(owner, out var retained)) return true;
            edit = retained;
            if (!Enum.IsDefined(decision)) return Fail("Choose Save, Discard or Stay.");
            if (!edit.IsDirty || decision == ProfileLeaveDecision.Discard)
            {
                if (Volatile.Read(ref localProfileRequests) != 0)
                    return Fail("Cancel cannot revoke an admitted profile write. Wait for its reconciled outcome; browsing remains available.");
                CancelProfileEditCore(owner);
                return true;
            }
        }
        return decision == ProfileLeaveDecision.Stay
            ? Fail("Stayed on the unsaved profile draft.") : SaveProfileEdit(edit);
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
        lock (profileDraftGate)
        {
            if (Volatile.Read(ref localProfileRequests) != 0)
            {
                Fail("Cancel cannot revoke an admitted profile write. Wait for its reconciled outcome; browsing remains available.");
                return;
            }
            if (profileEdit is { } edit) CancelProfileEditCore(edit.Basis.Email);
        }
    }

    private void CancelProfileEditCore(string owner)
    {
        localProfileEdits.Remove(owner);
        if (string.Equals(profileEdit?.Basis.Email, owner, StringComparison.OrdinalIgnoreCase)) profileEdit = null;
        failedSaves.Remove($"profile-session:{owner.ToLowerInvariant()}");
        Status = "Profile draft discarded. Independently saved personal context is retained.";
    }

    public bool SaveProfileEdit() => ProfileEdit is { } edit
        ? SaveProfileEdit(edit) : Fail("Choose Edit profile first.");

    public bool SaveProfileEdit(ProfileEditSession edit)
    {
        if (!CanBeginLocalSave()) return false;
        Interlocked.Increment(ref localProfileRequests);
        try { return TrackLocalSave(() => SaveProfileEditCore(edit)); }
        finally { Interlocked.Decrement(ref localProfileRequests); }
    }

    private bool SaveProfileEditCore(ProfileEditSession edit)
    {
        lock (workspaceGate)
        {
            var operation = $"profile-session:{edit.Basis.Email.ToLowerInvariant()}";
            lock (profileDraftGate)
                if (!localProfileEdits.TryGetValue(edit.Basis.Email, out var retained) || retained.Basis != edit.Basis)
                    return SaveFailed(operation, "Profile NOT saved: the submitted draft was cancelled or already superseded.");
            try
            {
                if (!RefreshWorkspaceCore(onlyIfRevisionChanged: true))
                    return SaveFailed(operation, "Profile NOT saved. Restore valid local delivery and retry. " + Status);
                if (!people.TryGetValue(edit.Basis.Email, out var current) || current != edit.Basis)
                    return SaveFailed(operation,
                        "Profile changed externally after Edit profile began; changes are NOT saved locally. " +
                        "The draft is retained. Cancel/Discard it before editing the delivered profile; retry cannot overwrite it.");
                var next = edit.Draft with { PreferredName = current.PreferredName, IsFavorite = current.IsFavorite };
                if (next.TeamName != current.TeamName && !CanEditTeamName(current.Email))
                    return SaveFailed(operation,
                        "Team name NOT saved: the owner must be active with a current direct report. " +
                        "The complete profile draft is retained. Restore eligibility and retry, or Cancel.");
                next = next with { TeamName = Text(JsonSerializer.SerializeToElement(next.TeamName), $"{next.Email}.teamName") };
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
                if (!Save(proposed, batches, proposedPhotos, history, operation,
                    resource is null ? null : [resource])) return false;
                people = proposed;
                photos = proposedPhotos;
                profileHistory = history;
                InvalidateProjections();
                pending = null;
                CompleteLocalProfileEdit(edit, next);
                bool remainingDrafts;
                lock (ProfileDraftGate)
                    remainingDrafts = localProfileEdits.Values.Any(draft => draft.IsDirty);
                Status = remainingDrafts ? "Submitted profile saved locally. Newer or other Person drafts remain unsaved." :
                    "Profile saved locally.";
                return true;
            }
            catch (Exception ex) when (StorageError(ex))
            {
                return SaveFailed(operation, "Profile NOT saved. Draft retained; correct the problem and retry, or Cancel. " + ex.Message);
            }

        }
    }

    private void CompleteLocalProfileEdit(ProfileEditSession submitted, PersonProfile saved)
    {
        lock (profileDraftGate)
        {
            if (!localProfileEdits.TryGetValue(saved.Email, out var latest)) return;
            ProfileEditSession? remaining = ReferenceEquals(latest, submitted) ? null : latest with
            {
                Basis = saved,
                Draft = latest.Draft with { PreferredName = saved.PreferredName, IsFavorite = saved.IsFavorite, PhotoPath = saved.PhotoPath },
                PhotoChanged = latest.PhotoChanged && (latest.PhotoLocalPath != submitted.PhotoLocalPath ||
                    latest.PhotoChanged != submitted.PhotoChanged)
            };
            if (remaining is null) localProfileEdits.Remove(saved.Email);
            else localProfileEdits[saved.Email] = remaining;
            if (ReferenceEquals(profileEdit, latest)) profileEdit = remaining;
        }
    }

    private void AcceptIndependentProfileChange(PersonProfile before, PersonProfile after)
    {
        lock (profileDraftGate)
        {
            if (localProfileEdits.TryGetValue(before.Email, out var retained) && retained.Basis == before)
            {
                var updated = retained with
                {
                    Basis = after,
                    Draft = retained.Draft with { PreferredName = after.PreferredName, IsFavorite = after.IsFavorite }
                };
                localProfileEdits[before.Email] = updated;
                if (ReferenceEquals(profileEdit, retained)) profileEdit = updated;
            }
        }
    }
}

public sealed partial class WorkspaceViewModel
{
    public ProfileEditSession? ProfileEdit => workflow.ProfileEdit;
    public bool BeginProfileEdit(string email) => AllowLocalDraftEdit() && Observe(() => workflow.BeginProfileEdit(email));
    public bool StageProfileText(PersonTextField field, string? value) => AllowLocalDraftEdit() && Observe(() => workflow.StageProfileText(field, value));
    public bool StageProfileManager(string? managerEmail) => AllowLocalDraftEdit() && Observe(() => workflow.StageProfileManager(managerEmail));
    public bool StageProfilePhoto(string? localPath) => AllowLocalDraftEdit() && Observe(() => workflow.StageProfilePhoto(localPath));
    public bool SaveProfileEdit() => CanSubmitLocalSave() && ObserveLocalSave(workflow.SaveProfileEdit);
    public bool SaveProfileEdit(ProfileEditSession edit) => CanSubmitLocalSave() && ObserveLocalSave(() => workflow.SaveProfileEdit(edit));
    public void SelectProfilePerson(string email) => Observe(() => { workflow.SelectProfilePerson(email); return true; });
    public bool TryLeaveProfileEdit(ProfileLeaveDecision decision) => Observe(() => workflow.TryLeaveProfileEdit(decision));
    public void CancelProfileEdit() => Observe(() => { workflow.CancelProfileEdit(); return true; });
    public void DiscardPersonChanges(string email) => Observe(() => { workflow.DiscardPersonChanges(email); return true; });
}
