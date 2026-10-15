# Changelog

## 0.2.0

- Detect the actual Windows build from the registry so MSI compatibility values do not incorrectly reject Windows 10/11.
- Add per-user Windows x64 MSI packaging with a bundled .NET Desktop Runtime and Start menu shortcut.
- Add a single-file setup wizard that checks stable GitHub releases on launch and offers install/update, exact-version repair and uninstall.
- Polish the launcher's dark theme with readable disabled controls, a dark application-path field, softer button borders and contrast/layout regression checks.
- Fix text leaking into other setup controls during partial repaints; preserve drawing clips/translations and give options opaque, buffered backgrounds.
- Verify downloaded installers against GitHub's SHA-256 digest before execution; restrict redirects and allow download cancellation without abandoning MSI transactions.
- Offer opt-in desktop shortcut and app launch on the finish page, with an MSI-managed shortcut and restart-aware launch availability.
- Add a live setup details window, retained verbose logs and OS Notepad log access; direct MSI progress includes optional action details and log access after success, failure or cancellation.
- Use native folder selection and destination confirmation for new installs; preserve the registered default or custom folder through updates, repairs and launch.
- Configure same-version repair, transactional major upgrades, downgrade protection and settings preservation through uninstall.
- Add a manual Settings update check for stable GitHub Releases with validated matching MSI links and clear network/metadata failure states.
- Keep the widget's update link browser-only, with no background software-update polling or installation without an explicit setup action.
- Add pinned WiX build tooling, explicit terms acknowledgement, launcher/payload preparation and SHA-256 artifact output.
- Extend update-response, download integrity and scaled setup/Settings layout regression coverage; document release publishing and disposable installer lifecycle checks.

## 0.1.6

- Allow window resizing and maximizing, with per-monitor DPI scaling and a saved 9–14 pt font-size slider.
- Align Settings labels and inputs in a two-column grid and compact the usage status and action spacing.
- Balance card content padding and fix gauge gutters while preserving rounded cards and tracks.
- Buffer layout containers to reduce resize flashing and keep overflowing cards scrolling vertically.
- Preserve window size during usage refreshes and extend layout and painting regression coverage.

## 0.1.5

- Add shared red and amber percentage thresholds in Settings with an Apply button and inline validation.
- Recolour current usage tiles immediately, without fetching usage or changing the update timestamp.
- Preserve existing settings files and default invalid stored threshold pairs to 15/35.
- Keep the compact Settings layout and add dependency-free regression checks.
- Preserve display scaling when new usage tiles arrive, avoiding clipped cards at higher DPI.

## 0.1.4

- Document Windows build prerequisites, compilation commands, and how to run the app.
- Add `build.ps1` with Release and Debug builds and optional launch via `-Run`.
- Remove unused icon variants and concept images, retaining the app's hourglass icon.
