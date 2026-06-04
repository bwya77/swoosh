# Code Signing

Swoosh is code-signed with **Azure Trusted Signing** (now branded **Azure
Artifact Signing**) so Windows SmartScreen stops warning when the app is
downloaded and when it launches at sign-in.

The release workflow (`.github/workflows/release.yml`) already contains the
signing step. It stays dormant until the credentials below are configured, then
activates automatically on the next build — no code changes needed.

> **Status: configured and verified (June 4, 2026).** Signing is live. The first
> signed release was **v0.1.19**; its `Swoosh.exe` and `Swoosh.Settings.exe`
> verify as `Status: Valid`, signed by `CN=Bradley Wyatt` and issued by
> `Microsoft ID Verified CS AOC CA 03`, with a valid timestamp. SmartScreen
> reputation builds over the following weeks of clean installs.

## Configured resources

| Resource | Value |
| --- | --- |
| Trusted Signing account | `swoosh-signing` |
| Region / endpoint | North Central US — `https://ncus.codesigning.azure.net/` |
| Certificate profile | `swoosh` (Public Trust, Individual Developer) |
| Publisher identity | `CN=Bradley Wyatt, O=Bradley Wyatt, L=Geneva, S=IL, C=US` |
| CI auth | App Registration `swoosh-signing-ci` with the *Trusted Signing Certificate Profile Signer* role |

The six repo secrets/variables (see the table further down) are already set. The
workflow signs the launched `*.exe` for both architectures (`win-x64`,
`win-arm64`) before packaging.

## Why this and not an EV certificate

| Option | Cost | SmartScreen | Notes |
| --- | --- | --- | --- |
| Unsigned | $0 | Warns every download | Current state |
| **Trusted Signing (this)** | **~$10/mo** | Clears once reputation builds | Best fit for an indie app |
| OV certificate | $300–500/yr | Same reputation model | More setup, secrets on disk |
| EV certificate | $300–700/yr | **Instant trust** | Hardware token / cloud HSM |

Trusted Signing is OV-equivalent: SmartScreen reputation is tied to the verified
publisher identity and builds over a few weeks of clean installs. It does **not**
grant instant trust — only an EV certificate does that.

## Eligibility

This project uses the **Individual Developer** identity-validation path, which is
available to individual developers in the **USA and Canada**. (The *Organization*
path is available in the USA, Canada, EU, and UK — that's a separate option.)

The certificate publisher name will be the developer's **legal name**, not a
company name.

## One-time setup

1. **Create a Trusted Signing account** — Azure Portal → "Trusted Signing".
   Choose a region; the region determines the endpoint URI, e.g. East US →
   `https://eus.codesigning.azure.net/`. Cost ~$9.99/mo (Basic tier).

2. **Complete identity validation** — In the account, create an **Individual**
   identity validation (requires the *Trusted Signing Identity Verifier* role and
   must be done in the Azure portal — the CLI can't do this). Microsoft verifies
   the developer's legal name and address; this can take a few days.

3. **Create a certificate profile** — Account → Certificate profiles → new →
   **Public Trust**, bound to the validated identity. Note the profile name.

4. **Create an App Registration for CI auth** — Microsoft Entra ID → App
   registrations → new. Record the **Client ID** and **Tenant ID**. Under
   Certificates & secrets, create a **client secret** and copy its value. On the
   Trusted Signing account's **Access control (IAM)**, assign the role
   **Trusted Signing Certificate Profile Signer** to that App Registration.

5. **Configure the GitHub repo** — Settings → Secrets and variables → Actions:

   | Kind | Name | Value |
   | --- | --- | --- |
   | Secret | `AZURE_TENANT_ID` | from step 4 |
   | Secret | `AZURE_CLIENT_ID` | from step 4 |
   | Secret | `AZURE_CLIENT_SECRET` | from step 4 |
   | Variable | `TRUSTED_SIGNING_ENDPOINT` | e.g. `https://eus.codesigning.azure.net/` |
   | Variable | `TRUSTED_SIGNING_ACCOUNT` | the account name |
   | Variable | `TRUSTED_SIGNING_PROFILE` | the certificate profile name |

Once these exist, the next push that triggers a release produces signed binaries.
The workflow signs the launched `*.exe` for both `win-x64` and `win-arm64` before
the zips are packaged. (It deliberately does **not** sign the bundled .NET /
WindowsAppSDK runtime DLLs — only the launched executables matter for SmartScreen,
and signing the whole runtime pushed the step past 15 minutes.)

## How the workflow guards signing

The job sets `HAS_SIGNING: ${{ secrets.AZURE_CLIENT_ID != '' }}` and the sign step
runs only `if: env.HAS_SIGNING == 'true'`. With no credentials configured the step
is skipped and unsigned builds publish exactly as before.

## Verifying a signed build

After a signed release, download the zip and check a binary:

```powershell
Get-AuthenticodeSignature .\Swoosh.exe | Format-List Status, SignerCertificate
```

`Status` should be `Valid` and the signer certificate subject should show the
publisher (legal name) identity.
