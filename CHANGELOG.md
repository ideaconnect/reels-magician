# Changelog

Changes to ReelsMagician, newest first. Version numbers will be assigned once the cross-platform port is done. Until then, every change goes under **Unreleased**.

## Unreleased

### Windows and Linux port, in progress

- **Processing core:** the core now builds for Linux as well as Windows, and passes the same checks on both.
- **Linux graphics:** on Linux the app finds the graphics card, and Vulkan support, from the system itself.
- **Linux archives:** tool archives in the formats Linux builds use (`.tar.gz`, `.tar.xz`) unpack with their permissions and links intact.
- **OpenCV:** face and people detection now use OpenCvSharp 4.13.
- **New interface toolkit:** the interface is rebuilt on Avalonia, so the same app runs on Windows and on Linux, where it runs natively on Wayland. The tabs, wording and colours stay as they were.
- **The app's own dialogs:** the app now draws its own file pickers and confirmation dialogs instead of using the system's, so they look and behave the same on both systems. Questions still default to the answer that changes nothing.
- **Copy path:** the output and data folders now have a **Copy path** button. **Open folder** appears wherever the system can show a folder.
- **Linux downloads:** on Linux the app downloads Linux builds of every tool. llama.cpp runs on the graphics card through Vulkan; whisper.cpp runs on the processor, since that is the only Linux build its publisher offers.

### Changed

- **Fallback builds are reported:** when the best build of whisper.cpp or llama.cpp that this machine supports cannot be found and a lesser one is installed instead (a CPU build on a machine with a capable graphics card, say), the app says so during the install and on the Setup tab, instead of quietly running slower.

- **Video links:** yt-dlp and Deno are no longer part of the default install. **Install everything missing** never fetches them. The app downloads them only after you agree, either the first time you load a compatible video link or from the optional card on the Setup tab. A yt-dlp already on your PATH is used without asking.
- **Links without ffmpeg:** loading a link before ffmpeg is installed now stops and points you to Setup, instead of failing after the download.

### Fixed

- **whisper.cpp on NVIDIA:** whisper.cpp renamed its CUDA builds, and the app stopped recognising them, so NVIDIA cards quietly got a CPU build. Both the old and the new names are recognised again, and the newest CUDA build the card's driver can run is chosen.
- **Failed loads:** a failed or cancelled load no longer throws away the current transcript and plan. The new source replaces the project only once it has loaded. A successful load also clears the subtitles, reels and preview left over from the previous video.
- **Automatic captions:** word-timed automatic captions that come with a video no longer lose lines or show others twice.
- **Drop-down lists:** a closed drop-down list could show the wrong text for the selected item.

## 0.1.0 (2026-11-02)

First public preview. See the [release notes](https://github.com/ideaconnect/reels-magician/releases/tag/v0.1.0).
