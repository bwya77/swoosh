# Swoosh deep dive

This document explains how each gesture behaves and how the app decodes the
touchpad and moves windows. For a quick overview and install instructions, see
the [README](../README.md).

## Gestures in detail

All touchpad gestures begin with the cursor over a window's titlebar. A
translucent preview (or, if you turn on live preview, the real window) shows
where the window will land. Lift your fingers to commit, or press Esc to cancel.
Resting your fingers still for the cancel timeout (0.8 seconds by default) drops
the window into the current zone.

### Two-finger snap

A two-finger swipe maps to eight directions:

| Swipe     | Action      |
| --------- | ----------- |
| Left      | Left half   |
| Right     | Right half  |
| Up        | Maximize    |
| Down      | Minimize    |
| Up-left   | Top-left quarter    |
| Up-right  | Top-right quarter   |
| Down-left | Bottom-left quarter |
| Down-right| Bottom-right quarter|

The preview glides between zones as you change direction, and the window
animates into place rather than jumping. A configurable grid spacing leaves a
gap around snapped windows if you want one.

### Pinch to fullscreen and back

With two fingers over the titlebar, spread them apart (pinch-out) to go
fullscreen, or draw them together (pinch-in) to restore. The centroid has to
stay roughly fixed, so a sideways two-finger swipe is never read as a pinch.

### Thirds and the 3x3 grid

Hold a modifier key (Shift by default, configurable to Ctrl or Alt) while you
swipe to snap to a 3x3 grid instead of halves and quarters. Moving left to right
steps the preview through left third, left two-thirds, centered third, right
two-thirds, then right third, and the same vertically. Diagonal swipes land the
window in any of the four corner cells (one third by one third). Release the
modifier to return to halves and quarters.

### Move across monitors and virtual desktops

Press and hold two fingers on the titlebar, then swipe to send the window to
another monitor or virtual desktop. A small mini-map HUD appears at the cursor:
a rounded square stands in for the current desktop, the other desktops unfold
out of it, and the active one stays under your cursor as you step through them.
The HUD stays up after a move so you can keep going or step back.

### Move to another display

Hold the move-to-display modifier (Alt by default) and swipe two fingers over
the titlebar to send the window to an adjacent physical monitor. A monitor-map
HUD shows the current display in the center with its up, down, left, and right
neighbors. Only directions that have a real monitor are drawn, and the one you
are aiming at lights up. The window keeps its relative position and size, so a
left-half window stays a left-half window and a maximized window stays
maximized.

### Mouse middle-button HUD

If you are using a mouse instead of the touchpad, Settings > Snapping has an
optional Mouse middle-button HUD. Hold the middle mouse button over a titlebar,
move toward a snap direction, and release to commit. Swoosh observes the middle
button without swallowing normal mouse clicks.

The mouse path shares the same snap mapping, HUD theme, per-gesture enable
settings, minimize/close chooser, and focus behavior as touchpad gestures. A
still press-and-hold enters the same hold HUD path as the touchpad: move left or
right to target virtual desktops or the optional app switcher, or hold the
configured Move to display modifier to show the physical monitor map.

### Five-finger free move

Put five fingers on the titlebar and the touchpad becomes a 1:1 proxy for the
monitor. Move your fingers and the window tracks them live. Lift to drop it.

### Five-finger tap to center

Tap five fingers briefly on the titlebar (a quick touch with no movement) to
center the window on its monitor while keeping its size. This maps Swish's
two-finger double-tap to five fingers, because no native Windows gesture claims
a five-finger tap, so there is nothing to conflict with. A longer or moving
five-finger touch is treated as a free move instead.

### Focus behavior

Swooshing a window always brings it to the front, even if it was not the active
window when you started.

## How it works

```
RawTouchpadListener -> TouchpadParser -> GestureEngine -> SwooshController -> WindowSnapper
   (WM_INPUT, HID)     (hid.dll HidP_*)   (gesture logic)    (orchestration)    (SetWindowPos)
```

- **RawTouchpadListener** registers for raw HID input (Usage Page `0x0D`, Usage
  `0x05`) on a message-only window with `RIDEV_INPUTSINK`, so it sees the
  touchpad even when another app is focused.
- **TouchpadParser** uses `hid.dll` (`HidP_GetCaps`, `HidP_GetValueCaps`,
  `HidP_GetUsageValue`, `HidP_GetUsages`) to decode each report into per-finger
  contacts (id, normalized X and Y, tip-down). It also filters out a firmware
  quirk where a contact can stay wedged down after a multi-finger lift.
- **GestureEngine** tracks the finger centroid and classifies the gesture: a
  two-finger swipe into one of eight snap directions, a press-and-hold swipe into
  a monitor or desktop move, a five-finger free move, or a five-finger tap to
  center.
- **SwooshController** wires the input, gestures, hotkeys, settings, and overlays
  together and decides what to do on each event.
- **WindowSnapper** computes the target rectangle from the monitor work area and
  applies it with `SetWindowPos`, compensating for the invisible DWM resize
  border (`DWMWA_EXTENDED_FRAME_BOUNDS`) so the visible frame lands exactly on
  the zone edges.

The app is per-monitor-DPI-v2 aware, so geometry is correct across mixed-DPI
multi-monitor setups.

## Releases and verification

Releases are produced by `.github/workflows/release.yml`. Every push to `main`
builds win-x64 and win-arm64, signs the binaries and the installers with Azure
Trusted Signing, and publishes a GitHub Release with:

- the installers (`SwooshSetup-<version>-win-<arch>.exe`),
- the portable zips (`Swoosh-<version>-win-<arch>.zip`),
- `SHA256SUMS.txt`,
- a signed SLSA build provenance bundle (`.intoto.jsonl`).

You can verify any download:

```powershell
# Authenticode signature (verified publisher)
Get-AuthenticodeSignature .\SwooshSetup-<version>-win-<arch>.exe | Format-List Status, SignerCertificate

# SLSA build provenance (proves it was built by this repo's CI from this source)
gh attestation verify .\SwooshSetup-<version>-win-<arch>.exe --repo bwya77/swoosh

# SHA-256 against the published SHA256SUMS.txt
Get-FileHash .\SwooshSetup-<version>-win-<arch>.exe -Algorithm SHA256
```

See [SECURITY.md](../SECURITY.md) for the full security policy and
[SIGNING.md](../SIGNING.md) for how code signing is set up.
