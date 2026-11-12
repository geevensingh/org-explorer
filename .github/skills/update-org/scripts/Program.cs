using System.Text.Json;
using OrgExplorer.OrgExport;

try
{
    if (!OperatingSystem.IsWindows())
        throw new PlatformNotSupportedException("Run this producer in a local Windows session.");
    if (args.Length == 0)
        throw new ArgumentException("Expected init, capture-start, capture, ingest, plan, profile, cache, photo, or publish. See collection.md.");
    object result;
    if (args[0] == "init" && args.Length == 4)
        result = ExportSession.Create(args[1], args[2], args[3]);
    else
    {
        if (args.Length < 2) throw new ArgumentException("A staging run path is required.");
        using var session = ExportSession.Open(args[1], publishing: args[0] == "publish");
        result = args[0] switch
        {
            "capture-start" when args.Length == 4 => session.BeginCapture(args[2], args[3]),
            "capture" when args.Length == 3 => session.Capture(args[2]),
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
    return 0;
}
catch (Exception error) when (error is ArgumentException or InvalidOperationException
    or IOException or UnauthorizedAccessException or JsonException or NotSupportedException or FormatException)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { error = error.Message }));
    return 1;
}
