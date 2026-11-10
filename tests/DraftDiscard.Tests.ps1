param([string] $Source = (Join-Path $PSScriptRoot '..\src\OrgExplorer.Desktop\NoteDraftState.cs'))
$ErrorActionPreference = 'Stop'
$root = Join-Path "$PSScriptRoot\..\.test-workspaces" ('draft-discard-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $root
try {
    $probe = Join-Path $root 'DiscardProbe.cs'
    @'
global using System;
global using System.Threading.Tasks;

namespace System.Windows.Threading
{
    // The timer is the only substituted effect; the actual draft coordinator is compiled unchanged.
    public sealed class DispatcherTimer
    {
        public TimeSpan Interval { get; set; }
        public event EventHandler Tick { add { } remove { } }
        public void Start() { }
        public void Stop() { }
    }
}

public static class DraftDiscardProbe
{
    public static async Task Run()
    {
        foreach (var kind in new[] { "Person note", "Team note" })
        {
            var pending = false;
            var message = "";
            var refreshes = 0;
            var discards = 0;
            var reloads = 0;
            var draft = new OrgExplorer.Desktop.NoteDraftState(kind, (_, _) => { },
                () => pending, () => { }, value => message = value, _ => { });
            draft.Edited();
            draft.CompleteSave(false, "title", "writing", "title", "writing");
            pending = true;
            async Task<bool> Refresh()
            {
                refreshes++;
                pending = false;
                await Task.Yield();
                return true;
            }
            void Discard() => discards++;
            void Reload() { reloads++; draft.Reset(); }
            await draft.DiscardAsync(Refresh, Discard, Reload, () => "");
            if (!pending || refreshes != 0 || discards != 0 || reloads != 0 ||
                !draft.HasUnsavedChanges || !message.Contains("Please wait"))
                throw new InvalidOperationException(kind + ": discard overlapped unfinished retry or lost its draft.");
            pending = false;
            await draft.DiscardAsync(Refresh, Discard, Reload, () => "");
            if (refreshes != 1 || discards != 1 || reloads != 1 || draft.HasUnsavedChanges)
                throw new InvalidOperationException(kind + ": explicit idle discard stopped working.");
        }
    }
}
'@ | Set-Content -LiteralPath $probe
    Add-Type -Path @((Resolve-Path -LiteralPath $Source).Path, $probe)
    $null = [DraftDiscardProbe]::Run().GetAwaiter().GetResult()
    Write-Host 'PASS: failed Person/Team drafts refuse discard during a retry without starting refresh or clearing writing; idle discard still works.'
} finally {
    Remove-Item -LiteralPath $root -Recurse -Force
}
