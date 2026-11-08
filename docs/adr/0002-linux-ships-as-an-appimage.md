# Linux ships as an AppImage

Status: accepted, 2026-11-07

ReelsMagician for Linux is released as a single AppImage file attached to each GitHub release, next to the Windows zip. Our first choice was a sandboxed Flatpak, and a review showed that the programs the app downloads would run fine inside one. A separate decision changed that. On both systems the app draws all of its own dialogs, file pickers included, so it needs nothing from the desktop beyond Wayland. A sandboxed app normally gets access to a file at the moment the desktop's own file dialog, the portal, hands it over. Without that dialog, a Flatpak has to be given its folders up front. That leaves two bad choices: most videos are out of reach, or the whole home folder is open, and with it most of what the sandbox protects. An AppImage has no sandbox. On Ubuntu it needs nothing beyond the FUSE 3 that ships with the system, and its picker can reach every file the user can.

## Considered options

- **Flatpak with fixed folders** (Videos, Downloads, Documents, Desktop and external drives). Hidden settings stay out of reach, but a video stored anywhere else must be moved first, and Ubuntu users must install Flatpak.
- **Flatpak with the whole home folder.** Everything is reachable, but little is left protected.
- **Snap.** It is sandboxed and comes with Ubuntu. Its home-folder access matches the fixed-folder option and works without a portal. It is only worth shipping through the Snap Store, and Linux Mint blocks it. It remains a possible addition.

## Consequences

- The app, and every program it downloads, runs with the user's own permissions. Nothing is sandboxed.
- An AppImage never updates itself. To update, users download the newer file.
- Data lives in `~/.local/share/ReelsMagician`. Removing the app means deleting the file and that folder.
- The AppImage must be built with the current static runtime, which needs only FUSE 3 (`fusermount3`). The old runtime needs libfuse2, which Ubuntu no longer installs.
- Showing a folder in a file manager depends on the desktop, and a minimal Wayland session may have none. So the app has no **Open folder** at all, on either system, and offers **Copy path** instead, which works everywhere.
