# Changelog

## 1.0.1 — 2026-11-03

### Changed

- Run the flyout and Settings windows in short-lived UI processes so their memory is released after closing them.
- Recompute the tray presentation when its displayed values can change, instead of rebuilding it every second while idle.
- Use a smaller file stream buffer when polling unchanged session logs.

### Fixed

- Closing the flyout while it is still loading no longer opens the fallback window in the tray process.
- Opening Settings during flyout startup now closes the loading window and cancels the pending flyout.
