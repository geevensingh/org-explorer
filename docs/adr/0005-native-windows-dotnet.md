# Native Windows UI on .NET

Use .NET 10 and WPF for supported Windows 11 x64 versions, rather than committing
to a Tauri or Electron web UI for the initial app. This favors a native Windows
experience and straightforward local-file handling within an already available
.NET toolchain. A future non-Windows app will need a different UI; portable
workspace data is preserved without claiming the WPF UI is portable.
