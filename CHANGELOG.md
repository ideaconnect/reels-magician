# Changelog

Changes to ReelsMagician, newest first.

## Unreleased

### Changed

- **Step strip:** the row of steps at the top of the window now looks like the app's menu. It is a bar of its own, with the page it opens framed under it, and each step is a rounded chip: the open one in the accent colour, the rest muted. The chips show your progress too. A step's number turns into a check once its output exists, the step to do next has its number ringed, and the steps after it are dimmed, though you can still open them. **Cleanup** stays on the right, outlined and unnumbered, so it does not read as a seventh step. Screen readers hear each chip as, for example, "Step 3 of 6, Subtitles, done".
- **Icons:** the app now draws icons from Font Awesome Free. On the Setup tab each component starts with a green check when it is installed or found on the system, a red X when it still has to be downloaded, and a grey dash when it is optional and not installed, so the states no longer differ by colour alone. A component's warning, such as a Fallback build, gets an amber warning sign. The buttons show an icon before their text, and the step strip's checks and the support bar's cup and heart are Font Awesome's too. Font Awesome's licence is listed with the others and ships with the app.

## 0.2.0 (2026-11-08)

The first release that runs on Linux as well as Windows, and the first with ready-to-run packages: a zip for Windows and an AppImage for Linux. Still an early preview.

### Windows and Linux

- **Processing core:** the core now builds for Linux as well as Windows, and passes the same checks on both.
- **Linux graphics:** on Linux the app finds the graphics card, and Vulkan support, from the system itself.
- **Linux archives:** tool archives in the formats Linux builds use (`.tar.gz`, `.tar.xz`) unpack with their permissions and links intact.
- **OpenCV:** face and people detection now use OpenCvSharp 5, on OpenCV 5.0. On Linux the app ships OpenCV's headless build, so it needs nothing from the desktop: no GTK 3 to install.
- **Releases:** Windows gets a zip with everything included; Linux gets a single AppImage that needs only FUSE 3, which Ubuntu installs by default. The app has an icon of its own for the first time. Both carry the licences of everything they include, in a `licenses` folder next to the app.
- **New interface toolkit:** the interface is rebuilt on Avalonia, so the same app runs on Windows and on Linux, where it runs natively on Wayland. The tabs, wording and colours stay as they were.
- **The app's own dialogs:** the app now draws its own file pickers and confirmation dialogs instead of using the system's, so they look and behave the same on both systems. Questions still default to the answer that changes nothing.
- **Copy path instead of Open folder:** the reel and the data folder have a **Copy path** button, which works the same on every system. **Open folder** and **Open data folder** are gone: they handed the folder to the system's file manager, which a Linux desktop may not have, and there the buttons did nothing.
- **Linux downloads:** on Linux the app downloads Linux builds of every tool. llama.cpp runs on the graphics card through Vulkan; whisper.cpp runs on the processor, since that is the only Linux build its publisher offers.

### Changed

- **Supporting the app:** a bar at the bottom of the window links to Buy Me a Coffee and GitHub Sponsors. ReelsMagician is free and open-source software, and that is how to help keep it going.
- **Fallback builds are reported:** when the best build of whisper.cpp or llama.cpp that this machine supports cannot be found and a lesser one is installed instead (a CPU build on a machine with a capable graphics card, say), the app says so during the install and on the Setup tab, instead of quietly running slower.

- **Video links:** yt-dlp and Deno are no longer part of the default install. **Install everything missing** never fetches them. The app downloads them only after you agree, either the first time you load a compatible video link or from the optional card on the Setup tab. A yt-dlp already on your PATH is used without asking.
- **Face detection:** faces are now found by YuNet, the face detector in OpenCV's model zoo. It also finds a head turned to the side and a small face, such as a webcam in the corner of a screen recording, and takes about a quarter of the time per frame. It is a 0.2 MB download instead of 11 MB: press **Install everything missing** once to get it, and the old detector's files are deleted.
- **Reel names:** a reel's file name is now made of ASCII letters, digits and underscores only: `Laptop czy tablet? Wszystko w jednym` becomes `Laptop_czy_tablet_Wszystko_w_jednym`. It comes out the same on Windows and Linux, and copies to any USB stick or shared folder. The suggested name for exported subtitles is made the same way.
- **Links without ffmpeg:** loading a link before ffmpeg is installed now stops and points you to Setup, instead of failing after the download.
- **Cleanup tab:** **Free up space** moved from the Setup tab to a tab of its own, **Cleanup**, on the right of the tab strip, apart from the six steps. It measures the data folder afresh each time it opens.
- **Buttons on the right:** every step's buttons now sit on the right, with its main action rightmost, and so do the confirmation dialogs and the file picker. A question still has the answer that changes nothing selected, so Enter never agrees by accident.

### Fixed

- **Repeated speech:** words spoken again were sometimes dropped or cut short, as if they were a caption repeating the previous line. Only a real rolling repeat is trimmed now: one that starts as the previous cue ends and repeats it on a line of its own.
- **One-word captions:** in captions that come with a video, a line adding a single new word could appear up to a second late. It now appears when that word is spoken.
- **Free up space:** after deleting, the status bar says how much was freed, or which files are still in use, instead of only "Freeing up space complete.". A progress line arriving late from a finished step can no longer replace that, or the path of a rendered reel.
- **Failed installs:** when an install fails or is cancelled partway, the Setup tab now shows the tools it did install, instead of listing them as missing until you press **Refresh**.
- **Typed paths:** a source or output folder typed as `~/Videos/…` now starts in the home folder, instead of failing to load or creating a folder named `~`. A relative output folder no longer fails the render after it has started; it is taken from the home folder, and an empty one means the default folder.
- **Quoted links:** a link pasted in quotes skipped the ffmpeg check and the question before yt-dlp is downloaded. Quotes are now removed once, before anything looks at the input, and single quotes from a Linux terminal are removed too.
- **Where the reel went:** after a render, the status bar could say "Reel complete" instead of naming the file it wrote. It names the file again.
- **Stale previews:** a caption preview still rendering when another video loaded could put the old video's frame back on screen.
- **Free up space:** the "Downloaded tools" row named yt-dlp and Deno even when they had never been downloaded. It now names what is actually there.
- **whisper.cpp on NVIDIA:** whisper.cpp renamed its CUDA builds, and the app stopped recognising them, so NVIDIA cards quietly got a CPU build. Both the old and the new names are recognised again, and the newest CUDA build the card's driver can run is chosen.
- **Failed loads:** a failed or cancelled load no longer throws away the current transcript and plan. The new source replaces the project only once it has loaded. A successful load also clears the subtitles, reels and preview left over from the previous video.
- **Automatic captions:** word-timed automatic captions that come with a video no longer lose lines or show others twice.
- **Drop-down lists:** a closed drop-down list could show the wrong text for the selected item.

## 0.1.0 (2026-11-02)

First public preview. See the [release notes](https://github.com/ideaconnect/reels-magician/releases/tag/v0.1.0).
