#!/bin/sh
# Builds the Linux release, a single AppImage, from a self-contained linux-x64 publish of the app.
#
#   dotnet publish src/ReelsMagician.App -c Release -r linux-x64 --self-contained -o out/publish
#   sh packaging/linux/build-appimage.sh out/publish ReelsMagician-x86_64.AppImage 0.2.0
#
# The image uses AppImage's current static runtime, which needs only FUSE 3 on the user's
# machine and not the libfuse2 that Ubuntu no longer installs (docs/adr/0002). It runs here
# without FUSE, so CI containers can build it. Needs curl.
set -eu

publish=$(cd "$1" && pwd)
output=$2
version=${3:-dev}
here=$(cd "$(dirname "$0")" && pwd)

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
appdir="$work/ReelsMagician.AppDir"

mkdir -p "$appdir/usr/bin" "$appdir/usr/share/applications"
cp -a "$publish"/. "$appdir/usr/bin/"
# The image adds the AppImage runtime, so the licences of what that links go in with the app's.
mkdir -p "$appdir/usr/bin/licenses"
cp -a "$here/licenses"/. "$appdir/usr/bin/licenses/"

for size in 16 24 32 48 64 128 256 512; do
  icons="$appdir/usr/share/icons/hicolor/${size}x${size}/apps"
  mkdir -p "$icons"
  cp "$here/icons/reelsmagician-$size.png" "$icons/reelsmagician.png"
done
cp "$here/icons/reelsmagician-256.png" "$appdir/reelsmagician.png"
cp "$here/ReelsMagician.desktop" "$appdir/ReelsMagician.desktop"
cp "$here/ReelsMagician.desktop" "$appdir/usr/share/applications/ReelsMagician.desktop"
cp "$here/AppRun" "$appdir/AppRun"

# NuGet unpacks package files with mode 0744 and a publish keeps it, while a publish copied over
# from Windows has no execute bits at all. Give every file the mode appimagetool expects: 0755
# for the programs and libraries, 0644 for the rest.
find "$appdir" -type d -exec chmod 0755 {} +
find "$appdir" -type f -exec chmod 0644 {} +
find "$appdir" -type f -name '*.so' -exec chmod 0755 {} +
chmod 0755 "$appdir/AppRun" "$appdir/usr/bin/ReelsMagician"
if [ -e "$appdir/usr/bin/createdump" ]; then chmod 0755 "$appdir/usr/bin/createdump"; fi

# appimagetool and the runtime it embeds, both from the AppImage project's own releases.
tools="$work/tools"
mkdir -p "$tools"
curl -fsSL -o "$tools/appimagetool" \
  https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage
curl -fsSL -o "$tools/runtime-x86_64" \
  https://github.com/AppImage/type2-runtime/releases/download/continuous/runtime-x86_64
chmod +x "$tools/appimagetool"

ARCH=x86_64 VERSION="$version" APPIMAGE_EXTRACT_AND_RUN=1 \
  "$tools/appimagetool" --no-appstream --runtime-file "$tools/runtime-x86_64" "$appdir" "$output"

echo "Built $output"
