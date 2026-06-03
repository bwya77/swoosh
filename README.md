# Swoosh

Swish-style window management for Windows. Hover the cursor over a window's
**titlebar**, then **two-finger swipe** on the Precision Touchpad and the window
snaps — left/right half, quarters, fullscreen, or minimize. Inspired by the
macOS app [Swish](https://highlyopinionated.co/swish/).

> Status: working MVP. Snapping engine is verified pixel-perfect. Touchpad
> gesture decoding is built to the HID Precision Touchpad spec and ships with a
> live debug overlay so you can validate finger tracking on your hardware.

## Gestures (two fingers, cursor over the titlebar)

| Swipe        | Action            |
|--------------|-------------------|
| ← Left       | Left half         |
| → Right      | Right half        |
| ↑ Up         | Maximize          |
| ↓ Down       | Minimize          |
| ↖ ↗ ↙ ↘ Diagonal | Quarter       |

A translucent preview shows the target zone as you swipe; lift to commit.

## Keyboard fallback

Because PowerToys **FancyZones** already owns `Win+Arrow`/`Win+Alt+Arrow`, the
fallback uses **Ctrl+Alt+Shift**:

- `Ctrl+Alt+Shift+←/→` — left / right half
- `Ctrl+Alt+Shift+↑/↓` — maximize / minimize
- `Ctrl+Alt+Shift+U/I/J/K` — top-left / top-right / bottom-left / bottom-right quarter

The hotkey acts on the window **under the cursor**.

## How it works

```
RawTouchpadListener ──► TouchpadParser ──► GestureEngine ──► SwooshController ──► WindowSnapper
   (WM_INPUT, HID)     (hid.dll HidP_*)    (2-finger swipe)     (orchestration)    (SetWindowPos)
```

- **RawTouchpadListener** registers for raw HID input (Usage Page `0x0D`,
  Usage `0x05`) on a message-only window with `RIDEV_INPUTSINK`, so it sees the
  touchpad even when another app is focused.
- **TouchpadParser** uses `hid.dll` (`HidP_GetCaps`/`HidP_GetValueCaps`/
  `HidP_GetUsageValue`/`HidP_GetUsages`) to decode each report into per-finger
  contacts (id, normalized X/Y, tip-down).
- **GestureEngine** tracks the 2-finger centroid and classifies the swipe into
  one of 8 directions once it passes the commit distance.
- **WindowSnapper** computes the target rect from the monitor work area and
  applies it with `SetWindowPos`, compensating for the invisible DWM resize
  border (`DWMWA_EXTENDED_FRAME_BOUNDS`) so the **visible** frame lands exactly
  on the zone edges.

The app is per-monitor-DPI-v2 aware, so geometry is correct across mixed-DPI
multi-monitor setups.

## Build & run

```powershell
dotnet build -c Debug
dotnet run -c Debug
# or the published build:
dotnet publish -c Release -r win-arm64 --self-contained false
.\bin\Release\net8.0-windows\win-arm64\publish\Swoosh.exe
```

Runs in the system tray. Right-click the tray icon for:
- **Gestures enabled** — master toggle
- **Touchpad debug overlay** — shows live finger contacts (use this to confirm
  the touchpad is being decoded on your machine)
- **About / Quit**

A diagnostic log is written to `%TEMP%\swoosh.log`.

## Roadmap

- Pinch-in to close, pinch-out to fullscreen
- Swipe to move windows between monitors / virtual desktops
- Chained swipes → thirds (½ → ⅓ → ⅔)
- Settings window with per-gesture toggles and sensitivity
- Snap layouts (2×2 / 3×2 / 3×3 grids)
- Magic Mouse / modifier-key gesture support

## Notes

"Swish" is a trademark of its owner; this project ("Swoosh") is an independent,
clean-room reimplementation of the interaction concept for Windows.
