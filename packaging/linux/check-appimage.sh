#!/bin/sh
# Checks a built AppImage where no desktop runs, such as a CI runner. It does not test the
# interface, which is checked on a real Wayland desktop only.
#
#   sh packaging/linux/check-appimage.sh ReelsMagician-x86_64.AppImage
#
# 1. It starts. With no Wayland session, the AppImage runtime, AppRun and the .NET host all run,
#    and the app exits with code 1 after saying it needs one. Extract-and-run needs no FUSE.
# 2. It holds what the app loads and the licences it must carry, and every file has the mode
#    appimagetool expects, with the execute bits intact.
set -eu

image=$(cd "$(dirname "$1")" && pwd)/$(basename "$1")
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
chmod +x "$image"

set +e
output=$(cd "$work" && env -u WAYLAND_DISPLAY "$image" --appimage-extract-and-run 2>&1)
status=$?
set -e
if [ "$status" -ne 1 ] || ! printf '%s\n' "$output" | grep -q 'needs a Wayland session'; then
  echo "The AppImage did not get as far as the app's Wayland check (exit code $status):" >&2
  printf '%s\n' "$output" >&2
  exit 1
fi
echo "Starts: the app ran and asked for a Wayland session."

(cd "$work" && "$image" --appimage-extract >/dev/null)
root="$work/squashfs-root"
failed=0
for file in \
  AppRun usr/bin/ReelsMagician \
  usr/bin/libSkiaSharp.so usr/bin/libHarfBuzzSharp.so usr/bin/libOpenCvSharpExtern.so \
  usr/bin/LICENSE usr/bin/THIRD-PARTY-NOTICES.md \
  usr/bin/licenses/Avalonia/licence.md usr/bin/licenses/OpenCvSharp/LICENSE \
  usr/bin/licenses/FFmpeg/COPYING.LGPLv2.1 \
  usr/bin/licenses/SkiaSharp.NativeAssets.Linux/THIRD-PARTY-NOTICES.txt \
  usr/bin/licenses/Microsoft.NETCore.App.Runtime.linux-x64/THIRD-PARTY-NOTICES.TXT \
  usr/bin/licenses/AppImage-type2-runtime/LICENSE usr/bin/licenses/libfuse/LGPL2.txt
do
  if [ ! -e "$root/$file" ]; then echo "Missing from the AppImage: $file" >&2; failed=1; fi
done
for file in AppRun usr/bin/ReelsMagician; do
  if [ -e "$root/$file" ] && [ ! -x "$root/$file" ]; then echo "Not executable in the AppImage: $file" >&2; failed=1; fi
done
# Any other mode makes appimagetool warn, and a future appimagetool may refuse it.
odd=$(cd "$root" && find . -type f ! -perm 0644 ! -perm 0755)
if [ -n "$odd" ]; then
  echo "Files in the AppImage with a mode other than 0644 or 0755:" >&2
  printf '%s\n' "$odd" | head -20 >&2
  failed=1
fi
[ "$failed" -eq 0 ] || exit 1
echo "Holds the native libraries and the licences, with the expected modes."
