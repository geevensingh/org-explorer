using System.Security.Cryptography;
using System.Text.Json;
using OrgExplorer.Workflow;

namespace OrgExplorer.TestFixtures;

internal static class WorkspacePublicState
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true
    };

    internal static string Hash(IApplicationWorkflow app)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(app.FavoritePeople.Select(person => person.Email).ToArray(), Json));
        foreach (var person in app.People.OrderBy(person => person.Email, StringComparer.Ordinal))
        {
            var photo = app.GetPhoto(person.Email);
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(new
            {
                person, personNotes = app.GetPersonNotes(person.Email, true), teamNotes = app.GetTeamNotes(person.Email, true),
                inherited = app.GetInheritedTeamNotes(person.Email), history = app.GetProfileTimeline(person.Email),
                frozen = app.GetHistoricalTeamContexts(person.Email),
                photo = photo is null ? null : new
                {
                    photo.Width, photo.Height, sha256 = Convert.ToHexString(SHA256.HashData(photo.Bytes.Span))
                }
            }, Json));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
