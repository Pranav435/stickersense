#!/bin/zsh
# Builds StickerSense for Mac and installs it to ~/Applications.
#
#   apple/scripts/build-mac.sh            # Release build, install, launch
#   apple/scripts/build-mac.sh --no-open  # don't launch afterwards
#
# Signing: macOS remembers permissions (Screen Recording, Accessibility, Automation) per
# signing identity. Ad-hoc builds lose them on every rebuild, so set
# STICKERSENSE_SIGN_IDENTITY to your "Apple Development" certificate (name or SHA-1) —
# `security find-identity -v -p codesigning` lists them.
#
# The sources are copied to a staging folder first: if the repo lives in an iCloud-synced
# folder (Desktop & Documents), iCloud's file coordination can stall xcodebuild indefinitely.
set -euo pipefail

ROOT="${0:A:h:h}"
STAGE="${STICKERSENSE_STAGE_DIR:-$HOME/Library/Caches/StickerSense-build}"
DERIVED="$STAGE/DerivedData"
INSTALL_DIR="${STICKERSENSE_INSTALL_DIR:-$HOME/Applications}"
CONFIG="${STICKERSENSE_CONFIG:-Release}"
IDENTITY="${STICKERSENSE_SIGN_IDENTITY:--}"

# Refuse revoked certificates: macOS treats apps signed with one as malware and offers to
# move them to the Trash. (Xcode leaves superseded certificates in the keychain.)
if [[ "$IDENTITY" != "-" ]]; then
  HASH="$(security find-identity -v -p codesigning | awk -v id="$IDENTITY" 'index($0, id) {print $2; exit}')"
  [[ -n "$HASH" ]] || { echo "Signing identity not found: $IDENTITY"; exit 1; }
  CERT_PEM="$(mktemp -t stickersense-cert)"
  security find-certificate -a -Z -p | awk -v h="$HASH" '/^SHA-1 hash:/ {keep = ($3 == h)} keep && /BEGIN CERT/,/END CERT/' > "$CERT_PEM"
  if ! security verify-cert -c "$CERT_PEM" -p codeSign -R ocsp >/dev/null 2>&1; then
    rm -f "$CERT_PEM"
    echo "Certificate $HASH is revoked or invalid — pick another with: security find-identity -v -p codesigning"
    exit 1
  fi
  rm -f "$CERT_PEM"
  IDENTITY="$HASH"
fi

command -v xcodegen >/dev/null || { echo "Install XcodeGen first: brew install xcodegen"; exit 1; }

# The sticker maker: stable-diffusion.cpp's sd-cli and its library (MIT), from a pinned release,
# checked against its SHA-256 and cut down to Apple silicon. Kept in the staging folder.
SD_RELEASE="master-964-1d0a58a"
SD_ZIP="sd-master-1d0a58a-bin-Darwin-macOS-26.6.2-arm64.zip"
SD_SHA256="45001839e925f98ecd18ef5251725f74cff874eeac4599f1136acba65b26911e"
SD_DIR="$STAGE/sd-cpp/$SD_RELEASE"
if [[ ! -x "$SD_DIR/sd-cli" || ! -f "$SD_DIR/libstable-diffusion.dylib" ]]; then
  echo "Downloading stable-diffusion.cpp $SD_RELEASE for the sticker maker"
  rm -rf "$SD_DIR" && mkdir -p "$SD_DIR"
  curl -sSL --fail -o "$SD_DIR/$SD_ZIP" "https://github.com/leejet/stable-diffusion.cpp/releases/download/$SD_RELEASE/$SD_ZIP"
  echo "$SD_SHA256  $SD_DIR/$SD_ZIP" | shasum -a 256 -c - >/dev/null || { echo "The stable-diffusion.cpp download didn't match its checksum"; exit 1; }
  unzip -q -o "$SD_DIR/$SD_ZIP" -d "$SD_DIR/universal"
  lipo "$SD_DIR/universal/sd-cli" -thin arm64 -output "$SD_DIR/sd-cli"
  lipo "$SD_DIR/universal/libstable-diffusion.dylib" -thin arm64 -output "$SD_DIR/libstable-diffusion.dylib"
  cp "$SD_DIR/universal/stable-diffusion.cpp.txt" "$SD_DIR/universal/ggml.txt" "$SD_DIR/"
fi

mkdir -p "$STAGE/src"
rsync -a --delete --exclude '.build' --exclude '*.xcodeproj' --exclude '.swiftpm' "$ROOT/" "$STAGE/src/"
cd "$STAGE/src"
xcodegen generate --quiet

# STICKERSENSE_VERSION (e.g. 1.2.0) overrides the version; the build number is the commit count.
VERSION_ARGS=()
if [[ -n "${STICKERSENSE_VERSION:-}" ]]; then
  VERSION_ARGS=(MARKETING_VERSION="$STICKERSENSE_VERSION" CURRENT_PROJECT_VERSION="$(git -C "$ROOT" rev-list --count HEAD)")
fi

xcodebuild -project StickerSense.xcodeproj -scheme StickerSense -configuration "$CONFIG" \
  -derivedDataPath "$DERIVED" -destination 'platform=macOS' \
  CODE_SIGNING_ALLOWED=NO "${VERSION_ARGS[@]}" build -quiet

APP="$DERIVED/Build/Products/$CONFIG/StickerSense.app"
mkdir -p "$INSTALL_DIR"
# Only replace the running copy when installing over it, not for release builds elsewhere.
[[ "$INSTALL_DIR" == "$HOME/Applications" ]] && { pkill -x StickerSense 2>/dev/null || true; }
rm -rf "$INSTALL_DIR/StickerSense.app"
ditto "$APP" "$INSTALL_DIR/StickerSense.app"
# sd-cli finds its library next to itself (@executable_path).
cp "$SD_DIR/sd-cli" "$SD_DIR/libstable-diffusion.dylib" "$INSTALL_DIR/StickerSense.app/Contents/MacOS/"
mkdir -p "$INSTALL_DIR/StickerSense.app/Contents/Resources/Licenses"
cp "$SD_DIR/stable-diffusion.cpp.txt" "$INSTALL_DIR/StickerSense.app/Contents/Resources/Licenses/stable-diffusion.cpp.txt"
cp "$SD_DIR/ggml.txt" "$INSTALL_DIR/StickerSense.app/Contents/Resources/Licenses/ggml.txt"
xattr -cr "$INSTALL_DIR/StickerSense.app"

# Inside out: the sticker maker's pieces first, then the app around them.
for helper in libstable-diffusion.dylib sd-cli; do
  codesign --force --options runtime --timestamp=none --sign "$IDENTITY" "$INSTALL_DIR/StickerSense.app/Contents/MacOS/$helper"
done

codesign --force --deep --options runtime --timestamp=none \
  --entitlements Mac/StickerSense.entitlements \
  --sign "$IDENTITY" "$INSTALL_DIR/StickerSense.app"
codesign --verify --strict "$INSTALL_DIR/StickerSense.app"
echo "Installed $INSTALL_DIR/StickerSense.app (signed with: $IDENTITY)"

if [[ "${1:-}" != "--no-open" ]]; then
  open "$INSTALL_DIR/StickerSense.app"
fi
