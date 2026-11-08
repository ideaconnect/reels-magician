# ReelsMagician

A desktop app for Windows and Linux that turns a long video into a vertical **1080×1920** reel.

Point it at an MP4 or a compatible video link. It transcribes the speech, lets you correct the subtitles,
asks a local Qwen model to pick the most compelling stretch, keeps the subject centred in the
vertical crop, joins the clips with a transition too short to smear the audio, and burns in
captions — plain, or with each word highlighted as it is spoken, the way CapCut and Filmora do it.

Everything runs locally. Nothing is uploaded.

> **Status: an early and unstable preview.** It works end to end on the machines it has been
> tested on, Windows 11 and Ubuntu 26.10, but expect rough edges, and expect settings and on-disk
> formats to change between versions.

---

## What it does

| Stage | What happens |
|---|---|
| **Source** | Opens a local file, or downloads a link with yt-dlp, an optional third-party tool you are asked about the first time you load a link. Downloads prefer 4K and refuse to settle below 1080p while a better format exists. Progress is reported throughout — a 4K master is often over a gigabyte. |
| **Transcribe** | Uses the subtitles that came with the video if there are any, otherwise runs Whisper with word-level timings. |
| **Review** | Shows every cue with its in and out times in an editable table. Fix the text, split, merge, retime, delete. |
| **Analyse** | Groups cues into paragraphs, scores each paragraph against the video's topic with Qwen, then assembles several complete reels of the length you asked for. |
| **Framing** | Samples frames of the chosen clips and tracks the subject, so the vertical crop follows it instead of taking a fixed centre slice. |
| **Captions** | A style table drives the burned-in captions: plain text, or per-word highlighting with a colour and a pop. Preview against a real frame. |
| **Render** | Encodes 1080×1920 H.264 + AAC, loudness-normalised to −14 LUFS. |

### The details that matter

**Whole paragraphs, not words.** The analyser never sees isolated words. Cues are grouped into
paragraphs on sentence endings and speech pauses, capped so no block is too long to reason about
or too short to stand alone. Every paragraph is scored on whether it would hold a scrolling
viewer, and the assembly pass builds runs that open on a hook and end on a complete thought.

**The topic is an input.** For a link, the video title; for a file, the filename with release-tag
noise stripped out. Both scoring passes are anchored to it, and you can correct it before
analysing if it is misleading.

**Long videos still work.** Scoring runs over overlapping windows of paragraphs, so an
hour-long transcript never has to fit in one context window.

**Captions match the language actually spoken.** A compatible video link can offer machine-translated
caption tracks next to the real ones — a Polish video offers `en-pl`, "English from Polish". ReelsMagician reads the
video's own language and takes the original track (`pl-orig`, then `pl`), never a translation.
Burning translated captions over the original speech, timed to the translation, would be worse
than having no subtitles at all. Subtitles are also fetched in a separate pass, so a hiccup on one
caption track cannot throw away a gigabyte-scale download that succeeded.

**Subject tracking, in three tiers.** A face in the foreground wins, chosen by apparent size with
hysteresis so the crop does not flip between two people on screen. If no face is found — someone
turned away, or there is no person at all — it falls back to a full-body detector, and then to the
visually dominant region (edge detail plus inter-frame motion). That last tier is what keeps a
product, a screen share or a landscape centred.

The resulting path is median-filtered against outliers, eased with a deadzone so a stationary
speaker does not make the frame twitch, speed-limited so pans never snap, and reduced to a handful
of keyframes. A shot with no real movement is emitted as a fixed crop.

**Only cropping and uniform scaling ever touch the picture.** The crop window is the largest
whole multiple of 9×16 that fits the source (1215×2160 from 4K, 603×1072 from 1080p), so the
scale to 1080×1920 is uniform by construction. Anamorphic sources are resampled to square pixels
first and planned in that geometry. The final scale is also told to keep the incoming proportions
and is followed by a centre crop, so a wrong window could only ever lose a pixel of edge; it can
never stretch the frame. The caption preview uses the same chain as the reel.

**Transitions that do not touch the audio.** Clips are joined with a ~100 ms cross dissolve, and
the audio is crossfaded by *the same* amount. This matters: `xfade` consumes its overlap from the
timeline, so fading only the picture would slide the audio later by one transition length on every
cut. Matching them keeps sound and picture locked, and at 100 ms the audio blend is inaudible.

**Captions stay up long enough to read.** A line can only occupy the span between its own first
word and the next line's first word — it must not start early (the highlight would lead the
speech) or end late (two lines would draw at once). When that span is too short for the text, the
only remedy left is fewer lines, so the line is joined to a neighbour and the pair share the sum of
their windows. Every word keeps its own timing, so the highlight stays exactly in sync; only the
line break moves.

The floor is 0.8 s, which no line ever breaches, with 1.0 s as the preference and 25 characters per
second as the reading-rate ceiling — both adjustable on the Captions tab. Those come from the
delivery specs: Netflix enforces 20 frames and 20 cps at QC, Amazon and the ESIST code both say one
second. The rate is set above Netflix's because word-level highlighting removes the cost of finding
your place again, which the minimum duration does not.

Gaps are either nothing at all or a real pause. A brief blank between two lines reads as a flicker
rather than a beat; the BBC's own subtitle validator errors on any non-zero gap below 0.8 s, so
anything shorter is closed up and the time spent leaving the words on screen instead.

A real pause is respected when lines are joined, too. A starved line has two possible partners,
and joining it to the one on the far side of a pause would put its words on screen while the
speaker is still silent and paper over the beat. So the partner on the same side of any pause is
preferred, and one across a pause is used only when it is the sole way to make the line readable —
on a real transcript of 660 lines, that is one line rather than four. A short word between two
beats is not joined at all; it simply stays up for its minimum time, exactly when it is spoken.

The joining pass measures a line by the same rule that later decides when it leaves the screen —
its hold after the last word, the next line's start, and the closed-gap rule — so a short line
ahead of a real pause is judged by the hold it will actually get, not by the whole pause.

**Word highlighting done properly.** ASS karaoke tags can only swap between two style colours —
they cannot scale a word or animate it. So each caption emits one event per word, carrying the
whole line with override tags on the word being spoken. That is what produces the CapCut look
rather than a karaoke machine.

**The caption never moves.** The spoken word is emphasised with colour and, optionally, a
*vertical* grow — never a horizontal one. `\fscx` changes a glyph's advance width, so scaling the
spoken word widens the line, and a centred line re-centres: every other word slides sideways, and
sweeping the highlight along makes the whole caption jitter. Measured against rendered frames, the
old horizontal scale moved the rest of the line 19 px per word; growing only the vertical axis
moves it 0 px while still visibly popping. A rendering test asserts this on every preset, because
it is invisible in the ASS text — the tags look perfectly reasonable either way.

---

## Requirements

[docs/system-requirements.md](docs/system-requirements.md) has the details for each system. In short:

- **Windows 11, x64.** Windows 10 22H2 should work, but Microsoft and .NET no longer support it.
- **Linux, x64, in a Wayland session:** Ubuntu 24.04 or newer, with GNOME, KDE Plasma, sway or
  another Wayland desktop. The app does not run under X11.
- **Disk:** about 6 GB for the tools and default models, plus about 0.5 GB more on Windows with an
  NVIDIA card, for the CUDA runtime.
- **GPU:** strongly recommended for the analysis step. NVIDIA with a current driver, or any GPU with
  a Vulkan driver.
- **[.NET 10 SDK](https://dotnet.microsoft.com/download)** only to build it yourself. The releases
  include everything they need.

Everything else the app installs itself, into its own data folder: `%LOCALAPPDATA%\ReelsMagician`
on Windows, `~/.local/share/ReelsMagician` on Linux. No admin rights, no system changes, nothing
added to your PATH. Tools already on your PATH, most likely ffmpeg, are reused rather than
downloaded again.

| Component | Source | Size |
|---|---|---|
| ffmpeg + ffprobe | BtbN FFmpeg-Builds | ~95 MB |
| whisper.cpp | ggml-org/whisper.cpp | ~29 MB |
| llama.cpp | ggml-org/llama.cpp | ~124 MB, ~600 MB as the CUDA build |
| Whisper large-v3-turbo (q5) | ggerganov/whisper.cpp | ~547 MB |
| Qwen3 8B Instruct (Q4_K_M) | Qwen/Qwen3-8B-GGUF | ~4.7 GB |
| Face detector (YuNet) | opencv / opencv_zoo | ~0.2 MB |

Smaller Whisper and Qwen models are selectable on the Setup tab if you are short on disk or VRAM.

### About video links

Loading a video from a link is optional. It needs two components that are not part of ReelsMagician
and are not installed with the tools above:

| Component | Source | Size |
|---|---|---|
| yt-dlp | yt-dlp releases | ~23 MB |
| Deno (JS runtime for yt-dlp) | deno releases | ~41 MB |

The first time you load a link, the app asks before downloading them; you can also start the
download from **Video links** on the Setup tab. If you decline, local video files keep working as
normal. A yt-dlp already on your PATH is used without asking. You are responsible for having the
right to download each video you load from a link, and for following the terms of the site it comes
from.

Two things are easy to get wrong here, and ReelsMagician handles both:

- **A JavaScript runtime is required in practice.** For some compatible video links, yt-dlp now treats
  extraction without one as deprecated and silently drops formats — sometimes every 4K variant. ReelsMagician downloads
  Deno alongside yt-dlp and points yt-dlp at it. If it is missing, the app says so instead of quietly
  giving you 1080p.
- **yt-dlp finds ffmpeg only on PATH**, and ReelsMagician keeps its ffmpeg in a private folder that is
  never added to PATH. Without `--ffmpeg-location`, a `bv*+ba` download cannot be merged: yt-dlp
  warns, *still exits 0*, and leaves separate video-only and audio-only files — which would sail
  through as a silent source. ReelsMagician passes the location explicitly, and fails loudly if it
  ever ends up with an unmerged pair.

### About CUDA and Vulkan

**On Linux, Qwen runs on Vulkan and Whisper on the CPU.** llama.cpp publishes no CUDA build that
ReelsMagician uses on Linux, and its Vulkan build runs on AMD, Intel and NVIDIA alike. whisper.cpp
publishes only a CPU build for Linux. The app finds the GPU in sysfs and counts a Vulkan driver
only when the card's kernel driver can run it: Mesa installs its manifests for every vendor
whatever the hardware.

**On Windows, Qwen runs on CUDA on an NVIDIA card, and on Vulkan everywhere else.** llama.cpp
publishes official Windows builds for both. Vulkan runs on AMD, Intel and NVIDIA alike, but on an NVIDIA
card CUDA is faster, most of all when reading the long transcript the analysis starts from.

On an NVIDIA card the app asks the driver which CUDA version it supports and which generation the
card is, then picks the newest CUDA build that can run: CUDA 13 for a Turing card (RTX 20 series)
or newer, CUDA 12 for older cards and older drivers. The CUDA build needs its runtime libraries
next to it, about 400 MB, which the app downloads alongside it. A driver too old for either falls
back to Vulkan. On any other GPU the app detects your Vulkan driver and picks the Vulkan build.

Detection reads the ICD manifest registered on the display adapter, which is where current drivers
put it — the older `HKLM\SOFTWARE\Khronos\Vulkan\Drivers` hive is checked too, but many modern
systems no longer populate it.

**Whisper does not, yet.** whisper.cpp publishes no Vulkan build for Windows — only CPU, OpenBLAS
and CUDA. ReelsMagician therefore picks CUDA on an NVIDIA card and OpenBLAS otherwise. This is
not much of a bottleneck in practice: large-v3-turbo transcribes roughly 6× faster than real time
on a modern CPU.

**A lesser build is never installed silently.** If the best build this machine supports cannot
be found (say, an NVIDIA card would get CPU whisper.cpp because the CUDA build was renamed), the
install log says so, and so does the Setup tab, for as long as that build stays installed.

The preference list is matched by pattern with Vulkan first, so if that project ever ships a
Vulkan asset it will be adopted with no code change. To use your own build today, point
`customWhisperDir` at it in `%LOCALAPPDATA%\ReelsMagician\settings.json`.

---

## Getting it

Releases come as a zip for Windows and an AppImage for Linux.

- **Windows:** unzip `ReelsMagician-win-x64.zip` and run `ReelsMagician.exe`. It isn't signed yet,
  so SmartScreen asks once: choose **More info**, then **Run anyway**.
- **Linux:** `chmod +x ReelsMagician-x86_64.AppImage`, then run it from a Wayland session.

## Build and run

```sh
git clone https://github.com/ideaconnect/reels-magician.git
cd reels-magician
dotnet build -c Release
dotnet run --project src/ReelsMagician.App -c Release
```

The same commands work on Windows and Linux. On Linux, run it from a Wayland session. The
release packages are built by `.github/workflows/release.yml`, and the AppImage by
`packaging/linux/build-appimage.sh`. Before the AppImage is uploaded,
`packaging/linux/check-appimage.sh` checks that it starts and holds its libraries and licences.

On first launch, open **1 · Setup** and press **Install everything missing**. It resumes if
interrupted, so a dropped connection is not a restart.

---

## Using it

**2 · Source** — paste a link or browse to a file, set the reel length, press **Load source**.
The first time you load a link, the app asks before downloading yt-dlp. The topic box is prefilled from the title or filename; correct it if it is wrong, because the
analysis is anchored to it. Then **Transcribe**.

**3 · Subtitles** — the review step. Every cue with its times, editable in place. Amber text marks
cues Whisper was unsure about, so you can aim your attention. Split, merge, nudge by 100 ms,
delete, or import and export SRT.

Edits are safe: when you change a line, word timings are re-derived so that words which survived
the edit keep their original timing and only new ones are re-spread. A typo fix never
desynchronises the rest of the line — which matters, because these timings drive the karaoke
highlight.

Press **Analyse and pick the reel** when you are happy.

**4 · Reel** — the proposals, each with its hook, its reasoning and its clip ranges. Pick one and
press **Plan framing**; the panel on the right then reports what it locked onto for each clip
("speaker, 87% of frames", "main visual subject, tracking across 430 px").

**5 · Captions** — the caption table. Start from a preset, then adjust anything: mode, entrance
animation, font, size, colours, outline, shadow, background box, position, words per line, the
shortest time a line may stay up, the fastest reading rate, and how
the spoken word is emphasised — **Colour** recolours it and nothing moves at all, **Grow** also
enlarges it vertically, which still leaves the line's width, and so its position, untouched. **Refresh preview** renders a real frame of your reel with the captions
burned in. Transition length lives here too.

**6 · Render** — choose an output folder and go. **Copy path** copies the reel's full path, ready to
paste into an upload page's file dialog, a file manager or a terminal. The activity log shows every
step.

File pickers and confirmations are drawn by the app itself, the same on both systems. A question
always defaults to the answer that changes nothing.

### Caption presets

| Preset | Look |
|---|---|
| Plain white | Static lower-third lines, no highlighting |
| Pop yellow | White text, spoken word in yellow and scaled up, pop entrance |
| Green karaoke | Tighter three-word lines, green highlight, slide-up entrance |
| Boxed caption | Filled background box instead of an outline; colour-only highlight |
| Big bounce | Large Impact-style text with an overshooting bounce |

---

## Where things live

```
%LOCALAPPDATA%\ReelsMagician\        on Windows
~/.local/share/ReelsMagician/        on Linux
  tools\      ffmpeg, whisper.cpp, llama.cpp; yt-dlp and the JS runtime if you downloaded them
  models\     Whisper, Qwen and face detector weights
  cache\      per-project working files (audio, clips, subtitles)
  downloads\  partially fetched archives
  logs\       error logs
  settings.json
```

Reels are written to `Videos/ReelsMagician` in your home folder unless you choose otherwise.

The app used to be called Selfroller. On first run it moves an existing `%LOCALAPPDATA%\Selfroller`
folder to the new name, so nothing has to be downloaded again. An output folder you already chose
is kept as it is.

### Freeing up space

**Free up space** on the Setup tab measures what each of those costs on disk and removes the parts
you tick:

| | ticked by default | why |
|---|---|---|
| Working files | yes | Safe to clear whenever — the app recreates them |
| Logs | no | Tiny, and useful when something has gone wrong |
| Downloaded tools | no | ~280 MB to fetch again |
| Downloaded models | no | Several gigabytes to fetch again |
| Settings | no | Resets your model choices and output folder |

The button names the amount it is about to delete, and confirms with the exact list before doing
anything. It only ever touches paths inside the data folder — a path outside it, the folder root
itself, or a `..` traversal is refused rather than followed.

---

## Project layout

```
src/ReelsMagician.Core/        no UI, fully testable
  Toolchain/    tool discovery, GPU probing, downloads, model catalogue
  Media/        process runner, ffprobe, ffmpeg, source resolution
  Transcription/ transcript model, Whisper driver, SRT/VTT parsing
  Analysis/     paragraph grouping, llama-server session, Qwen planner
  Framing/      subject detection, crop path planning
  Rendering/    caption styles, ASS generation, renderer, preview
  Projects/     settings, project state, pipeline orchestration
src/ReelsMagician.App/         Avalonia front end, Windows and Wayland
tests/ReelsMagician.SmokeTests/
packaging/linux/               the AppImage: desktop entry, icons, licences, build and check scripts
docs/                          system requirements and architecture decisions
tools/make-speech-video.ps1 generates a narrated 4K test video (Windows: it uses the Windows speech engine)
```

## Tests

```powershell
# Logic checks, plus a real ffmpeg render of a synthetic 4K source
dotnet run --project tests/ReelsMagician.SmokeTests -c Release

# Logic checks only, needing no tools; this is what CI runs on Windows and Ubuntu
dotnet run --project tests/ReelsMagician.SmokeTests -c Release -- logic

# Install what "Install everything missing" installs, never yt-dlp or Deno, and assert the right backends were chosen
dotnet run --project tests/ReelsMagician.SmokeTests -c Release -- install

# Full pipeline: Whisper, Qwen, framing and render over a narrated test video
pwsh tools/make-speech-video.ps1
dotnet run --project tests/ReelsMagician.SmokeTests -c Release -- e2e

# Resolve a real compatible video link: download, 4K check, language, subtitles, progress.
# This is the only mode that fetches yt-dlp and Deno, and only when they are missing.
dotnet run --project tests/ReelsMagician.SmokeTests -c Release -- link [url]
```

The default run needs only ffmpeg. It exercises the parts most likely to break silently: the crop
expressions and their exact 9:16 geometry (square-pixel and anamorphic sources alike), the
crossfade offsets, the audio/video alignment across a cut, and the ASS output.

---

## Troubleshooting

**Linux: "ReelsMagician needs a Wayland session"** — log in to a Wayland desktop. On Ubuntu 24.04,
choose **Ubuntu** rather than **Ubuntu on Xorg** on the login screen. The app has no X11 mode.

**Linux: the AppImage does not start** — it needs FUSE 3 (`fusermount3`, in the `fuse3` package),
which Ubuntu installs by default. Without it, run it as
`./ReelsMagician-x86_64.AppImage --appimage-extract-and-run`.

**Linux: "no driver for this GPU"** — install Mesa's Vulkan drivers (`mesa-vulkan-drivers`), or
the NVIDIA driver on an NVIDIA card. Without one, Qwen runs on the CPU, which works but is much
slower.

**"Vulkan loader present but no driver found"** — update your graphics driver. Without Vulkan (or,
on an NVIDIA card, CUDA) the app falls back to the CPU llama.cpp build, which works but is much
slower.

**llama.cpp shows Vulkan on an NVIDIA card** — either the driver is too old for the CUDA builds,
or llama.cpp was installed before the app picked CUDA. Update the driver, delete
`%LOCALAPPDATA%\ReelsMagician\tools\llama`, and press **Install everything missing**.

**Analysis is slow or runs out of memory** — pick Qwen3 4B on the Setup tab, or lower the context
size in `settings.json`.

**The reel picked a dull stretch** — check the topic on the Source tab first; a wrong topic
misdirects the scoring. Otherwise choose a different proposal on the Reel tab, or re-analyse.

**The crop wanders** — expected on footage with no clear subject, where it follows visual energy.
Raise `deadzoneFraction` or `smoothingSeconds` in the framing options to calm it down.

**Captions are in the wrong font** — the font must be installed on this machine; libass falls back
silently otherwise. The dropdown lists only installed fonts.

---

## Supporting ReelsMagician

ReelsMagician is free and open-source software, made in spare time and kept going by goodwill and
coffee. If it saves you time, please support its development on
[Buy Me a Coffee](https://buymeacoffee.com/idct) or
[GitHub Sponsors](https://github.com/sponsors/ideaconnect?frequency=one-time). The bar at the
bottom of the app's window links to both.

## Licence and third-party components

ReelsMagician is released under the [BSD 3-Clause License](LICENSE).

The repository contains no third-party binaries and no model weights. The app downloads ffmpeg
(GPL build), whisper.cpp, llama.cpp, and model weights from Whisper, Qwen and OpenCV from their
original publishers on first run. It downloads yt-dlp and Deno only if you agree to it. Each stays
under its own licence.
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) lists every component, its licence and its
source; read it before redistributing a build.
