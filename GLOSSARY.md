# ReelsMagician

ReelsMagician turns a long video into a short vertical reel on the user's own machine. This glossary fixes the words we use for where the app ships and what it depends on.

## Language

### Shipping

**Target OS**:
An operating system we ship the app for.
_Avoid_: Platform

**UI toolkit**:
The library that draws the app's windows and controls.
_Avoid_: Platform, framework

**Release artifact**:
The file a person downloads to get the app.
_Avoid_: Build, package

### External programs

**Toolchain**:
The external programs the app runs to do its work, obtained separately from the app itself. Never the compiler or anything else used to build the app.
_Avoid_: Tools, dependencies

**Tool build**:
One of the downloads a Toolchain program is published as, each made for one kind of hardware: an NVIDIA graphics card, any graphics card through Vulkan, or the processor alone.
_Avoid_: Variant, flavour, backend

**Fallback build**:
A Tool build below the best one the machine could run, used because the better one couldn't be found.
_Avoid_: Downgrade

**Link tools**:
The optional part of the Toolchain needed only to load a video from a compatible video link: yt-dlp and its JavaScript runtime. Obtained only after the user agrees.
_Avoid_: Downloader, optional tools

### The Polish interface

The interface's texts live in `Strings.resx` (English) and `Strings.pl.resx` (Polish), in the app for the window and in Core for what Core shows on screen. Log lines, settings.json and the smoke tests' output stay English. These are the words the Polish texts use for the terms that recur, so a screen never says the same thing two ways. The rule on links holds in Polish too: "zgodny link do filmu", never a platform's name.

**Reel** — **rolka**:
The short vertical video the app makes.
_Avoid_: Klip, short, film pionowy

**Clip** — **klip**:
One continuous stretch of the source that a reel is cut from.
_Avoid_: Fragment, ujęcie

**Segment** — **fragment**:
A stretch of the transcript that Qwen scores and picks from, before it becomes a clip.
_Avoid_: Odcinek, akapit

**Cue** — **napis**:
One timed line of the transcript, as the Subtitles tab lists them; the transcript as a whole is **napisy**.
_Avoid_: Wpis, linia

**Caption** — **podpis**:
The text burned into the reel, styled on the Captions tab.
_Avoid_: Napis (that is a cue), tytuł

**Framing** — **kadrowanie**:
Choosing where the 1080×1920 crop sits in each frame; what it keeps in view is the **kadr**.
_Avoid_: Przycinanie, crop

**Transcript** — **transkrypcja**:
What Whisper heard, or the subtitles that came with the video.
_Avoid_: Zapis, tekst mówiony

**Render** — **render** (the step), **renderowanie** (the work):
Encoding the finished reel. The step keeps the short loanword, as "Renderowanie" would not fit the step strip at the window's minimum width.
_Avoid_: Eksport, eksportowanie
