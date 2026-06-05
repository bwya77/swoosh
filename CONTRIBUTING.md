# Contributing to Swoosh

Thanks for your interest in Swoosh. This guide covers how to build, run, test,
and submit changes.

## Prerequisites

- Windows 10 or 11.
- The [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
- A Precision Touchpad if you want to test gestures on real hardware. The tray
  menu includes a touchpad debug overlay that shows live finger contacts, which
  is the easiest way to confirm decoding on your device.

The settings app uses WinUI 3 (Windows App SDK), which restores as a NuGet
package, so no extra workload install is required for a normal build.

## Build and run

```powershell
# Tray app (WPF)
dotnet build Swoosh.csproj -c Debug
dotnet run -c Debug --project Swoosh.csproj

# Settings app (WinUI 3) builds as its own project
dotnet build Swoosh.Settings/Swoosh.Settings.csproj -c Debug
```

The app runs in the system tray. Right-click the tray icon for Settings, the
master gestures toggle, the debug overlay, About, and Quit. A diagnostic log is
written to `%TEMP%\swoosh.log`.

To produce a self-contained single file like the released build:

```powershell
dotnet publish Swoosh.csproj -c Release -r win-arm64 --self-contained true -p:PublishSingleFile=true
```

Swap `win-arm64` for `win-x64` on Intel or AMD hardware.

## Tests

Unit tests cover the snapping geometry and settings serialization. Run them
with:

```powershell
dotnet test Swoosh.Tests/Swoosh.Tests.csproj -c Release
```

The same tests run in CI on every push and pull request.

## Project layout

| Path | What it holds |
| --- | --- |
| `Input/` | Raw HID touchpad listener and the report parser |
| `Gestures/` | The gesture recognition engine |
| `Snapping/` | Window placement, monitor and virtual-desktop logic |
| `UI/` | Tray menu and the on-screen overlays |
| `Settings/` | Settings model, persistence, startup, and stats (shared with the settings app) |
| `Updates/` | The GitHub release update checker |
| `Native/` | Win32 and HID interop |
| `Swoosh.Settings/` | The standalone WinUI 3 settings window |
| `Swoosh.Tests/` | xUnit tests |
| `installer/` | The Inno Setup installer script |

For how the pieces fit together and how each gesture behaves, see the
[deep dive](docs/deep-dive.md).

## Submitting changes

1. Fork the repo and create a branch for your change.
2. Keep changes focused and include a short, clear description in the pull
   request.
3. The `main` branch is protected. A pull request must pass the unit tests and
   CodeQL analysis before it can be merged.
4. Match the existing code style. Comment code only where the intent is not
   obvious, and prefer clear names over comments.

## Reporting issues

- For bugs and feature requests, open an issue. Including your device, Windows
  version, and the contents of `%TEMP%\swoosh.log` helps a lot.
- For security vulnerabilities, please follow [SECURITY.md](SECURITY.md) instead
  of opening a public issue.

## License

Swoosh is released under the [MIT License](LICENSE). By contributing, you agree
that your contributions are licensed under the same terms.
