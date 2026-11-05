#!/usr/bin/env bash
# Builds the desktop app, with the reviewer CLI beside it, into a Velopack release for one runtime: an installer, a
# portable zip and the update package, plus a disk image on macOS, in artifacts/releases/<rid>. Run it on macOS for osx-* and on Windows (Git Bash)
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
  # Full packages only. Without this, an older package left in the output folder makes vpk build a delta, which
  # needs zstd on macOS and isn't what the release ships anyway (each CI job starts with an empty folder).
  --delta None
)
if [[ -n "${RELEASE_NOTES:-}" ]]; then
  args+=(--releaseNotes "$RELEASE_NOTES")
fi

case "$rid" in
  osx-*)
    args+=(--mainExe Reviewer.Desktop --bundleId com.github.tez-szymon.reviewer
      --icon "$root/src/Reviewer.Desktop/Assets/app.icns")
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
      # Signed ad hoc ("-"), so the bundle's signature is at least whole. Unsigned, only the executable carries the
      # linker's ad-hoc signature, the bundle doesn't match it, and a downloaded copy is reported as damaged with no
      # way past it. Ad hoc, Gatekeeper only can't verify the developer, and Open Anyway gets past that.
      args+=(--signAppIdentity -)
      echo "warning: MACOS_SIGN_APP_IDENTITY isn't set; the app is signed ad hoc and not notarized, so Gatekeeper asks before opening it." >&2
    fi
    ;;
  win-*)
    args+=(--mainExe Reviewer.Desktop.exe --shortcuts StartMenuRoot
      --icon "$root/src/Reviewer.Desktop/Assets/app.ico")
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

# macOS also gets a disk image: the same app as the portable zip, beside an Applications link to drag it onto. The app
# inside carries Velopack's updater, so a copy installed this way updates like one from the .pkg. vpk doesn't make
# disk images, so this isn't in assets.<rid>.json, and the release workflow uploads it separately.
if [[ "$rid" == osx-* ]]; then
  dmg="$releases/Reviewer-$rid.dmg"
  stage=$(mktemp -d)
  trap 'rm -rf "$stage"' EXIT
  # ditto keeps the bundle's symlinks, extended attributes and (when signed) its stapled ticket intact.
  ditto -x -k "$releases/Reviewer-$rid-Portable.zip" "$stage"
  ln -s /Applications "$stage/Applications"
  cp "$root/src/Reviewer.Desktop/Assets/app.icns" "$stage/.VolumeIcon.icns"

  # Built writable first, so the volume can be flagged to show .VolumeIcon.icns (the flag lives on the volume's root,
  # which -srcfolder doesn't copy), then compressed.
  rw="$stage.rw.dmg"
  hdiutil create -volname "Reviewer" -srcfolder "$stage" -fs HFS+ -format UDRW -ov "$rw"
  mnt=$(mktemp -d)
  hdiutil attach -nobrowse -noautoopen -mountpoint "$mnt" "$rw" >/dev/null
  SetFile -a C "$mnt" || echo "warning: SetFile failed; the disk image gets the default icon." >&2
  hdiutil detach -quiet "$mnt"
  rm -f "$dmg"
  hdiutil convert "$rw" -format ULFO -o "$dmg"
  rm -f "$rw"

  if [[ -n "${MACOS_SIGN_APP_IDENTITY:-}" ]]; then
    codesign --force --timestamp --sign "$MACOS_SIGN_APP_IDENTITY" ${MACOS_KEYCHAIN:+--keychain "$MACOS_KEYCHAIN"} "$dmg"
    xcrun notarytool submit "$dmg" --keychain-profile "$MACOS_NOTARY_PROFILE" \
      ${MACOS_KEYCHAIN:+--keychain "$MACOS_KEYCHAIN"} --wait
    xcrun stapler staple "$dmg"
  fi
  echo "Disk image: $dmg"
fi
