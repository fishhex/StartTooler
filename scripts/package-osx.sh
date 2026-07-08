#!/usr/bin/env bash
set -euo pipefail

VERSION="${1:?usage: package-osx.sh <version> <publish-dir> <icns-path>}"
PUBLISH_DIR="${2:?}"
ICNS_SRC="${3:?}"

APP="dist/StartTooler.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
mkdir -p "$APP/Contents/Resources"

cp "$PUBLISH_DIR/StartTooler" "$APP/Contents/MacOS/"
cp "$ICNS_SRC" "$APP/Contents/Resources/App.icns"

cat > "$APP/Contents/Info.plist" << EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>
    <string>StartTooler</string>
    <key>CFBundleIconFile</key>
    <string>App.icns</string>
    <key>CFBundleIdentifier</key>
    <string>com.starttooler.app</string>
    <key>CFBundleName</key>
    <string>StartTooler</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>${VERSION}</string>
    <key>CFBundleVersion</key>
    <string>${VERSION}</string>
    <key>LSMinimumSystemVersion</key>
    <string>10.13</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSPrincipalClass</key>
    <string>NSApplication</string>
    <key>NSHumanReadableCopyright</key>
    <string>Copyright © 2026 fishhex. MIT License.</string>
</dict>
</plist>
EOF

ARCHIVE="StartTooler-darwin-arm64-v${VERSION}.tar.gz"
tar -czf "$ARCHIVE" -C dist StartTooler.app

echo "Packaged: $ARCHIVE"
