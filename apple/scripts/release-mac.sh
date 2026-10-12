#!/bin/zsh
# Publishes a Mac release from this Mac: runs the tests, builds and signs the app, zips it,
# tags, pushes, uploads to GitHub Releases, and points the Homebrew cask at the new zip.
#
#   STICKERSENSE_SIGN_IDENTITY=<SHA-1> apple/scripts/release-mac.sh 1.2.0            release v1.2.0
#   STICKERSENSE_SIGN_IDENTITY=<SHA-1> apple/scripts/release-mac.sh 1.2.0 --dry-run  everything except tag, push, upload and cask
#
# Needs the GitHub CLI, signed in (gh auth login).
set -euo pipefail
ROOT="${0:A:h:h:h}"
cd "$ROOT"

version="${1:?Usage: apple/scripts/release-mac.sh <version> [--dry-run]}"
version="${version#v}"
fail() { echo "$1" >&2; exit 1; }
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "Use a version like 1.2.3."
[[ -z "$(git status --porcelain)" ]] || fail "Commit or stash your changes first."
[[ "$(git branch --show-current)" == main ]] || fail "Switch to main first."
git rev-parse -q --verify "refs/tags/v$version" >/dev/null && fail "v$version already exists."
gh auth status >/dev/null 2>&1 || fail "Sign in to GitHub first: gh auth login"
[[ -n "${STICKERSENSE_SIGN_IDENTITY:-}" ]] || fail "Set STICKERSENSE_SIGN_IDENTITY to your signing certificate."

OUT="${STICKERSENSE_RELEASE_DIR:-$HOME/Library/Caches/StickerSense-release}"
rm -rf "$OUT" && mkdir -p "$OUT"

# Tests run outside the (possibly iCloud-synced) repo.
(cd apple/StickerKit && swift test --scratch-path "$OUT/test-build" -q)

STICKERSENSE_VERSION="$version" STICKERSENSE_CONFIG=Release STICKERSENSE_INSTALL_DIR="$OUT" \
  apple/scripts/build-mac.sh --no-open
ditto -c -k --keepParent "$OUT/StickerSense.app" "$OUT/StickerSense.zip"
sum="$(shasum -a 256 "$OUT/StickerSense.zip" | cut -d ' ' -f 1)"

notes="Install with [Homebrew](https://brew.sh):

\`\`\`
brew install --cask pranav435/tap/stickersense
\`\`\`

Or download **StickerSense.zip**, unzip it, and move StickerSense to Applications.

StickerSense isn't notarized yet, so macOS blocks the first launch. Open it once, then go to System Settings › Privacy & Security and choose **Open Anyway**. Or run:

\`\`\`
xattr -dr com.apple.quarantine /Applications/StickerSense.app
\`\`\`

Requires macOS 27 or later on Apple silicon, with Apple Intelligence turned on. Made for VoiceOver. The Setup window walks you through the permissions it needs.

The iPhone and Windows versions are source-only for now; see the README.

Free, no ads, no tracking. If it saved you from answering a crying sticker with \"lol\", [buy its developer a coffee](https://paypal.me/theblindiephoenix).

SHA-256: \`$sum\`"

if [[ "${2:-}" == "--dry-run" ]]; then
  printf 'Dry run. Built %s (%s).\nA real run tags v%s, pushes main and the tag, publishes the zip and updates the cask, with these notes:\n\n%s\n' \
    "$OUT/StickerSense.zip" "$sum" "$version" "$notes"
  exit 0
fi

git tag -a "v$version" -m "StickerSense $version"
git push origin main "v$version"
# --notes goes above GitHub's generated changelog.
gh release create "v$version" "$OUT/StickerSense.zip" --verify-tag --title "StickerSense $version" --notes "$notes" --generate-notes

tap="$(mktemp -d)"
gh repo clone "${TAP_REPO:-Pranav435/homebrew-tap}" "$tap" -- -q
cat > "$tap/Casks/stickersense.rb" <<EOF
cask "stickersense" do
  version "$version"
  sha256 "$sum"

  url "https://github.com/Pranav435/stickersense/releases/download/v#{version}/StickerSense.zip"
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
if ! grep -q "stickersense" "$tap/README.md"; then
  cat >> "$tap/README.md" <<'EOF'

```sh
brew install --cask pranav435/tap/stickersense
```

[StickerSense](https://github.com/Pranav435/stickersense) describes chat stickers and GIFs out loud for screen reader users. The cask is updated by StickerSense's release script.
EOF
fi
git -C "$tap" add -A
git -C "$tap" commit -qm "StickerSense $version"
git -C "$tap" push -q
echo "Released v$version and updated the Homebrew cask."
