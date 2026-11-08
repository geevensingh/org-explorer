# Project-key update authentication

Authenticate unattended Velopack updates with project-key signed manifests and
trusted verification keys embedded in the installed app, rather than making paid
or externally approved Windows code signing a v1 release prerequisite. This
preserves fully automatic product releases without certificate subscription fees,
but requires explicit signature/asset binding and secure release-key handling
beyond Velopack's feed hash checks. It authenticates updates to an already-trusted
app, not a publicly verified legal publisher or the initial installer's Windows
reputation; public-trust Windows signing is deferred, not simulated.
