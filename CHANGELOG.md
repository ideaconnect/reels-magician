# Changelog

Changes to ReelsMagician, newest first. Version numbers will be assigned once the cross-platform port is done. Until then, every change goes under **Unreleased**.

## Unreleased

### Changed

- **Video links:** yt-dlp and Deno are no longer part of the default install. **Install everything missing** never fetches them. The app downloads them only after you agree, either the first time you load a compatible video link or from the optional card on the Setup tab. A yt-dlp already on your PATH is used without asking.
- **Links without ffmpeg:** loading a link before ffmpeg is installed now stops and points you to Setup, instead of failing after the download.

### Fixed

- **Failed loads:** a failed or cancelled load no longer throws away the current transcript and plan. The new source replaces the project only once it has loaded. A successful load also clears the subtitles, reels and preview left over from the previous video.
- **Automatic captions:** word-timed automatic captions that come with a video no longer lose lines or show others twice.
- **Drop-down lists:** a closed drop-down list could show the wrong text for the selected item.

## 0.1.0 (2026-11-02)

First public preview. See the [release notes](https://github.com/ideaconnect/reels-magician/releases/tag/v0.1.0).
