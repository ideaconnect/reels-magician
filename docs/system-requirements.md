# System requirements

These are the requirements for the upcoming release of ReelsMagician for Windows and Linux. The current release runs on Windows only and is described in the [README](../README.md).

## Where it runs

| | Tested | Should work |
|---|---|---|
| **Windows** | Windows 11 | Windows 10 22H2 |
| **Linux** | Ubuntu 26.10 with sway | Ubuntu 24.04 or newer with GNOME, KDE Plasma or another Wayland desktop, and other distributions with glibc 2.35 or newer and FUSE 3 |

## On every system

- **Processor:** 64-bit x86 (amd64). ARM isn't supported.
- **Internet on first run:** the app downloads its tools and models itself, about 6 GB with the default models. After that, local video files work offline.
- **Disk space:** about 6 GB for the tools and default models, plus room for working files. A 4K source alone can be over 1 GB, so leave at least 10 GB free.
- **Graphics card:** strongly recommended for the analysis step. The default model (Qwen3 8B) wants about 6 GB of video memory, and the lighter Qwen3 4B fits smaller cards. Without a usable graphics card, everything runs on the processor, which works but is much slower.
- **Fonts:** captions use the fonts installed on your computer. If a preset names a font you don't have, a similar one is used, so captions can look slightly different from one computer to another.

What the app downloads on first run, with the default models:

| Component | Size |
|---|---|
| ffmpeg | ~95 MB |
| whisper.cpp | ~30 MB |
| llama.cpp | ~130 MB, ~600 MB as the NVIDIA build on Windows |
| Whisper large-v3-turbo (quantised) | ~574 MB |
| Qwen3 8B Instruct (Q4_K_M) | ~5 GB |
| Face detector | ~0.2 MB |

An ffmpeg already installed on your system is used instead of downloading another copy.

Loading a video from a compatible video link is optional. It needs yt-dlp and Deno, about 64 MB together. The app downloads them only after you agree, and local files work without them.

## Windows

- **Windows 11, x64.** Windows 10 22H2 still runs the app, but Microsoft and .NET no longer support it.
- **No installation:** unzip the release and run `ReelsMagician.exe`. Everything the app needs is in the folder, and no admin rights are required.
- **First start:** the app isn't signed yet, so Windows SmartScreen warns you the first time. Choose **More info**, then **Run anyway**.
- **Graphics drivers:**
  - **NVIDIA:** a current driver. The app picks the CUDA build the driver can run. CUDA 13 needs an RTX 20 or GTX 16 series card or newer; older cards get CUDA 12.
  - **AMD and Intel:** a current driver with Vulkan support, which standard drivers include.
  - Speech recognition runs on an NVIDIA card through CUDA, and on the processor otherwise.
- **Data folder:** `%LOCALAPPDATA%\ReelsMagician`. Reels are written to `Videos\ReelsMagician` unless you choose another folder.

## Linux

The Linux version is an AppImage: a single file that runs without being installed.

- **Distribution:** Ubuntu 24.04 LTS or newer, on amd64. Other distributions with glibc 2.35 or newer and FUSE 3 should work too.
- **Wayland session:** the app doesn't run under X11.
  - **Ubuntu 25.10 and newer:** GNOME always runs on Wayland, so nothing to do.
  - **Ubuntu 24.04:** choose **Ubuntu**, not **Ubuntu on Xorg**, on the login screen.
  - **KDE Plasma, sway and other Wayland desktops:** should work too. The app draws its own windows and file dialogs, so it needs nothing from the desktop except Wayland itself. That includes no `xdg-desktop-portal`.
- **FUSE 3:** the AppImage mounts itself with `fusermount3`. Ubuntu includes it in the `fuse3` package, and the older `libfuse2` isn't needed.
- **Nothing else to install:** the AppImage carries everything else the app needs, OpenCV included. From the system it uses only what every Linux desktop already has: the C and C++ runtime libraries, fontconfig, OpenSSL, ICU, and the session's Wayland and graphics libraries.

### Running and updating

Download `ReelsMagician-x86_64.AppImage` from the release page, make it executable, and start it:

```sh
chmod +x ReelsMagician-x86_64.AppImage
./ReelsMagician-x86_64.AppImage
```

In most file managers you can instead open the file's properties and allow it to run as a program.

- **Applications menu:** the AppImage doesn't add itself. Tools such as Gear Lever can add it if you want.
- **Updates:** the AppImage doesn't update itself. Download the newer file and replace the old one.

### Graphics

- **AMD and Intel:** Mesa's Vulkan driver (`mesa-vulkan-drivers`), which Ubuntu installs by default.
- **NVIDIA:** the proprietary NVIDIA driver, which includes Vulkan.
- **What uses the graphics card:** on Linux, the analysis runs on the graphics card through Vulkan; there is no CUDA build. Speech recognition runs on the processor.

### Permissions

The AppImage isn't sandboxed. The app, and the programs it downloads, run with your user account's permissions, the same as any other program you start.

### Where things live and removing the app

- **Tools, models and settings:** `~/.local/share/ReelsMagician/`
- **Reels:** `~/Videos/ReelsMagician`, unless you choose another folder.

To remove the app, delete the AppImage file and the `~/.local/share/ReelsMagician` folder.

### Known limitation

Screen readers don't work with the Linux version yet. The UI toolkit's Wayland support doesn't provide them so far.
