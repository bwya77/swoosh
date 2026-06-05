# Swoosh

[![Build & Release](https://github.com/bwya77/swoosh/actions/workflows/release.yml/badge.svg)](https://github.com/bwya77/swoosh/actions/workflows/release.yml)
[![Tests](https://github.com/bwya77/swoosh/actions/workflows/tests.yml/badge.svg)](https://github.com/bwya77/swoosh/actions/workflows/tests.yml)
[![CodeQL](https://github.com/bwya77/swoosh/actions/workflows/codeql.yml/badge.svg)](https://github.com/bwya77/swoosh/actions/workflows/codeql.yml)
[![OpenSSF Scorecard](https://api.securityscorecards.dev/projects/github.com/bwya77/swoosh/badge)](https://securityscorecards.dev/viewer/?uri=github.com/bwya77/swoosh)
[![Latest release](https://img.shields.io/github/v/release/bwya77/swoosh?sort=semver)](https://github.com/bwya77/swoosh/releases/latest)
[![Downloads](https://badgen.net/github/assets-dl/bwya77/swoosh)](https://github.com/bwya77/swoosh/releases)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows%2010%20%2F%2011-0078D6)

Swoosh brings macOS Swish style window management to Windows. Hover your cursor
over a window's titlebar, then use simple Precision Touchpad gestures to snap,
move, and resize it. Inspired by the macOS app
[Swish](https://highlyopinionated.co/swish/).

## Install

Download the latest build from the
[Releases page](https://github.com/bwya77/swoosh/releases/latest).

**Installer (recommended).** Run the signed installer. It installs to Program
Files, adds a Start Menu shortcut, can start with Windows, and updates itself.

- `SwooshSetup-<version>-win-arm64.exe` for ARM64 devices (Surface Pro X,
  Surface Pro 9 5G, and similar)
- `SwooshSetup-<version>-win-x64.exe` for everything else

**Portable (no install).** Unzip and run `Swoosh.exe`.

- `Swoosh-<version>-win-arm64.zip` or `Swoosh-<version>-win-x64.zip`

You do not need to install .NET. The runtime is bundled. Everything is code
signed, and each release ships `SHA256SUMS.txt` plus a signed build provenance
bundle. See [Security and privacy](#security-and-privacy).

## Gestures

Every gesture starts with your cursor over a window's titlebar. A preview shows
where the window will go. Lift your fingers to drop it there, or press Esc to
cancel.

Two-finger swipe:

| Swipe          | Action              |
| -------------- | ------------------- |
| Left or Right  | Snap to that half   |
| Up             | Maximize            |
| Down           | Minimize            |
| Diagonal       | Snap to that quarter|

A few more:

- Pinch out to go fullscreen, pinch in to restore.
- Hold Shift (configurable) while swiping to snap to a 3x3 grid of thirds.
- Hold two fingers, then swipe to move the window to another monitor or virtual
  desktop.
- Hold Alt (configurable) and swipe to send the window to the next display.
- Five-finger drag to free move the window with fine control.
- Five-finger tap to center the window on its monitor.

Swooshing a window always brings it to the front. For exactly how each gesture
works, see the [deep dive](docs/deep-dive.md).

### Keyboard fallback

PowerToys FancyZones already uses `Win+Arrow`, so Swoosh uses `Ctrl+Alt+Shift`:

- `Ctrl+Alt+Shift+Left/Right` for the left or right half
- `Ctrl+Alt+Shift+Up/Down` for maximize or minimize
- `Ctrl+Alt+Shift+U/I/J/K` for the four quarters

The hotkey acts on the window under your cursor.

## Settings

Open Settings from the tray icon. You can:

- Turn gestures on or off, including each snap gesture individually.
- Turn on Start with Windows.
- Adjust touch sensitivity, grid spacing, and the cancel timeout.
- Turn on live preview, where the real window moves as you swipe instead of a
  translucent overlay.
- Pick the overlay color or follow your Windows accent color.
- Check for updates and read the changelog.

## Security and privacy

Swoosh is a local tool, built to be easy to trust.

- No telemetry and no data collection. The only network call is an optional
  update check against GitHub.
- Signed releases using Azure Trusted Signing (verified publisher).
- Every release includes `SHA256SUMS.txt` and a signed SLSA provenance bundle.
- CI runs CodeQL, OpenSSF Scorecard, and Dependabot, plus unit tests on every
  change.

To verify a download or report a vulnerability, see [SECURITY.md](SECURITY.md).
For how signing is set up, see [SIGNING.md](SIGNING.md).

## Build from source

```powershell
dotnet build Swoosh.csproj -c Debug
dotnet run -c Debug --project Swoosh.csproj
```

Full developer setup, project layout, and tests are in
[CONTRIBUTING.md](CONTRIBUTING.md).

## Learn more

- [Deep dive](docs/deep-dive.md): how the gestures and touchpad decoding work.
- [Contributing](CONTRIBUTING.md): build, test, and submit changes.
- [Changelog](CHANGELOG.md)
- [Security policy](SECURITY.md) and [code signing](SIGNING.md)

## Notes

Swish is a trademark of its owner. Swoosh is an independent, clean-room
reimplementation of the interaction concept for Windows, released under the
[MIT License](LICENSE).
