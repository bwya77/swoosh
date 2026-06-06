# Changelog

What's new in Swoosh. Newest changes are at the top.

## June 2026

- The Settings app has a refreshed look. The General page is now a Home page with the Swoosh logo, status chips, and grouped cards, and every section (Snapping, Appearance, Updates, About) now uses consistent section headers and leading icons. The window also has a sensible minimum size and adapts its header as you resize, so text no longer jumbles when the window gets narrow.
- You can now support Swoosh's development. Settings under About has a Support card with GitHub Sponsors and Buy Me a Coffee links, and the repository has a Sponsor button.
- New "HUD fade-out" slider in Settings under Appearance: control how long the gesture HUD takes to fade away when a gesture ends (0.10 to 1.50 s, default 0.36 s). Raise it to make the HUD linger and fade more slowly.
- The slider settings now show their range as fixed labels on each end and a reset button to return to the default, and the value no longer shifts the slider as you drag.
- The system tray menu now follows your Windows light/dark setting instead of always being dark.
- The gesture HUD's highlight color now updates live when you change your Windows accent color, without needing to restart Swoosh.
- The move-to-display HUD now shows the current monitor in solid color at rest, matching the desktop strip and snap HUD.
- The lifetime swoosh count in Settings now updates instantly while the Settings window is open.
- New "Desktop switch hold delay" slider in Settings under Snapping: control how long you hold before the virtual-desktop switcher appears (0.10 to 1.00 s, default 0.30 s). It works independently of the cancel timeout now, so a longer hold delay no longer gets cancelled early.
- The Snapping settings are now grouped into sections (Gestures, Snap behavior, Virtual desktops and displays) so the longer list is easier to scan.
- The virtual-desktop reveal is smoother and snappier. The desktop strip now holds still while your fingers rest (instead of drifting with the cursor), no longer flashes the full strip for a frame before animating, and starts appearing a bit sooner after you begin holding.
- New "HUD size" option in Settings under Appearance: choose Normal (the default) or Large for the gesture HUD.
- The gesture HUD can now use a light background. In Settings under Appearance, the new "HUD background" option offers Dark, Light, or Follow system (which matches the Windows light/dark setting and updates on your next gesture when you change it). The highlight color is unchanged.
- Consolidated the two "Move to display" settings into one. A single dropdown now offers Off, Shift, Ctrl, or Alt, so you pick whether it is on and which key engages it in one place.
- Fixed the first gesture after a cold start sometimes being misread as a move to another virtual desktop. On the first launch the input thread could briefly stall while warming up, which made a quick swipe look like a press-and-hold. Swoosh now ignores that startup stall, so your first swipe snaps the window as expected.
- New "Preview destination desktop" option (on by default): when you move a window to another virtual desktop (hold then swipe), the HUD now highlights the desktop the window will land on as you aim, and the move commits when you drop instead of switching live as your fingers sweep. Swipe farther to target a further desktop, so you can send a window straight from desktop 1 to desktop 3 in a single motion. Turn it off to keep the live step-by-step ratchet that switches as you go.
- Snapping with live preview is cleaner: the window no longer does a small refresh or re-glide at the moment you set it. The live preview already places the window during the swipe, so committing the gesture just leaves it there.
- Performance: the touchpad decoder no longer formats and writes a diagnostic line to disk on every frame during a gesture, and no longer allocates and copies the raw report buffer per frame. Gesture input is lighter on CPU and produces no garbage on the hot path. Detailed per-frame input logging is still available for bug reports by setting the SWOOSH_LOG environment variable to 1 before launching.
- New "Move cursor with window" setting (off by default): after a snap, the cursor follows the window to the same relative spot it was grabbed at, so the middle of the titlebar stays under the cursor in the window's new position.
- Fixed a black flash in newly revealed areas when growing a window with the five-finger resize. Resizes now repaint properly instead of expanding ahead of the app's redraw.

- Fixed the version shown in Settings reading 0.1.0 on installed and released builds. It now reads the real build version, which also means installed builds correctly check for updates on startup again.
- Swooshing a window that wasn't in focus now brings it to the front. Previously it would snap into place but stay hidden behind other windows; now it's reliably raised and activated (with live preview, it comes forward as soon as it starts moving).
- Swoosh now has a proper signed Windows installer: `SwooshSetup-<arch>.exe` installs to Program Files with a Start Menu shortcut and a clean uninstaller (which also tidies up the sign-in entry). It offers a "Start Swoosh when I sign in to Windows" checkbox that the app picks up as its launch-at-login setting. The portable zip is still available for no-install use.
- Installed builds can now update themselves: choosing "update" downloads and runs the new signed installer, which closes Swoosh, updates in place, and relaunches it. Portable builds still open the releases page.
- New "Live window preview" option (off by default): as you swipe, the real window glides to the target zone instead of showing the translucent overlay, so you preview on the actual app. Esc puts it back.
- New "Grid spacing" slider (0–10px): leave a consistent gap around snapped windows, or keep it at 0 for flush, edge-to-edge snapping.
- New "Cancel timeout" slider (default 0.8s): rest your fingers still to drop the window into the current zone, or press Esc to cancel and put it back where it started.
- The HUD now fades out smoothly when you finish, drop, or cancel a gesture instead of vanishing instantly.
- The virtual-desktop strip now unfolds out of the current desktop (kept under your cursor) rather than blooming from the middle, and the active desktop stays put as you step through them.
- Swoosh has an app icon now — a soft blue screen with a window snapped and floating over its left half. It shows up in the system tray, the taskbar, File Explorer, the download, and the sign-in (run) entry instead of the generic blank icon.
- The Settings "What's new" section now links to the full changelog on GitHub instead of rendering an abbreviated copy inline.
- "Start with Windows" now self-heals: if you move or update Swoosh, it re-points its sign-in entry at the new location the first time you run it from there, so launch-at-login keeps working without re-toggling the setting.
- Windows now track your fingers more tightly during a free-move: the move is handed off to the window's own thread instead of waiting on heavy apps to repaint, so they no longer trail behind fast motion.
- The snap HUD is easier to read: a darker, more solid backdrop keeps the highlighted zone visible with any accent colour (including grey) and over light or busy wallpapers.
- Fixed the Settings window's minimize, maximize, and close buttons showing as black in dark mode — they now reliably match the current theme on first open.
- Swoosh now runs as a single instance: launching it again (for example when the sign-in entry fires while it's already open) quietly focuses the existing app instead of stacking a second tray icon.
- Start Swoosh automatically when you sign in to Windows — toggle it on the General settings page.
- Track your lifetime swooshes: a running tally of every snap, move, and gesture now lives at the bottom of the Settings navigation pane.
- Touch sensitivity now starts at a lighter default so gestures register more easily out of the box.
- Overlay colour swatches no longer turn grey when you hover them, and they stay clearly visible in dark mode with a subtle outline; the selected swatch ring now adapts to light and dark themes.
- The Settings window's minimize, maximize, and close buttons now match the current theme instead of disappearing.
- Fixed dark-mode readability: gesture descriptions and changelog text are now properly visible instead of rendering near-black.
- The virtual-desktop HUD now unfolds smoothly: it starts as the single current desktop and the other desktops slide out from the centre and fade in, instead of popping from one square to many.
- Turn individual snap gestures on or off from the Snapping settings. Each one is a clickable window-shape tile (Maximize, Halves, Quarters, Minimize, Center, and the thirds grid) that greys out when disabled.
- Move a window to another monitor: hold Alt (configurable) and swipe, with a monitor-map HUD showing where it will land.
- New system tray menu with a clean dark theme, even spacing, and a smooth hover highlight.
- The tray menu no longer closes unexpectedly while you move the mouse across it.
- Settings now sync instantly between the tray app and the Settings window.
- "What's new" notes are now shown right here in Settings.

## May 2026

- Pinch out to go fullscreen, pinch in to restore.
- Snap to thirds and corners with a held modifier key.
- Snap animation, a touchpad debug overlay, and automatic update checks.
- WinUI 3 Settings window with General, Snapping, Appearance, and Updates pages.
