using System.Net.Http;
using OrgExplorer.Workflow.Updates;

namespace OrgExplorer.Desktop;

internal static class UpdateEffects
{
    public static void RecordSafePointRejection(string diagnostic) => System.Diagnostics.Trace.WriteLine(diagnostic);
    public static IReleasePackageTransport Transport(HttpClient http) => new HttpReleasePackageTransport(http);
    public static IUpdateRestart Restart(UpdateRestartJournal journal) => new VelopackUpdateRestart(journal);
}
