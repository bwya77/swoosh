# Swoosh

[![Build & Release](https://github.com/bwya77/swoosh/actions/workflows/release.yml/badge.svg)](https://github.com/bwya77/swoosh/actions/workflows/release.yml)
[![Latest release](https://img.shields.io/github/v/release/bwya77/swoosh?sort=semver)](https://github.com/bwya77/swoosh/releases/latest)

Swish-style window management for Windows. Hover the cursor over a window's
**titlebar**, then **two-finger swipe** on the Precision Touchpad and the window
snaps to a left/right half, a quarter, fullscreen, or minimize. Inspired by the
macOS app [Swish](https://highlyopinionated.co/swish/).

> Status: working MVP. The snapping engine is verified pixel-perfect. Touchpad
> gesture decoding is built to the HID Precision Touchpad spec and ships with a
> live debug overlay so you can validate finger tracking on your hardware.

## Download

Grab the latest build from the [Releases page](https://github.com/bwya77/swoosh/releases/latest):

- `Swoosh-<version>-win-arm64.zip` for ARM64 devices (Surface Pro X, Surface Pro 9 5G, and similar)
- `Swoosh-<version>-win-x64.zip` for everything else

Each archive is a self-contained single executable. Unzip it and run `Swoosh.exe`.
No .NET install is required because the runtime is bundled inside the binary.

Every push to `main` publishes a fresh release automatically with an
auto-incrementing version, so the download link above always points at the
newest build.

## Gestures (two fingers, cursor over the titlebar)

| Swipe        | Action            |
|--------------|-------------------|
| Left         | Left half         |
| Right        | Right half        |
| Up           | Maximize          |
| Down         | Minimize          |
| Diagonal     | Quarter           |

A translucent preview shows the target zone as you swipe; lift to commit.

## Move across monitors and virtual desktops

Press and hold two fingers on the titlebar, then swipe to send the window to
another monitor or virtual desktop. A small **mini-map HUD** appears at the
cursor: a rounded square stands in for the monitor, the target zone lights up in
blue as you move, and a second square appears when a virtual desktop sits to the
side so you can see where the window will land. The HUD stays up after a move so
you can keep going to the next screen or step back to the previous one.

## Five-finger free move

Put **five fingers** on the titlebar and the touchpad becomes a 1:1 proxy for the
monitor. Move your fingers and the window tracks them live, so you can place it
anywhere with fine-grained control. Lift to drop it in place.

## Keyboard fallback

Because PowerToys **FancyZones** already owns `Win+Arrow` and `Win+Alt+Arrow`, the
fallback uses **Ctrl+Alt+Shift**:

- `Ctrl+Alt+Shift+Left/Right` for left or right half
- `Ctrl+Alt+Shift+Up/Down` for maximize or minimize
- `Ctrl+Alt+Shift+U/I/J/K` for the top-left, top-right, bottom-left, or bottom-right quarter

The hotkey acts on the window **under the cursor**.

## How it works

```
RawTouchpadListener --> TouchpadParser --> GestureEngine --> SwooshController --> WindowSnapper
   (WM_INPUT, HID)     (hid.dll HidP_*)    (gesture logic)     (orchestration)    (SetWindowPos)
```

- **RawTouchpadListener** registers for raw HID input (Usage Page `0x0D`,
  Usage `0x05`) on a message-only window with `RIDEV_INPUTSINK`, so it sees the
  touchpad even when another app is focused.
- **TouchpadParser** uses `hid.dll` (`HidP_GetCaps`, `HidP_GetValueCaps`,
  `HidP_GetUsageValue`, `HidP_GetUsages`) to decode each report into per-finger
  contacts (id, normalized X/Y, tip-down). It also filters out a firmware quirk
  where a contact can stay wedged down after a multi-finger lift.
- **GestureEngine** tracks the finger centroid and classifies the gesture: a
  2-finger swipe into one of 8 snap directions, a press-and-hold swipe into a
  monitor or desktop move, or a 5-finger free move.
- **WindowSnapper** computes the target rect from the monitor work area and
  applies it with `SetWindowPos`, compensating for the invisible DWM resize
  border (`DWMWA_EXTENDED_FRAME_BOUNDS`) so the **visible** frame lands exactly
  on the zone edges.

The app is per-monitor-DPI-v2 aware, so geometry is correct across mixed-DPI
multi-monitor setups.

## Build and run from source

```powershell
dotnet build -c Debug
dotnet run -c Debug
# or produce a self-contained single file like the released build:
dotnet publish -c Release -r win-arm64 --self-contained true -p:PublishSingleFile=true
.\bin\Release\net8.0-windows\win-arm64\publish\Swoosh.exe
```

Swap `win-arm64` for `win-x64` on Intel or AMD hardware.

Runs in the system tray. Right-click the tray icon for:

- **Gestures enabled** as the master toggle
- **Touchpad debug overlay** that shows live finger contacts (use this to confirm
  the touchpad is being decoded on your machine)
- **About / Quit**

A diagnostic log is written to `%TEMP%\swoosh.log`.

## Releases

Releases are produced by a GitHub Actions workflow (`.github/workflows/release.yml`).
On every push to `main` it builds win-x64 and win-arm64, packages each as a zip,
and publishes a GitHub Release tagged `v0.1.<run number>`. To start a new version
series, edit the `0.1.` prefix in that workflow and the run number keeps counting
from there. You can also start a build by hand from the **Actions** tab using
**Run workflow**.

## Roadmap

- Pinch-in to close, pinch-out to fullscreen
- Chained swipes for thirds (half, then one third, then two thirds)
- Settings window with per-gesture toggles and sensitivity
- Snap layouts (2x2, 3x2, 3x3 grids)
- Magic Mouse and modifier-key gesture support

## Notes

"Swish" is a trademark of its owner. This project ("Swoosh") is an independent,
clean-room reimplementation of the interaction concept for Windows.
