#!/bin/bash
set -euo pipefail

project_dir="$(cd "$(dirname "$0")" && pwd)"
repository_dir="$(cd "$project_dir/.." && pwd)"
output_dir="${1:-$repository_dir/dist/macos}"
cd "$project_dir"
swift test --arch arm64
swift build -c release --arch arm64
binary_dir="$(swift build -c release --arch arm64 --show-bin-path)"
stage_dir="$(mktemp -d "$project_dir/.build/package.XXXXXX")"
bundle="$stage_dir/Actions Ring.app"
mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources" "$output_dir"
cp "$binary_dir/ActionsRing" "$bundle/Contents/MacOS/ActionsRing"
cp "$project_dir/Info.plist" "$bundle/Contents/Info.plist"
plutil -lint "$bundle/Contents/Info.plist"

iconset="$stage_dir/AppIcon.iconset"
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
    sips -z "$size" "$size" "$repository_dir/src/ActionsRing.App/Assets/ActionsRingLogo.png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
    retina=$((size * 2))
    sips -z "$retina" "$retina" "$repository_dir/src/ActionsRing.App/Assets/ActionsRingLogo.png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$bundle/Contents/Resources/AppIcon.icns"
codesign --force --sign - --options runtime --identifier app.actionsring.macos "$bundle"
codesign --verify --deep --strict "$bundle"
test "$(lipo -archs "$bundle/Contents/MacOS/ActionsRing")" = arm64
"$bundle/Contents/MacOS/ActionsRing" --smoke-test "$output_dir/visual-qa"
ditto -c -k --sequesterRsrc --keepParent "$bundle" "$output_dir/ActionsRing-macOS-AppleSilicon.zip"
cp "$project_dir/README.md" "$output_dir/README-macOS.md"
(cd "$output_dir" && shasum -a 256 ActionsRing-macOS-AppleSilicon.zip > SHA256SUMS-macOS.txt)
printf 'Package: %s\n' "$output_dir/ActionsRing-macOS-AppleSilicon.zip"
