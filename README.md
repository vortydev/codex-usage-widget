# Codex Usage Widget

A small Windows window for ChatGPT-plan usage limits reported by the local Codex App Server. It uses the standard Windows caption controls and rounded usage cards and progress tracks.

## Install, repair and update

Download **CodexUsageSetup.exe** from this repository's stable GitHub Release and launch it. This single-file Windows setup app checks for the latest stable release automatically when opened, shows the installed and available versions, and offers three actions:

- **Install / update:** download and install the latest stable x64 MSI. Existing newer installations are never downgraded.
- **Repair:** download the exact installed version's MSI and restore missing or modified application files without silently upgrading.
- **Uninstall:** remove the registered installation while keeping your saved settings. This action remains available if GitHub is offline.

After a successful install, update or repair, the finish page offers **Add desktop shortcut** and **Start Codex Usage Widget**, both unchecked by default. The desktop shortcut is managed by Windows Installer and removed on uninstall. A pre-existing desktop shortcut is left unchanged. If Windows requests a restart, starting the app is disabled. There is no automatic startup entry.

The setup launcher uses consistent dark controls: disabled actions remain readable, the application path has a dark read-only field, and buttons have subtle borders with visible keyboard focus. Custom text drawing preserves repaint clipping and translation, and options use opaque, buffered backgrounds to prevent overlapping labels. The native Windows folder picker and confirmation dialogs remain OS-managed.

The launcher verifies the GitHub asset's SHA-256 digest before running it, rejects untrusted download redirects and never starts installation merely by checking for updates. Internet access and a published MSI asset with a GitHub digest are required for install/update and repair. Windows Installer supplies progress, cancellation and files-in-use prompts. **Show details** opens a read-only window showing actual setup stages and the live Windows Installer log; **Open log** opens the full saved log in Windows Notepad after the operation. Setup logs are retained under `%LOCALAPPDATA%\CodexUsageWidget\SetupLogs`; closing setup during a download cancels it, but does not abandon a running installer transaction. The details window reads a bounded log tail only while open, not a background service.

The underlying Windows x64 MSI installs for the current user, with a **Codex Usage Widget** Start menu shortcut. The default location is `%LOCALAPPDATA%\Programs\CodexUsageWidget`. On a new install, **Browse…** uses Windows' folder picker to select a dedicated, local, user-writable application folder, followed by a native confirmation showing the destination before installation. Do not select a folder containing unrelated applications. Updates and repairs reuse the registered location; they do not offer relocation. Launcher validation rejects relative paths, network paths and drive roots. Both the launcher and widget bundle the .NET Desktop Runtime; the target PC does not need an SDK or a separately installed .NET runtime. Windows 10/11 x64 and an installed Codex CLI are still required.

Close the widget before running an installer. Windows Installer's files-in-use dialog handles open copies; the installer does not forcibly kill Codex or its app-server processes. Closing the widget normally stops only the app-server it owns.

- **Direct MSI install:** run `CodexUsageWidget-0.2.0-win-x64.msi`. A fresh install uses Windows Installer's native destination page and folder browser, then displays the chosen path for confirmation. Existing installs retain their registered path. Enable **Show action details** to see current action/file information alongside the native progress bar. **Open log** is available on the success, failure and cancellation pages; the MSI enables verbose, flushed logging by default, with its log path shown on the final page (normally a Windows temporary `MSI*.LOG` file). Then open the Start menu shortcut. Use the setup app for the optional finish-page actions.
- **Repair:** rerun the same released MSI and select **Repair**. Missing and modified application files are restored, including the bundled runtime and shortcut.
- **Update:** run a newer version's MSI. The stable upgrade identity replaces the old version; downgrade attempts are blocked. Upgrade removal is scheduled inside the MSI transaction so a failed upgrade can roll back.
- **Uninstall:** use Windows Settings → Apps, or the MSI's **Remove** option. User settings at `%LOCALAPPDATA%\CodexUsageWidget\settings.json` remain intact through repair, upgrade and uninstall.

Existing source-built/portable copies are not registered MSI products and are not moved or deleted by installation. Close those copies and use the installed Start menu shortcut afterward to avoid running the old executable.

In the widget's **Settings** tab, select **Check for updates**. This makes an unauthenticated, timeout-bounded request to this repository's latest stable GitHub Release; it never polls for software updates automatically. A newer release needs a matching `CodexUsageWidget-MAJOR.MINOR.PATCH-win-x64.msi` asset before a download link appears. The link opens GitHub in your browser; the widget does not download or execute installers. No-release, missing-asset, offline, timeout and malformed-release states are reported inline. This check does not fetch account usage or change its update timestamp.

Setup and MSIs are currently unsigned. Windows may warn about an unknown publisher. Obtain them from this repository's release page and compare their SHA-256 against the accompanying `.sha256` file; a checksum checks integrity, not publisher identity. Signing requires a separately supplied certificate and is not configured here.

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

### Build the installer

The packaging project pins WiX SDK and UI extension **7.0.0** through NuGet, without a global WiX installation. Review the [WiX 7 terms](https://docs.firegiant.com/wix/osmf/) before using the explicit acknowledgement switch:

```powershell
.\build-installer.ps1 -AcceptWixEula
```

This publishes a fresh, untrimmed, self-contained `win-x64` payload, generates per-user file components with HKCU key paths and path-stable explicit component GUIDs, builds the single-file setup launcher, and builds the MSI with normal WiX validation enabled. The configured project version drives the executable/footer, setup executable, MSI version, version-specific product code and artifact filename. Keep the package identity `vortydev.CodexUsageWidget` stable across upgrades. Outputs are `artifacts\installer\CodexUsageSetup.exe` and `artifacts\installer\CodexUsageWidget-VERSION-win-x64.msi`, each with its SHA-256 file; temporary payloads are retained under ignored `artifacts\staging` for inspection. Each published release must have a new version; do not replace a published MSI with different binaries under the same version.

MSI validation runs with warnings treated as errors, except **ICE91**. That check warns about per-user folders in per-machine installations; Microsoft documents it as harmless for per-user-only packages. This package explicitly rejects `ALLUSERS` and never offers per-machine installation. See [Microsoft's ICE91 documentation](https://learn.microsoft.com/en-us/windows/win32/msi/ice91). Repair mode is set by the repair action, not globally for every installation.

To build the setup launcher and prepare the runtime payload and generated XML without accepting WiX terms or building an MSI:

```powershell
.\build-installer.ps1 -PrepareOnly
```

Run payload checks with the generated source path printed by the script. If an MSI was built, also pass `-MsiPath` for read-only Windows Installer database, dialog wiring and checksum checks. A safe MSI session also checks Windows build discovery, evaluates OS guard boundaries and restores a simulated custom install path without installing anything:

```powershell
.\tests\InstallerChecks.ps1 -PayloadSource .\artifacts\staging\BUILD_ID\Payload.wxs
.\tests\InstallerChecks.ps1 -PayloadSource .\artifacts\staging\BUILD_ID\Payload.wxs -MsiPath .\artifacts\installer\CodexUsageWidget-0.2.0-win-x64.msi
```

Publishing a release is a separate, manual action: build from the intended clean, tagged source, run the checks below, then create a non-draft/non-prerelease GitHub Release for `vMAJOR.MINOR.PATCH` and attach the setup EXE, MSI and both checksums. Keep older release MSIs available for exact-version repair. A Git tag alone is not discoverable by the update checker. The scripts do not commit, tag, push, publish, install or sign anything.

## Run

After a Release build, launch:

```powershell
.\bin\Release\net8.0-windows\CodexUsageWidget.exe
```

For Debug builds, replace `Release` with `Debug`. Keep the executable with the other files in its output folder; this build requires the .NET 8 Desktop Runtime on the machine where it runs.

The widget starts `codex.exe app-server` from the current Codex installation. If that app-server is not already signed in, choose **Connect with ChatGPT**; the widget opens the documented ChatGPT sign-in flow in your browser. Complete sign-in there. The widget does not read or store account tokens itself.

The **Usage** tab shows your limits. Usage windows refresh when the server reports an update, when **Refresh** is clicked, and on the configured schedule. In **Settings**, you can turn **Always on top** on or off and set automatic refresh to Off, 1, 5, or 15 minutes. Manual Refresh works even when scheduled refresh is off. Settings are saved in `%LOCALAPPDATA%\CodexUsageWidget\settings.json`; the defaults are always on top and one-minute refresh.

Colour thresholds are shared by all usage tiles. In **Settings**, edit **Red below (%)** and **Amber below (%)**, then choose **Apply thresholds**. Values must be whole numbers with `0 ≤ red < amber ≤ 100`. Defaults are red below 15% remaining, amber below 35%, and teal at or above 35%; colours use the actual percentage rather than the rounded label. Invalid entries leave thresholds unchanged. Applying immediately recolours existing tiles without refreshing usage. Other settings still save automatically. Older settings files receive the defaults; invalid stored threshold pairs fall back to 15/35 without resetting unrelated settings. If saving fails, the app reports that changes only last until close.

The window can be moved by its title bar and resized by dragging its edges. Windows display scaling is applied per monitor. In **Settings**, the **Font size** slider adjusts text from 9 to 14 pt and saves automatically; spacing grows with the text. Usage refreshes preserve the current window size, and additional usage cards can be scrolled. Closing it stops its local app-server process.

## Checks

Run dependency-free settings, update-metadata, setup-download and off-screen UI regression checks on Windows:

```powershell
dotnet run --project .\tests\ThresholdTests.csproj --configuration Release
```

The HTTP checks use simulated responses and no credentials or live network requests. Setup tests exercise download integrity, trusted redirects, exact-release lookup, native installer command construction, default/custom install records, bounded UTF-8/UTF-16 log reading, the details window and opt-in finish options without executing MSI operations. Setup layouts use isolated new-install and existing-install fixtures, not your installation record. Layout checks cover minimum window size, the widget's 9/14 pt font endpoints, and simulated 100%, 150% and 200% scaling; they do not substitute for physical multi-monitor DPI transitions or interaction with the real OS folder dialog.

Before releasing an MSI, verify the following in a disposable Windows 10/11 x64 VM or account (not your development installation):

1. Launch the single-file setup app as a non-admin user without a preinstalled .NET runtime; check stable release discovery and clean install. Exercise both finish checkboxes, Start menu launch, version, account connection and both widget tabs.
2. Change refresh/font/threshold settings. Remove an application file and modify another, then **Repair** with the same released MSI. Check restoration, shortcut and unchanged settings.
3. Install an older MSI-built version, then the new one. Check one registered product, preserved settings, new version and blocked downgrade. Portable versions before 0.2.0 are not MSI upgrade fixtures.
4. Keep the widget open during repair/upgrade; check the files-in-use prompt, close the widget and retry. Verify unrelated Codex processes remain running.
5. Cancel an upgrade and exercise rollback after a controlled failure in the disposable environment. Verify the old registered application and settings survive.
6. Uninstall from the setup app and verify program files and MSI-managed shortcuts are removed, settings remain, and reinstall picks them up. Check both tabs and the setup wizard at real 100/150/200% display scaling and font extremes.
7. Check no-release/offline/rate-limit states, missing digests, cancelled downloads and unavailable old releases. No failed download should start MSI. Verify uninstall remains available offline and repair never switches to a different release.
8. Exercise both the launcher's Windows folder picker and the direct MSI's native folder browser. Choose a user-writable custom path containing spaces; verify the confirmation, shortcuts, finish-page launch, repair and upgrade all use it. Cancel folder selection and confirmation and verify no installation starts. Check an unwritable destination produces a visible failure rather than claiming success.
9. Open **Show details** before download and during installation; check stages, live action/log output, closing/reopening details, saved log and cancellation/failure visibility. Check the direct MSI's details toggle and **Open log** on every final outcome.

Installer source inspection/build validation is not evidence that these lifecycle cases passed. They require an actual disposable installation.

## Notes

- Uses `account/rateLimits/read` and `account/rateLimits/updated` from Codex App Server.
- Requires Codex CLI (`codex.exe`) on the machine; it searches the standard Codex install folder and then `PATH`.
- This is a local personal utility, not a hosted service or an official OpenAI product.
