# Releasing

A release is a version tag. Pushing it runs `.github/workflows/release.yml`, which tests, builds the installers and update packages for `osx-arm64`, `osx-x64` and `win-x64`, and publishes them as one GitHub release. Installed apps find it within 6 hours, or when they next start.

```bash
git tag -a v0.4.0 -m "What changed, in markdown"   # the tag's message is the release notes
git push origin v0.4.0
```

A tag with a suffix, such as `v0.4.0-beta.1`, is published as a pre-release, which installed apps don't update to. Install it by hand to try it.

The release has, per runtime:

| File | |
|---|---|
| `Reviewer-<rid>-Setup.pkg` | macOS installer |
| `Reviewer-win-x64-Setup.exe` | Windows installer, per user, no admin rights |
| `Reviewer-<rid>-Portable.zip` | The app without an installer |
| `Reviewer-<version>-<rid>-full.nupkg`, `releases.<rid>.json`, `assets.<rid>.json`, `RELEASES-<rid>` | What the update check reads; don't remove them |

## Signing

Without these secrets the packages are built unsigned: macOS Gatekeeper refuses to open the app (right-click → Open gets past it), and Windows SmartScreen warns. Set them under the repository's Settings → Secrets and variables → Actions.

**macOS** needs an Apple Developer Program membership.

| Secret | |
|---|---|
| `MACOS_CERTIFICATES_P12` | A `.p12` with both the *Developer ID Application* and *Developer ID Installer* certificates and their keys, base64-encoded (`base64 -i certs.p12 \| pbcopy`) |
| `MACOS_CERTIFICATES_PASSWORD` | The `.p12`'s password |
| `MACOS_SIGN_APP_IDENTITY` | `Developer ID Application: Name (TEAMID)` |
| `MACOS_SIGN_INSTALL_IDENTITY` | `Developer ID Installer: Name (TEAMID)` |
| `APPLE_ID`, `APPLE_TEAM_ID` | The Apple ID and team that notarize |
| `APPLE_APP_PASSWORD` | An app-specific password for that Apple ID (appleid.apple.com → Sign-In and Security) |

The workflow puts the certificates in a temporary keychain, stores the notarization credentials there as the `reviewer` profile, and `vpk` signs the bundle (the CLI inside it too) with the hardened runtime, notarizes it, and staples the ticket.

**Windows** takes one secret, `WINDOWS_SIGN_TEMPLATE`: the command that signs one file, with `{{file}}` where the file goes. Code-signing keys now have to live on hardware or in a cloud service, so a `.pfx` in a secret is rarely an option. With Azure Trusted Signing, for example:

```text
signtool sign /v /fd SHA256 /tr http://timestamp.acs.microsoft.com /td SHA256 /dlib Azure.CodeSigning.Dlib.dll /dmdf metadata.json {{file}}
```

That also needs the dlib, `metadata.json` and an Azure sign-in in the job before the Package step.

## Locally

`eng/package.sh <version> <rid>` builds the same packages into `artifacts/releases/<rid>`, signing when the variables it lists are set. It runs on macOS for `osx-*` and on Windows (Git Bash) for `win-*`.

To try an update before publishing it, install one version, package a higher one, and point the app at its folder:

```bash
eng/package.sh 0.4.1 osx-arm64
# open doesn't pass the environment on, so start the executable itself
REVIEWER_UPDATE_URL="$PWD/artifacts/releases/osx-arm64" /Applications/Reviewer.app/Contents/MacOS/Reviewer.Desktop
```
