# Third-party components

ReelsMagician's own source code is licensed under the BSD 3-Clause License (see `LICENSE`).

This repository contains **no third-party binaries and no model weights**. Everything below is
fetched from its original publisher. NuGet restores the libraries when the app is built, the
release packages add the runtimes the app needs to start, and the app itself downloads its tools
and models on the user's machine: when they press **Install everything missing** or, for the
optional link tools, only after they agree to the download. Each component stays under its own
licence, which you accept from its publisher when it is downloaded.

## Restored by NuGet at build time

Every build of the app includes these.

| Package | Licence | Source |
|---|---|---|
| Avalonia, Avalonia.Skia, Avalonia.HarfBuzz, Avalonia.Win32, Avalonia.Wayland and Avalonia.Themes.Fluent, with Avalonia.FreeDesktop and Avalonia.Remote.Protocol (the user interface) | MIT | https://github.com/AvaloniaUI/Avalonia |
| NWayland (Avalonia's Wayland bindings) | MIT | https://github.com/kekekeks/NWayland |
| MicroCom.Runtime (used by Avalonia) | MIT | https://github.com/kekekeks/MicroCom |
| Tmds.DBus.Protocol (used by Avalonia on Linux) | MIT | https://github.com/tmds/Tmds.DBus |
| SkiaSharp, with its native Skia library (draws the interface) | MIT; Skia itself is BSD-3-Clause, and the native library carries further notices of its own | https://github.com/mono/SkiaSharp |
| HarfBuzzSharp, with its native HarfBuzz library (shapes text) | MIT; HarfBuzz itself is under the "Old MIT" licence | https://github.com/mono/SkiaSharp |
| Avalonia.Angle.Windows.Natives, a build of ANGLE (Windows only) | BSD-3-Clause | https://github.com/AvaloniaUI/angle |
| OpenCvSharp4 | Apache-2.0 | https://github.com/shimat/opencvsharp |
| OpenCvSharp4.runtime.win (native OpenCV build for Windows) | Apache-2.0; its `opencv_videoio_ffmpeg` DLL is OpenCV's LGPL-2.1 build of FFmpeg | https://github.com/shimat/opencvsharp |
| OpenCvSharp4.official.runtime.linux-x64 (native OpenCV build for Linux) | Apache-2.0; the native library statically links FFmpeg (LGPL-2.1) and Tesseract (Apache-2.0) | https://github.com/shimat/opencvsharp |
| SharpCompress (unpacks the `.tar.xz` archives Linux tools come in) | MIT | https://github.com/adamhathcock/sharpcompress |

## Added by the release packages

Both packages are self-contained, so nothing else needs installing to start the app.

| Component | Licence | Source |
|---|---|---|
| The .NET runtime (the Windows zip and the AppImage) | MIT, with third-party notices of its own | https://github.com/dotnet/runtime |
| The AppImage type2 runtime, which mounts the image and starts the app (the AppImage only) | MIT; it statically links musl (MIT), libfuse (LGPL-2.1), squashfuse (BSD-2-Clause), zstd (BSD-3-Clause or GPL-2.0) and zlib (zlib licence) | https://github.com/AppImage/type2-runtime |

## Downloaded by the app at run time

Installed into `%LOCALAPPDATA%\ReelsMagician` on Windows and `~/.local/share/ReelsMagician` on
Linux, never into this repository. A tool already on the
user's PATH (ffmpeg, most likely) is reused instead of downloaded.

| Component | Licence | Source |
|---|---|---|
| ffmpeg + ffprobe (BtbN's `win64-gpl` build on Windows, `linux64-gpl` on Linux) | GPL-3.0 (the build includes GPL-only libraries such as libx264) | https://github.com/BtbN/FFmpeg-Builds |
| whisper.cpp (on Windows with an NVIDIA card it can drive, its CUDA build, which includes NVIDIA's CUDA runtime libraries) | MIT; the CUDA runtime libraries are under NVIDIA's CUDA EULA | https://github.com/ggml-org/whisper.cpp |
| llama.cpp (and, on Windows with an NVIDIA card it can drive, the CUDA runtime archive llama.cpp publishes next to its CUDA build) | MIT; the CUDA runtime libraries are under NVIDIA's CUDA EULA | https://github.com/ggml-org/llama.cpp |
| Whisper models (ggml conversions) | MIT | https://huggingface.co/ggerganov/whisper.cpp |
| Qwen3 4B / 8B GGUF models | Apache-2.0 | https://huggingface.co/Qwen |
| Face detector prototxt | Apache-2.0 | https://github.com/opencv/opencv (`samples/dnn/face_detector`) |
| Face detector weights (`res10_300x300_ssd_iter_140000.caffemodel`) | Published by the OpenCV project for its DNN samples; the source branch carries no separate licence file | https://github.com/opencv/opencv_3rdparty (`dnn_samples_face_detector_20170830`) |

## Downloaded only if the user agrees

Optional tools for loading a video from a link. They do not come with the app and are not part of
the default install: the app asks first, and installs them into the same folder only on a yes. A
yt-dlp already on the user's PATH is used without asking.

| Component | Licence | Source |
|---|---|---|
| yt-dlp (its standalone `yt-dlp.exe` and `yt-dlp_linux` builds) | yt-dlp's own code is Unlicense, but these PyInstaller builds include GPLv3+ code, so each build as a whole is GPLv3+; its `THIRD_PARTY_LICENSES.txt` lists the rest | https://github.com/yt-dlp/yt-dlp |
| Deno | MIT | https://github.com/denoland/deno |

ReelsMagician runs ffmpeg, yt-dlp, whisper.cpp and llama.cpp as separate processes; it does not
link against them.

## If you redistribute a build

A build of the app ships every library restored by NuGet above, and a release package adds the
runtimes listed with them. Include all of their notices, among them the LGPL-2.1 notices for the
FFmpeg inside the OpenCV runtime (`opencv_videoio_ffmpeg` on Windows, linked into the native
library on Linux) and, in the AppImage, for libfuse. If you bundle any of the run-time components
above instead of letting the app download them, you take on
their licence terms yourself: for the GPL ffmpeg build, that includes the obligation to provide
its corresponding source.

`dotnet publish` puts these notices next to the executable, so every release package carries
them: `LICENSE`, this file, and a `licenses` folder. That folder holds the licence and notice
files each bundled package and the .NET runtime ship, and, for the packages that ship none, the
texts kept in this repository's `licenses` folder. The AppImage adds those in
`packaging/linux/licenses` for its runtime. Keep all of them when you pass a build on.
