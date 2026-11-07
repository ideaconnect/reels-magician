# Third-party components

ReelsMagician's own source code is licensed under the BSD 3-Clause License (see `LICENSE`).

This repository contains **no third-party binaries and no model weights**. Everything below is
fetched from its original publisher, either by NuGet at build time or by the app itself on the
user's machine: when they press **Install everything missing** or, for the optional link tools,
only after they agree to the download. Each component stays under its own licence, which you accept
from its publisher when it is downloaded.

## Restored by NuGet at build time

| Package | Licence | Source |
|---|---|---|
| OpenCvSharp4 | Apache-2.0 | https://github.com/shimat/opencvsharp |
| OpenCvSharp4.runtime.win (native OpenCV build) | Apache-2.0; its `opencv_videoio_ffmpeg` DLL is OpenCV's LGPL-2.1 build of FFmpeg | https://github.com/shimat/opencvsharp |

## Downloaded by the app at run time

Installed into `%LOCALAPPDATA%\ReelsMagician`, never into this repository. A tool already on the
user's PATH (ffmpeg, most likely) is reused instead of downloaded.

| Component | Licence | Source |
|---|---|---|
| ffmpeg + ffprobe (BtbN `win64-gpl` build) | GPL-3.0 (the build includes GPL-only libraries such as libx264) | https://github.com/BtbN/FFmpeg-Builds |
| whisper.cpp | MIT | https://github.com/ggml-org/whisper.cpp |
| llama.cpp (and, on NVIDIA, the CUDA runtime archive llama.cpp publishes next to it) | MIT; the CUDA runtime libraries are under NVIDIA's CUDA EULA | https://github.com/ggml-org/llama.cpp |
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
| yt-dlp | Unlicense | https://github.com/yt-dlp/yt-dlp |
| Deno | MIT | https://github.com/denoland/deno |

ReelsMagician runs ffmpeg, yt-dlp, whisper.cpp and llama.cpp as separate processes; it does not
link against them.

## If you redistribute a build

A build of the app ships the OpenCvSharp native runtime, so include its Apache-2.0 notice and the
LGPL-2.1 notice for `opencv_videoio_ffmpeg`. If you
bundle any of the run-time components above instead of letting the app download them, you take on
their licence terms yourself: for the GPL ffmpeg build, that includes the obligation to provide
its corresponding source.
