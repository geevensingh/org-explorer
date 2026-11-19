using System.Text.Json;
using OrgExplorer.OrgExport;

try
{
    if (!OperatingSystem.IsWindows())
        throw new PlatformNotSupportedException("Run this producer in a local Windows session.");
    if (args.Length == 0)
        throw new ArgumentException("Expected init, capture-start, capture, a source-* command, ingest, plan, profile, cache, photo, or publish. See collection.md.");
    object result;
    if (args[0] == "init" && args.Length == 4)
        result = ExportSession.Create(args[1], args[2], args[3]);
    else
    {
        if (args.Length < 2) throw new ArgumentException("A staging run path is required.");
        using var session = ExportSession.Open(args[1], publishing: args[0] == "publish");
        result = args[0] switch
        {
            "source-status" when args.Length == 2 => session.SourceStatus(),
            "source-claim" when args.Length == 5 => session.ClaimSource(args[2], args[3], args[4]),
            "source-finish" when args.Length == 4 => session.FinishSource(Guid.Parse(args[2]), args[3]),
            "source-recover" when args.Length == 2 => session.BeginSourceRecovery(),
            "source-refresh-manager" when args.Length == 4 => session.RefreshManagerConfirmation(args[2], args[3]),
            "source-authorize-best-effort" when args.Length == 3 =>
                new { bestEffortEnrichment = session.AuthorizeBestEffortEnrichment(args[2]) },
            "source-authorize-per-read-recovery" when args.Length == 3 =>
                new { perReadRecovery = session.AuthorizePerReadSourceRecovery(args[2]) },
            "source-authorize-unclassified-recovery" when args.Length == 5 =>
                new { unclassifiedRecovery = session.AuthorizeUnclassifiedSourceRecovery(args[2], args[3], args[4]) },
            "source-authorize-matching-error-recovery" when args.Length == 5 =>
                new { matchingErrorRecovery = session.AuthorizeMatchingErrorSourceRecovery(args[2], args[3], args[4]) },
            "source-authorize-not-found-recovery" when args.Length == 5 =>
                new { notFoundRecovery = session.AuthorizeNotFoundSourceRecovery(args[2], args[3], args[4]) },
            "source-omit-photo" when args.Length == 4 =>
                new { photoOmission = session.AuthorizePhotoOmission(args[2], args[3]) },
            "source-interrupt" when args.Length == 4 => session.InterruptSource(Guid.Parse(args[2]), args[3]),
            "capture-start" when args.Length == 4 => session.BeginCapture(args[2], args[3]),
            "capture" when args.Length == 3 => session.Capture(args[2]),
            "capture-fail" when args.Length == 5 => session.FailCapture(Guid.Parse(args[2]), args[3], args[4]),
            "ingest" when args.Length == 4 => session.Ingest(args[2], args[3]),
            "plan" when args.Length == 2 => session.Jobs(),
            "profile" when args.Length == 4 => session.Profile(args[2], args[3]),
            "profiles" when args.Length == 3 => session.Profiles(args[2]),
            "cache" when args.Length == 3 => session.ReusePhoto(args[2]),
            "cached-photos" when args.Length == 3 => session.CachedPhotos(args[2]),
            "photo" when args.Length == 4 => session.Photo(args[2], args[3]),
            "photos" when args.Length == 3 => session.Photos(args[2]),
            "publish" when args.Length == 2 => session.Publish(),
            _ => throw new ArgumentException("Invalid command or arguments. See collection.md.")
        };
    }
    Console.WriteLine(JsonSerializer.Serialize(result, ExportSession.Json));
    return result is ExportSession.ProfileBatchResult { Failed: > 0 } ? 1 : 0;
}
catch (Exception error) when (error is ArgumentException or InvalidOperationException
    or IOException or UnauthorizedAccessException or JsonException or NotSupportedException or FormatException)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { error = error.Message }));
    return 1;
}
