# Changelog

All notable changes to Better Bluetooth Audio Connector are documented here.

## [1.0.0] - 2026-09-08

First public release.

### Added

- Unpackaged, self-contained WinUI 3 desktop build that runs without MSIX or separately installed runtimes.
- Inno Setup installer with English and Simplified Chinese UI, destination selection, shortcuts, and standard uninstall support.
- Device availability states for checking, nearby, offline, connected, and unknown devices.
- Cancellable Start/Open connection stages, bounded retries, late-result isolation, and watcher recovery.
- Recovery from stale audio streams, sleep/resume handling, and system media controls.
- Privacy-preserving diagnostic logs with seven-day retention and an in-app log-folder button.
- Per-Monitor V2 DPI support, compact window layout, and a multi-resolution application icon.

### Limitations

- One audio source connection at a time.
- Devices must be paired in Windows before they can be selected.
- Windows x64 only.
- The installer is not currently code-signed.
