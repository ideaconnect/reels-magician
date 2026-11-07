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
