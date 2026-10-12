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

mkdir -p "$STAGE/src"
rsync -a --delete --exclude '.build' --exclude '*.xcodeproj' --exclude '.swiftpm' "$ROOT/" "$STAGE/src/"
cd "$STAGE/src"
xcodegen generate --quiet

xcodebuild -project StickerSense.xcodeproj -scheme StickerSense -configuration "$CONFIG" \
  -derivedDataPath "$DERIVED" -destination 'platform=macOS' \
  CODE_SIGNING_ALLOWED=NO build -quiet

APP="$DERIVED/Build/Products/$CONFIG/StickerSense.app"
mkdir -p "$INSTALL_DIR"
pkill -x StickerSense 2>/dev/null || true
rm -rf "$INSTALL_DIR/StickerSense.app"
ditto "$APP" "$INSTALL_DIR/StickerSense.app"
xattr -cr "$INSTALL_DIR/StickerSense.app"

codesign --force --deep --options runtime --timestamp=none \
  --entitlements Mac/StickerSense.entitlements \
  --sign "$IDENTITY" "$INSTALL_DIR/StickerSense.app"
codesign --verify --strict "$INSTALL_DIR/StickerSense.app"
echo "Installed $INSTALL_DIR/StickerSense.app (signed with: $IDENTITY)"

if [[ "${1:-}" != "--no-open" ]]; then
  open "$INSTALL_DIR/StickerSense.app"
fi
