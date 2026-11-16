namespace OrgExplorer.Desktop.Smoke;

internal sealed record NoteObservationEvidence(
    string Owner, string Profile, string NoteId, string Title, string Markdown,
    bool Enabled, bool Visible, bool Writable, bool TitleFocused, bool BodyFocused,
    bool EditSelected, bool PatternsAvailable)
{
    internal sealed record Checks(bool Owner, bool Profile, bool NoteIdentity, bool Title, bool Markdown,
        bool Enabled, bool Visible, bool Writable, bool Focused, bool EditSelected, bool PatternsAvailable)
    {
        public bool Correct => Owner && Profile && NoteIdentity && Title && Markdown && Enabled && Visible &&
            Writable && Focused && EditSelected && PatternsAvailable;
    }

    internal Checks Check(NoteObservationEvidence expected) => new(
        Owner == expected.Owner, Profile == expected.Profile, NoteId == expected.NoteId,
        Title == expected.Title, Markdown == expected.Markdown, Enabled, Visible, Writable,
        TitleFocused && !BodyFocused, EditSelected, PatternsAvailable);

    internal static void RequireHostedProbe(string? actions, string? runner, string? job,
        string? revision, string? run, string? attempt)
    {
        if (actions != "true" || runner != "Windows" || job != "uia-observation-probe" ||
            revision is not { Length: 40 } || !revision.All(Uri.IsHexDigit) ||
            !long.TryParse(run, out var runId) || runId <= 0 ||
            !int.TryParse(attempt, out var runAttempt) || runAttempt <= 0)
            throw new InvalidOperationException("This diagnostic requires its explicitly dispatched disposable Windows probe job.");
    }
}
