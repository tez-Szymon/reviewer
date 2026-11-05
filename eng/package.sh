#!/usr/bin/env bash
# Builds the desktop app, with the reviewer CLI beside it, into a Velopack release for one runtime: an installer, a
# portable zip and the update package, in artifacts/releases/<rid>. Run it on macOS for osx-* and on Windows (Git Bash)
# for win-*; vpk packs only for the OS it runs on.
#
#   eng/package.sh <version> <rid>        e.g. eng/package.sh 0.4.0 osx-arm64
#
# Signing is used when its settings are in the environment, and skipped otherwise:
#   macOS    MACOS_SIGN_APP_IDENTITY      "Developer ID Application: Name (TEAMID)"
#            MACOS_SIGN_INSTALL_IDENTITY  "Developer ID Installer: Name (TEAMID)"
#            MACOS_NOTARY_PROFILE         a notarytool keychain profile (xcrun notarytool store-credentials)
#            MACOS_KEYCHAIN               the keychain holding both, if not the default one
#   Windows  WINDOWS_SIGN_TEMPLATE        a signing command with {{file}} for the file, e.g.
#                                         signtool sign /fd sha256 /tr http://timestamp.digicert.com /td sha256 /a {{file}}
#
# The release notes come from RELEASE_NOTES (a markdown file) when set.
set -euo pipefail

version=${1:?usage: eng/package.sh <version> <rid>}
rid=${2:?usage: eng/package.sh <version> <rid>}

root=$(cd "$(dirname "$0")/.." && pwd)
publish="$root/artifacts/publish/$rid"
releases="$root/artifacts/releases/$rid"

rm -rf "$publish"
# Self-contained, so neither the app nor the CLI beside it needs .NET installed. The CLI is published with the app
# (a project reference), and runs on the app's runtime files.
dotnet publish "$root/src/Reviewer.Desktop/Reviewer.Desktop.csproj" \
  --configuration Release --runtime "$rid" --self-contained \
  -p:Version="$version" \
  --output "$publish"

args=(
  pack
  --packId Reviewer
  --packTitle Reviewer
  --packAuthors "Szymon Bakunowicz"
  --packVersion "$version"
  --packDir "$publish"
  --runtime "$rid"
  # One channel per runtime, so an app only ever updates to a build for its own OS and architecture.
  --channel "$rid"
  --outputDir "$releases"
)
if [[ -n "${RELEASE_NOTES:-}" ]]; then
  args+=(--releaseNotes "$RELEASE_NOTES")
fi

case "$rid" in
  osx-*)
    args+=(--mainExe Reviewer.Desktop --bundleId com.github.tez-szymon.reviewer)
    if [[ -n "${MACOS_SIGN_APP_IDENTITY:-}" ]]; then
      args+=(
        --signAppIdentity "$MACOS_SIGN_APP_IDENTITY"
        --signInstallIdentity "${MACOS_SIGN_INSTALL_IDENTITY:?MACOS_SIGN_INSTALL_IDENTITY is needed to sign the installer}"
        --notaryProfile "${MACOS_NOTARY_PROFILE:?MACOS_NOTARY_PROFILE is needed to notarize}"
      )
      if [[ -n "${MACOS_KEYCHAIN:-}" ]]; then
        args+=(--keychain "$MACOS_KEYCHAIN")
      fi
    else
      echo "warning: MACOS_SIGN_APP_IDENTITY isn't set; the app is neither signed nor notarized, and Gatekeeper will refuse it." >&2
    fi
    ;;
  win-*)
    args+=(--mainExe Reviewer.Desktop.exe --shortcuts StartMenuRoot)
    if [[ -n "${WINDOWS_SIGN_TEMPLATE:-}" ]]; then
      args+=(--signTemplate "$WINDOWS_SIGN_TEMPLATE")
    else
      echo "warning: WINDOWS_SIGN_TEMPLATE isn't set; the app isn't signed, and SmartScreen will warn about it." >&2
    fi
    ;;
  *)
    echo "error: $rid isn't packaged; the desktop app ships for osx-* and win-* (Linux comes after the MVP)." >&2
    exit 1
    ;;
esac

cd "$root"
dotnet tool restore
dotnet vpk "${args[@]}"
