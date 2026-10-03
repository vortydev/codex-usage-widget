# Codex Usage Widget

A small Windows window for ChatGPT-plan usage limits reported by the local Codex App Server. It uses the standard Windows caption controls and rounded usage cards and progress tracks.

## Build

Requires Windows and the .NET 8 SDK (or a newer compatible SDK) available on `PATH`. An SDK is required to compile; the runtime alone is not enough. Check installed SDKs with `dotnet --list-sdks`.

From the repository folder, build with the PowerShell script:

```powershell
.\build.ps1
```

The script restores dependencies and builds Release by default. It can also build and launch the widget, or select Debug:

```powershell
.\build.ps1 -Run
.\build.ps1 -Configuration Debug
```

If PowerShell blocks the script, allow it for this invocation only:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

Alternatively, compile directly:

```powershell
dotnet build .\CodexUsageWidget.csproj --configuration Release
```

Close any running copy of the widget before rebuilding to avoid locked output files. The first build may need network access to restore dependencies. The script can also be invoked from another folder using its full path.

## Run

After a Release build, launch:

```powershell
.\bin\Release\net8.0-windows\CodexUsageWidget.exe
```

For Debug builds, replace `Release` with `Debug`. Keep the executable with the other files in its output folder; this build requires the .NET 8 Desktop Runtime on the machine where it runs.

The widget starts `codex.exe app-server` from the current Codex installation. If that app-server is not already signed in, choose **Connect with ChatGPT**; the widget opens the documented ChatGPT sign-in flow in your browser. Complete sign-in there. The widget does not read or store account tokens itself.

The **Usage** tab shows your limits. Usage windows refresh when the server reports an update, when **Refresh** is clicked, and on the configured schedule. In **Settings**, you can turn **Always on top** on or off and set automatic refresh to Off, 1, 5, or 15 minutes. Manual Refresh works even when scheduled refresh is off. Settings are saved in `%LOCALAPPDATA%\CodexUsageWidget\settings.json`; the defaults are always on top and one-minute refresh.

The window can be moved by its title bar. Closing it stops its local app-server process.

## Notes

- Uses `account/rateLimits/read` and `account/rateLimits/updated` from Codex App Server.
- Requires Codex CLI (`codex.exe`) on the machine; it searches the standard Codex install folder and then `PATH`.
- This is a local personal utility, not a hosted service or an official OpenAI product.
