# Code Signing - decision and spec

Status: IMPLEMENTED in CI (2026-07-08, issue #93). Signing turns ON the moment the
repo secrets/variable are set (see "Repository secrets and variable" below); until then
the pipeline builds UNSIGNED releases unchanged. Supersedes the blocking premise of #2
(the certificate now exists).

## Decision

**Azure Trusted Signing** (Microsoft's managed signing service, formerly "Azure Artifact
Signing"), under **Center Consulting Inc.** as the validated publisher.

- **AgentEyes reuses DevThrottle's existing signing setup.** DevThrottle stood up Azure
  Trusted Signing under Center Consulting Inc. on 2026-07-05 (verified working: signature
  Valid, chains to a preinstalled Windows root). AgentEyes reuses the SAME signing account
  and the SAME certificate profile - no new Azure account, no new cost, no re-doing the
  identity validation.
- **No certificate is purchased.** Microsoft is the certificate authority: the service
  issues short-lived, HSM-backed certificates on every signing operation. Nothing to store,
  renew, or lose.
- **Cost:** already covered by the existing DevThrottle Basic-tier account (USD 9.99/month -
  5,000 signatures/month, 1 certificate profile). AgentEyes adds no incremental cost.
- **Publisher identity:** the certificate carries the validated legal name
  `CN=Center Consulting Inc.` - that string is what Windows shows users as the publisher.
- **GitHub has no native Authenticode signing** (Sigstore attestations do not satisfy
  SmartScreen). CI signing happens via the official `azure/trusted-signing-action` calling
  the same Azure service.

## Reused Azure facts (not secret)

| Fact | Value |
|---|---|
| Signing account | `centerconsulting` (resource group `CenterConsultingSigning`, East US, Basic) |
| Endpoint / Account URI | `https://eus.codesigning.azure.net/` |
| Certificate profile | `devthrottle-signing` (Public Trust; AgentEyes reuses this profile) |
| Publisher shown to users | `CN=Center Consulting Inc.` |
| Timestamp URL | `http://timestamp.acs.microsoft.com/` |

The service principal used by CI must hold the **"Trusted Signing Certificate Profile
Signer"** role on the `centerconsulting` account.

## What gets signed

The four first-party self-contained exes, after publish and BEFORE the manifest is hashed:

| Artifact | Produced by | Signed |
|---|---|---|
| `AgentEyesApp-win-x64.exe` (WPF tray app) | dotnet publish | yes |
| `agenteyes-win-x64.exe` (CLI) | dotnet publish | yes |
| `agenteyes-setup-cli-win-x64.exe` (setup CLI) | dotnet publish | yes |
| `AgentEyes-Setup-win-x64.exe` (setup wizard - what users download) | dotnet publish | yes |

NOT signed by us:

- `ffmpeg.exe` / `ffprobe.exe` - third-party Gyan builds; we must not sign binaries we did
  not produce. They ship zipped inside `agenteyes-ffmpeg-win-x64.zip`, so the `filter: exe`
  sign step never touches them (they are not loose exes in `dist\release`).
- Microsoft's .NET runtime files - already signed by Microsoft and bundled INSIDE the
  self-contained single-file exes, so they are never loose files to sign.

Integrity of the bundle as a whole is covered by the signed installer plus the SHA-256
manifest.

## How signing is wired (CI)

The release workflow (`.github/workflows/release.yml`) runs, in order:

1. **Publish (unsigned):** `scripts\build-release.ps1 -FfmpegDir <dir> -SkipManifest`
   publishes the four exes + the ffmpeg zip into `dist\release\` and writes the
   asset-version sidecar `dist\asset-versions.json`, but does NOT write the manifest.
2. **Sign:** the reusable composite action `.github/actions/sign-windows` signs
   `dist\release` with `filter: exe` (recursing), which is exactly the four first-party
   exes. Guarded by `if: ${{ vars.SIGNING_CERT_PROFILE != '' }}` - a no-op until the
   secrets/variable are set.
3. **Manifest:** `scripts\write-manifest.ps1` hashes the assets as they now sit on disk
   (signed, if step 2 ran) into `dist\release\release-manifest.json`.

Ordering matters: the setup engine SHA-256-verifies each asset at install, so the manifest
MUST record the hash of the SIGNED bytes. Publishing and manifest-writing were split for
exactly this reason. A LOCAL one-shot build (`scripts\build-release.ps1` with no
`-SkipManifest`) calls `write-manifest.ps1` itself and produces an identical UNSIGNED
release - local behavior is unchanged.

### The composite action

`.github/actions/sign-windows/action.yml` wraps `azure/trusted-signing-action@v2.0.0`
(matches the version DevThrottle currently ships; v2.0.0 migrated to the artifactsigning
module and renamed `trusted-signing-account-name` to `signing-account-name`). The endpoint
(`https://eus.codesigning.azure.net/`) and account name (`centerconsulting`) are baked in;
the caller supplies `certificate-profile-name`, `folder`, `filter`, and the three Azure
credentials. Confirm the pinned action version is still the current release before the first
signed release.

## Repository secrets and variable (human-only, one-time)

Signing stays OFF until a human sets these in
`thefrederiksen/AgentEyes` -> Settings -> Secrets and variables -> Actions. Use the SAME
values as the DevThrottle repo:

| Kind | Name | Value |
|---|---|---|
| Variable | `SIGNING_CERT_PROFILE` | `devthrottle-signing` |
| Secret | `AZURE_TENANT_ID` | tenant of the CI Entra app registration |
| Secret | `AZURE_CLIENT_ID` | app registration (service principal) client id |
| Secret | `AZURE_CLIENT_SECRET` | a client secret for that app registration |

Because every sign step is gated on `vars.SIGNING_CERT_PROFILE != ''`, the code merges and
ships safely BEFORE these are set; signing simply stays off until the variable is present.

## Verifying a signed release (human, post-merge)

After the secrets/variable are set and a `v*` tag is pushed:

```
signtool verify /pa /v AgentEyes-Setup-win-x64.exe
```

Expect the signature to report **Valid** and the signer to be `CN=Center Consulting Inc.`.
On a clean Windows machine the SmartScreen prompt no longer reads "Unknown publisher".

## SmartScreen expectations

Signing removes the "Unknown publisher" red flag immediately; full SmartScreen reputation
still accrues over downloads. Azure Trusted Signing certs inherit reputation at the publisher
level, so it builds once for Center Consulting Inc. across releases (and across DevThrottle
and AgentEyes, which share the publisher) - one more reason every release must be signed from
the first one onward.
