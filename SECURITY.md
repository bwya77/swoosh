# Security Policy

## Reporting a vulnerability

If you believe you've found a security vulnerability in Swoosh, please report it
privately rather than opening a public issue:

- Use GitHub's **[Report a vulnerability](https://github.com/bwya77/swoosh/security/advisories/new)**
  form (Security tab → Advisories), or
- Open a regular issue **only** for clearly non-sensitive matters.

Please include steps to reproduce, the affected version, and any relevant logs
(`%TEMP%\swoosh.log`). We aim to acknowledge reports within a few days.

## Supported versions

Swoosh ships as a rolling release; only the **latest** release on the
[Releases page](https://github.com/bwya77/swoosh/releases/latest) is supported.
Please update before reporting an issue.

## What Swoosh does (and does not) do

Swoosh is a local Windows utility. For transparency:

- **No network access** except an optional update check against the public GitHub
  Releases API. There is **no telemetry and no data collection**.
- **Touchpad input:** registers for raw Precision Touchpad HID reports
  (`RegisterRawInputDevices`) to decode multi-finger gestures. Input is processed
  in memory and never stored or transmitted.
- **Window management:** reads window positions and moves/resizes the window under
  the cursor via standard Win32 APIs (`SetWindowPos`, `ShowWindow`).
- **Registry:** the optional "Start with Windows" setting writes a single value to
  the per-user `HKCU\…\Run` key. No machine-wide or elevated changes are made.
- **Files:** settings and a lifetime-counter file are stored under
  `%APPDATA%\Swoosh\`. A diagnostic log may be written to `%TEMP%\swoosh.log`.

## How releases are protected

- **Code signing:** release binaries are signed with **Azure Trusted Signing**
  (verified publisher identity). See [SIGNING.md](SIGNING.md).
- **Build provenance:** release artifacts carry a signed
  [SLSA build provenance attestation](https://github.com/bwya77/swoosh/attestations)
  so you can verify a download was built by this repository's CI from this source.
- **Checksums:** each release includes `SHA256SUMS.txt`.
- **Scanning:** CI runs CodeQL (SAST), OpenSSF Scorecard, and Dependabot
  (dependency alerts + automated fixes).

## Verifying a download

```powershell
# 1. Authenticode signature (publisher = Bradley Wyatt, issued by Microsoft)
Get-AuthenticodeSignature .\Swoosh.exe | Format-List Status, SignerCertificate

# 2. SLSA build provenance (proves it was built by this repo's CI)
gh attestation verify .\Swoosh-<version>-win-<arch>.zip --repo bwya77/swoosh

# 3. SHA-256 checksum against the published SHA256SUMS.txt
Get-FileHash .\Swoosh-<version>-win-<arch>.zip -Algorithm SHA256
```
