#!/bin/zsh
# Publishes a release for Mac and Windows from this Mac: runs the tests, builds and signs the Mac
# app, builds the Windows zips on a Windows PC over SSH, tags, pushes, uploads everything to
# GitHub Releases, and points the Homebrew cask at the new Mac zip.
#
#   scripts/release.sh 1.2.0              release v1.2.0
#   scripts/release.sh 1.2.0 --dry-run    build everything and print the notes; publish nothing
#
# Environment:
#   STICKERSENSE_SIGN_IDENTITY   Mac signing certificate (name or SHA-1). Required.
#   STICKERSENSE_WINDOWS_HOST    user@host of a Windows PC with OpenSSH Server, for the Windows zips.
#                                Required unless STICKERSENSE_SKIP_WINDOWS=1.
#   STICKERSENSE_DOTNET          dotnet command for the Windows core tests on this Mac (default: dotnet).
#
# Needs the GitHub CLI, signed in (gh auth login).
set -euo pipefail
ROOT="${0:A:h:h}"
cd "$ROOT"

version="${1:?Usage: scripts/release.sh <version> [--dry-run]}"
version="${version#v}"
dry_run="${2:-}"
fail() { echo "$1" >&2; exit 1; }
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "Use a version like 1.2.3."
[[ -z "$(git status --porcelain)" ]] || fail "Commit or stash your changes first."
[[ "$(git branch --show-current)" == main ]] || fail "Switch to main first."
git rev-parse -q --verify "refs/tags/v$version" >/dev/null && fail "v$version already exists."
gh auth status >/dev/null 2>&1 || fail "Sign in to GitHub first: gh auth login"
[[ -n "${STICKERSENSE_SIGN_IDENTITY:-}" ]] || fail "Set STICKERSENSE_SIGN_IDENTITY to your Mac signing certificate."
if [[ "${STICKERSENSE_SKIP_WINDOWS:-}" != 1 && -z "${STICKERSENSE_WINDOWS_HOST:-}" ]]; then
  fail "Set STICKERSENSE_WINDOWS_HOST (user@host of a Windows PC) or STICKERSENSE_SKIP_WINDOWS=1."
fi

OUT="${STICKERSENSE_RELEASE_DIR:-$HOME/Library/Caches/StickerSense-release}"
rm -rf "$OUT" && mkdir -p "$OUT"

# Tests, outside the (possibly iCloud-synced) repo.
(cd apple/StickerKit && swift test --scratch-path "$OUT/test-build" -q)
dotnet_cmd="${STICKERSENSE_DOTNET:-dotnet}"
if command -v "$dotnet_cmd" >/dev/null; then
  "$dotnet_cmd" test windows/tests/StickerSense.Core.Tests --artifacts-path "$OUT/dotnet" --nologo -v q
else
  echo "No dotnet here; the Windows core tests run on the Windows PC during packaging instead."
fi

# Mac.
STICKERSENSE_VERSION="$version" STICKERSENSE_CONFIG=Release STICKERSENSE_INSTALL_DIR="$OUT" \
  apple/scripts/build-mac.sh --no-open
ditto -c -k --keepParent "$OUT/StickerSense.app" "$OUT/StickerSense-Mac.zip"
mac_sum="$(shasum -a 256 "$OUT/StickerSense-Mac.zip" | cut -d ' ' -f 1)"
assets=("$OUT/StickerSense-Mac.zip")

# Windows.
win_sums=""
if [[ "${STICKERSENSE_SKIP_WINDOWS:-}" != 1 ]]; then
  windows/scripts/build-release-remote.sh "$STICKERSENSE_WINDOWS_HOST" "$OUT"
  for zip in StickerSense-Windows-x64.zip StickerSense-Windows-x64-app-only.zip; do
    size=$(stat -f %z "$OUT/$zip")
    (( size < 2000000000 )) || fail "$zip is over GitHub's 2 GB limit."
    assets+=("$OUT/$zip")
    win_sums+="- \`$zip\`: \`$(shasum -a 256 "$OUT/$zip" | cut -d ' ' -f 1)\`
"
  done
fi

notes="StickerSense describes stickers and GIFs out loud for screen reader users, on your own computer.

## Mac

Install with [Homebrew](https://brew.sh):

\`\`\`
brew install --cask pranav435/tap/stickersense
\`\`\`

Or download **StickerSense-Mac.zip**, unzip it, and move StickerSense to Applications.

StickerSense isn't notarized yet, so macOS blocks the first launch. Open it once, then go to System Settings › Privacy & Security and choose **Open Anyway**, or run:

\`\`\`
xattr -dr com.apple.quarantine /Applications/StickerSense.app
\`\`\`

Requires macOS 27 or later on Apple silicon, with Apple Intelligence turned on. Made for VoiceOver; the Setup window walks you through the permissions.

## Windows

1. Download **StickerSense-Windows-x64.zip** (about 1.5 GB: the app and its on-device model, so it works as soon as it's unzipped). On a slow connection, download **StickerSense-Windows-x64-app-only.zip** instead; Setup then downloads the model (1.4 GB).
2. Extract it: select the zip in File Explorer, open its context menu with the Applications key or Shift+F10, and choose Extract All. A folder in your user folder, such as Apps\\StickerSense, works well.
3. Run StickerSense.exe. It isn't code-signed yet, so Windows may say \"Windows protected your PC\": choose More info, then Run anyway.
4. The Setup window checks your screen reader (NVDA, JAWS or Narrator) and the model, and has a practice sticker.

Requires Windows 10 (1809 or later) or Windows 11, 64-bit, with 8 GB of memory. On a processor without a graphics card, a new sticker takes about 3 to 4 seconds. The shortcuts use Control Alt Shift: D describes, S opens the picker, H the history, A turns automatic descriptions on or off. Typing two colons in a chat opens the picker too.

The iPhone and iPad version is source-only for now; see the README.

Free, no ads, no tracking. If it saved you from answering a crying sticker with \"lol\", [buy its developer a coffee](https://paypal.me/theblindiephoenix).

## Checksums (SHA-256)

- \`StickerSense-Mac.zip\`: \`$mac_sum\`
$win_sums"

if [[ "$dry_run" == "--dry-run" ]]; then
  printf 'Dry run. Built:\n'; ls -la "${assets[@]}"
  printf '\nA real run tags v%s, pushes main and the tag, publishes these files and updates the cask, with these notes:\n\n%s\n' "$version" "$notes"
  exit 0
fi

git tag -a "v$version" -m "StickerSense $version"
git push origin main "v$version"
# --notes goes above GitHub's generated changelog.
gh release create "v$version" "${assets[@]}" --verify-tag --title "StickerSense $version" --notes "$notes" --generate-notes

tap="$(mktemp -d)"
gh repo clone "${TAP_REPO:-Pranav435/homebrew-tap}" "$tap" -- -q
cat > "$tap/Casks/stickersense.rb" <<EOF
cask "stickersense" do
  version "$version"
  sha256 "$mac_sum"

  url "https://github.com/Pranav435/stickersense/releases/download/v#{version}/StickerSense-Mac.zip"
  name "StickerSense"
  desc "Describes chat stickers and GIFs out loud for screen reader users"
  homepage "https://github.com/Pranav435/stickersense"

  livecheck do
    url :url
    strategy :github_latest
  end

  depends_on arch: :arm64
  depends_on macos: :golden_gate

  app "StickerSense.app"

  uninstall quit: "com.pranavsavla.StickerSense"

  zap trash: [
    "~/Library/Application Support/StickerSense",
    "~/Library/Logs/StickerSense.log",
    "~/Library/Preferences/com.pranavsavla.StickerSense.plist",
  ]

  caveats <<~EOS
    StickerSense isn't notarized yet, so macOS blocks the first launch.
    Open it once, then go to System Settings > Privacy & Security and choose Open Anyway, or run:
      xattr -dr com.apple.quarantine #{appdir}/StickerSense.app
  EOS
end
EOF
git -C "$tap" add -A
git -C "$tap" commit -qm "StickerSense $version"
git -C "$tap" push -q
echo "Released v$version for Mac and Windows, and updated the Homebrew cask."
