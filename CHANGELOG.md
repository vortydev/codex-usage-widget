# Changelog

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
