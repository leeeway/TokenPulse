#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

echo "🔨 Building TokenPulse (release)..."
swift build -c release

APP_NAME="TokenPulse"
APP_BUNDLE="$ROOT_DIR/dist/${APP_NAME}.app"
CONTENTS_DIR="$APP_BUNDLE/Contents"
MACOS_DIR="$CONTENTS_DIR/MacOS"
RESOURCES_DIR="$CONTENTS_DIR/Resources"

rm -rf "$APP_BUNDLE"
mkdir -p "$MACOS_DIR" "$RESOURCES_DIR"

cp ".build/release/TokenPulseApp" "$MACOS_DIR/$APP_NAME"
cp ".build/release/tokenpulse" "$ROOT_DIR/dist/tokenpulse"
chmod +x "$MACOS_DIR/$APP_NAME" "$ROOT_DIR/dist/tokenpulse"

cat > "$CONTENTS_DIR/Info.plist" << 'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDevelopmentRegion</key>
    <string>zh_CN</string>
    <key>CFBundleExecutable</key>
    <string>TokenPulse</string>
    <key>CFBundleIdentifier</key>
    <string>io.github.leeeway.TokenPulse</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleName</key>
    <string>TokenPulse</string>
    <key>CFBundleDisplayName</key>
    <string>TokenPulse</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>0.1.3</string>
    <key>CFBundleVersion</key>
    <string>4</string>
    <key>LSMinimumSystemVersion</key>
    <string>14.0</string>
    <key>LSUIElement</key>
    <true/>
    <key>NSHighResolutionCapable</key>
    <true/>
</dict>
</plist>
EOF

codesign --force --deep --sign - "$APP_BUNDLE" 2>/dev/null || true

echo "✅ Packaged macOS Menu Bar app at: $APP_BUNDLE"
echo "✅ Built CLI binary at: $ROOT_DIR/dist/tokenpulse"
echo "🚀 Run 'open dist/TokenPulse.app' to launch in your macOS Menu Bar!"
