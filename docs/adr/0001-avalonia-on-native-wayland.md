# Avalonia, on native Wayland, replaces WPF

Status: accepted, 2026-11-07

ReelsMagician leaves WPF for Avalonia 12 on .NET 10, so one codebase ships for Windows and Linux, with macOS as a possible later addition. We chose this over a rewrite in Rust with Slint because it keeps the work already done. The processing core and its tests carry over unchanged, and only the UI layer, about 2,700 lines, depends on the toolkit. Avalonia also has the longer record: public since 2013, more than 700 contributors and sponsored maintenance. Slint is very active, but it is younger and run by a small company whose customers are mostly embedded. On Linux the app uses only Avalonia's native Wayland backend and never X11, even though Avalonia's documentation still calls that backend early stage. That is the trade-off, because mature Wayland support was Slint's strongest argument. We accepted the risk after a test app passed on GNOME's compositor with X11 switched off: it opened a window, handled 5,000 editable rows and opened modal dialogs repeatedly.

## Considered options

- **Rewrite in Rust with Slint.** Mature Wayland support and a small runtime. Against it: it discards the tested C# core, has no built-in file dialogs, its table view cannot edit cells, and its free licence requires an attribution on the About screen.
- **Avalonia through XWayland.** Works today, but keeps the X11 dependency that the Linux build is meant to drop.

## Consequences

- The Linux build will not start without a Wayland compositor. Avalonia has no automatic fallback, and we don't add one.
- Screen readers don't work on Avalonia's Wayland backend yet ([Avalonia #22696](https://github.com/AvaloniaUI/Avalonia/issues/22696)).
- Avalonia's DataGrid is deprecated and its successor is paid, so the editable cue table can't rely on either.
- Switching to XWayland is a small code change if the Wayland backend fails on real desktops. It would reverse this decision, so it needs a new ADR.
