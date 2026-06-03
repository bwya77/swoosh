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

A translucent preview shows the target zone as you swipe; lift to commit. The
preview glides smoothly between zones and the window animates into place rather
than snapping instantly.

## Pinch to fullscreen and back

With **two fingers over the titlebar**, spread them apart (**pinch-out**) and the
window goes fullscreen; draw them together (**pinch-in**) to restore it. A live
preview tracks the pinch so you can see it engage before you commit. The centroid
has to stay put, so a sideways two-finger swipe is never mistaken for a pinch.

## Thirds and the 3x3 grid

Hold a **modifier key** (default **Shift**, configurable to Ctrl or Alt in
settings) while you swipe and the screen snaps to a **3x3 grid** instead of the
normal halves and quarters. As you move left to right the preview steps through
left third, left two-thirds, centered third, right two-thirds, then right third,
and the same vertically. Diagonal swipes land the window in any of the four 1/3
by 1/3 corner cells. Release the modifier to go back to halves and quarters.

## Move across monitors and virtual desktops

Press and hold two fingers on the titlebar, then swipe to send the window to
another monitor or virtual desktop. A small **mini-map HUD** appears at the
cursor: a rounded square stands in for the monitor, the target zone lights up as
you move, and a second square appears when a virtual desktop sits to the
side so you can see where the window will land. The HUD stays up after a move so
you can keep going to the next screen or step back to the previous one. The
target zone lights up in your Windows accent color by default, or any color you
pick in settings.

## Five-finger free move

Put **five fingers** on the titlebar and the touchpad becomes a 1:1 proxy for the
monitor. Move your fingers and the window tracks them live, so you can place it
anywhere with fine-grained control. Lift to drop it in place.

## Five-finger tap to center

Tap **five fingers** briefly on the titlebar (a quick touch with no movement) and
the window snaps to the center of its monitor, keeping its current size. This is
the equivalent of Swish's two-finger double-tap, mapped to five fingers because no
native Windows gesture claims a five-finger tap, so there's nothing to conflict
with. A longer or moving five-finger touch is treated as a free move instead.

## Keyboard fallback

Because PowerToys **FancyZones** already owns `Win+Arrow` and `Win+Alt+Arrow`, the
fallback uses **Ctrl+Alt+Shift**:

- `Ctrl+Alt+Shift+Left/Right` for left or right half
- `Ctrl+Alt+Shift+Up/Down` for maximize or minimize
- `Ctrl+Alt+Shift+U/I/J/K` for the top-left, top-right, bottom-left, or bottom-right quarter

The hotkey acts on the window **under the cursor**.

## Settings

Open **Settings...** from the tray icon for a polished WinUI-style window with a
left navigation pane:

- the current **version** and a short **changelog**, plus a **Check for updates**
  button
- toggle **gestures** and the **touchpad debug overlay**
- toggle the smooth **snap animation**
- enable the **grid modifier** and choose the key (Shift, Ctrl, or Alt) for 3x3
  snapping
- a **touch sensitivity** slider that controls how easily a swipe is read as
  diagonal versus straight
- pick the **overlay color** from a set of swatches, or have it follow your
  **Windows accent color**

Swoosh also checks GitHub for a newer release on startup and lets you know in the
tray if one is available.

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
  monitor or desktop move, a 5-finger free move, or a 5-finger tap to center.
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

- **Settings...** to open the settings window
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

- Magic Mouse gesture support
- Saveable custom snap layouts

## Notes

"Swish" is a trademark of its owner. This project ("Swoosh") is an independent,
clean-room reimplementation of the interaction concept for Windows.
