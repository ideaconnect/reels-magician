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
# A publish copied over from Windows arrives without execute bits.
chmod +x "$appdir/usr/bin/ReelsMagician"

for size in 16 24 32 48 64 128 256 512; do
  icons="$appdir/usr/share/icons/hicolor/${size}x${size}/apps"
  mkdir -p "$icons"
  cp "$here/icons/reelsmagician-$size.png" "$icons/reelsmagician.png"
done
cp "$here/icons/reelsmagician-256.png" "$appdir/reelsmagician.png"
cp "$here/ReelsMagician.desktop" "$appdir/ReelsMagician.desktop"
cp "$here/ReelsMagician.desktop" "$appdir/usr/share/applications/ReelsMagician.desktop"
cp "$here/AppRun" "$appdir/AppRun"
chmod +x "$appdir/AppRun"

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
