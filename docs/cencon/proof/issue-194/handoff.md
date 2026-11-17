# Issue #194 - Developer handoff (LINUX HALF ONLY)

Branch `issue-194-linux-release-asset`, PR #205, from main 02a4622 (v1.12.0).

**Scope of this pass:** the Linux asset, i.e. criteria 2, 3, 5 and 6 for Linux, and criterion 4
stated for Linux and for a platform with no asset. **The macOS half is not started** and #194 stays
open for it (see "What the macOS half still needs" at the end). No tag was created or pushed, and no
release was cut or edited. The latest release is still v1.12.0.

## The asset form: a tarball plus a verifying install script

Two Linux assets:

- `agenteyes-linux-x64.tar.gz`: the prebuilt self-contained recorder (`AgentEyes.Core`) and
  desktop app (`AgentEyes.DesktopApp`), plus the same `scripts/linux/install.sh` a source checkout
  uses, run with the new `--prebuilt DIR`.
- `agenteyes-install-linux-x64.sh`: downloads the tarball and `release-manifest.json` from its own
  release, checks the tarball's size and SHA-256, and only then extracts and installs.

**What decided it: the per-user unit.** The recorder is a systemd USER unit in
`~/.config/systemd/user`, enabled with `systemctl --user` for the person installing, and everything
else lives under that person's home. A `.deb` is installed by dpkg as root. Its maintainer scripts
have no user session to enable a user unit in, so it would have to ship the unit to
`/usr/lib/systemd/user`, enable it globally for every account on the machine, and move the product
into system paths. That is the opposite of today's per-user layout. An AppImage cannot install a unit
at all and would need this same script beside it.

The other two measurements did not decide it. They are what the existing installer already handles
with one permission prompt each, and the tarball reuses that unchanged: ffmpeg comes from the
distribution, and libvlc wants root. A `.deb`'s `Depends:` would handle those two more natively, and
that is the one thing this choice gives up.

## What changed

| File | Change |
|------|--------|
| `scripts/linux/build-release.sh` (new) | Builds both assets and `asset-versions-linux.json`. The release's linux job and `ci.yml` run exactly this. |
| `scripts/linux/install-release.sh` (new) | Template for `agenteyes-install-linux-x64.sh`; `@VERSION@` is baked in at build time, so a script always installs its own release. Steps: download (or `--release-dir`), read the manifest entry (python3), compare size and SHA-256, then extract and run `install.sh --prebuilt`. Any mismatch exits 1 before extraction. |
| `scripts/linux/install.sh` | Adds `--prebuilt DIR` (installs without building and needs no .NET) and `--no-signin`. Unknown options are now refused; before, they were silently ignored. A `--prebuilt` folder is never deleted. The icon is copied after the app copy, so it is never written into the input. |
| `scripts/linux/release-smoke.sh` (new) | Runs the clean-container install described under criterion 2. |
| `scripts/write-manifest.ps1` | `-VersionsFile` accepts several sidecars, which must share one version, and no asset may appear twice. Each asset's `platform` now comes from its name: `-win-x64` gives windows, `-linux-x64` gives linux, and anything else is refused. |
| `.github/workflows/release.yml` | New `linux` job: Linux tests with the opt-in ffmpeg tests, build, smoke, upload. The Windows `release` job now `needs: linux`. It folds the Linux assets in after signing and before the manifest. A new step checks that the published manifest's Linux entries equal the smoke-tested ones. "Verify completeness" now checks the set is EXACT: nothing missing, nothing extra, every asset hashed. The publish step's own exact list names the Linux assets. `workflow_dispatch` is a dry run: the tag check and the publish step are skipped, and the would-be release is uploaded as an artifact. |
| `.github/workflows/ci.yml` | The linux job builds the Linux asset on every push. |
| `tests/AgentEyes.Tests/SetupEngineTests.cs` | Adds `Planner_ManifestWithLinuxAssets_PlansOnlyTheWindowsComponents`. |
| `README.md` | New "Linux" install section. |

## Acceptance criteria (Linux)

### 2. A Linux asset is published and installs on a clean machine, unit started, /health answering

- **Published:** by the tag-push path of `release.yml`, which was not run because no release was
  cut. Every step except the tag check and the publish ran in dispatch dry run
  **44052244930** (green). The release it would have published is that run's `release-dry-run`
  artifact: 5 Windows assets, the 2 Linux assets and the manifest.
- **Download URL shape:**
  `https://github.com/thefrederiksen/agenteyes-app/releases/latest/download/agenteyes-install-linux-x64.sh`.
  The script then fetches
  `https://github.com/thefrederiksen/agenteyes-app/releases/download/v<version>/{release-manifest.json,agenteyes-linux-x64.tar.gz}`.
  **NOT EXERCISED:** that URL answers 404 until a release carrying the asset is cut. The script's curl
  branch is therefore unmeasured. Everything after the fetch (verify, extract, install) is the code
  path measured below. Verify the URL right after the first cut.
- **The clean machine, and how clean it really is.** It is a new container from the public
  `ubuntu:24.04` image, with systemd as PID 1 and one new user, `tester`, who has never run AgentEyes.
  Before the install the transcript prints `dotnet`, `ffmpeg` and `agenteyes` as "not installed", and
  no `~/.local/lib/agenteyes` and no unit. It is NOT the development machine, which has AgentEyes
  installed and live. That install was never stopped, replaced or touched: every smoke ran inside its
  own container, and the host's `/health` answered before and after.

  What the container is given that a person's machine would not need, and what a genuinely clean
  desktop might do differently:
  - systemd, dbus, sudo, curl, python3 and x11-xserver-utils are preinstalled. A desktop Ubuntu has
    these.
  - **Passwordless sudo stands in for the permission prompt.** A person sees sudo or pkexec ask for
    the ffmpeg and libvlc installs. That prompt path is not exercised here.
  - **Xvfb `:99` stands in for the X session.** Measured: without a display, the service refuses to
    start ("DISPLAY is not set, so there is no X screen to record") and restarts every 10 s. That is
    correct behaviour. A real desktop also has a real GPU, compositor and audio server, and no
    recording was made in this test.
  - **Sign-in is skipped with `--no-signin`** because it opens a browser and waits for a person. A
    real install ends with that browser step.
  - **Apt package lists come from the image build.** FINDING, not fixed here: `install.sh` runs
    `apt-get install` and never `apt-get update`. A machine with empty package lists failed with
    "Unable to locate package ffmpeg" (measured on the first container attempt). An Ubuntu desktop
    normally has lists, but stale ones are possible.
  - **Wayland is refused by design** ("log in with Ubuntu on Xorg"). The release notes must say so.
    That is not this issue.
- **Evidence:** the CI dry run's linux job log, step "Install the Linux asset on a clean machine",
  contains this transcript, abridged:
  ```
  [OK] container agenteyes-release-smoke-4176 up, systemd running
    dotnet    not installed
    ffmpeg    not installed
    agenteyes not installed
  == 2. the real download installs ==
    | [OK] verified agenteyes-linux-x64.tar.gz against release-manifest.json (sha256 cf671d79...c50c, 86898932 bytes)
    | [OK] ffmpeg installed
    | [OK] video playback library installed
    | [OK] installed /home/tester/.local/lib/agenteyes, ..., unit /home/tester/.config/systemd/user/agenteyes.service
    | [OK] service is up: {"ok":true,"app":"AgentEyes","host":"headless","platform":"linux"}
    installer exit code: 0
    systemctl --user is-active agenteyes.service: active
    GET http://127.0.0.1:7882/health: {"ok":true,"app":"AgentEyes","host":"headless","platform":"linux"}
    ffmpeg: /usr/bin/ffmpeg, from package ffmpeg
    dotnet: not installed - the asset needs no .NET
    app:    3466 /home/tester/.local/lib/agenteyes-app/AgentEyesDesktop
  ```
  The `release-dry-run` artifact itself (the CI-built bytes, under the Windows-written 7-asset
  manifest) was then downloaded and installed by the same script in a new container on the
  development machine. Result: exit 0, unit active, same `/health`.

### 3. In release-manifest.json with SHA-256; install path verifies; shown failing on a corrupted download

- The dry run's manifest has `agenteyes-linux-x64.tar.gz` (sha256 `cf671d79...c50c`, 86898932
  bytes, platform linux) and `agenteyes-install-linux-x64.sh` (sha256 `7b8c6bea...486b`, 5121 bytes,
  platform linux), next to the 5 Windows entries, now with a derived platform.
- **The refusal, measured three times** (local build, CI dry run, CI artifact): ONE byte of a copy of
  the tarball is XOR-ed by 1, then the installer is run on it. From the CI log:
  ```
  changed 1 byte at offset 43449466: 0xfe -> 0xff
  original:  cf671d799cf5b80986ef635a04299b0a197e353433e279c929d6b4859d14c50c
  corrupted: 20c3450571fc5982495bee7c3d61ae9ea169210198fc22b85f2863a8ba811fc6
  | [FAIL] agenteyes-linux-x64.tar.gz does NOT match release-manifest.json - it is corrupt or has been altered.
  |        expected: sha256 cf671d79...c50c, 86898932 bytes
  |        got:      sha256 20c34505...11fc6, 86898932 bytes
  |        Nothing was extracted and nothing was installed. Download it again.
  installer exit code: 1
  [OK] corrupted download refused (exit 1), nothing installed
  ```
  The size is unchanged, so the hash alone refused it. "Nothing installed" is asserted, not just
  printed: the smoke fails if `~/.local/lib/agenteyes` or the unit file exists after the refusal.
- **Not covered:** the manifest itself is trusted as served over HTTPS from the same release. That is
  the same trust model as Windows. Nothing signs the manifest.

### 4. The updater's behaviour, stated and shown

- **Linux: there is no in-app updater.** Shown by source: the only updater is
  `src/AgentEyes.App/UpdateChecker.cs`, which hands over to the setup engine. Both are Windows-only
  (`net8.0-windows*`). Neither `AgentEyes.DesktopApp` nor the Linux build of `AgentEyes.Core` contains
  any release-channel code: a grep for `ReleaseSource`, `releases/latest` and `api.github.com` over
  `src/AgentEyes.Core`, `src/AgentEyes.DesktopApp` and `src/AgentEyes.LinuxApp` finds nothing. **To
  update on Linux, the person downloads the newer release's `agenteyes-install-linux-x64.sh` and runs
  it again.** `install.sh` stops the unit (a running recording is stopped and kept), swaps both
  folders and restarts. The README says so. A Linux install never learns a newer release exists: an
  update notice is a gap, not built here.
- **A platform whose asset is absent (macOS today):** nothing reads a macOS entry, because macOS has
  no updater either (`scripts/mac/install.sh` installs from source). For the Windows updater, absence
  and presence of other platforms' entries are both inert. `UpdatePlanner` looks up exactly its four
  fixed Windows asset names (`ComponentRegistry.All`) and ignores every other entry. Shown by:
  - **The new test** `Planner_ManifestWithLinuxAssets_PlansOnlyTheWindowsComponents`: a 7-asset
    manifest plans exactly `app, cli, setup-cli, ffmpeg`, plans no Linux asset, and parses the Linux
    platform as `linux`. It cannot be compiled on Linux (the SDK here has no WindowsDesktop targets).
    It ran on the Windows CI: the `AgentEyes.Tests` count went from 2539 (2543 with the ffmpeg tests)
    on main's last CI run, 43976454882, to 2540 (2544) on this branch, failed 0. That is +1 test.
  - **The real thing:** the dry run's step "Smoke-test an offline install from the built release" ran
    the setup CLI's `install --release-dir dist/release` against the 7-asset manifest and passed.
  - **Already-installed v1.12.0 Windows apps** will read the next release's mixed manifest with the
    same parser and planner. `tools/AgentEyes.Setup.Engine` is byte-identical to tag v1.12.0
    (`git diff --quiet v1.12.0 HEAD -- tools/AgentEyes.Setup.Engine`).

### 5. A platform whose asset failed to build fails the release (extended, not loosened)

- **Structurally:** the Windows job `needs: linux`. If the Linux tests, the build, the
  corrupted-download refusal or the clean install fail, the Windows job never starts, and the publish
  step is in the Windows job. A Linux failure means no release at all.
- **Exact set, extended:** "Verify completeness" was only a presence check. It now also fails on any
  unexpected file and on any asset with no manifest entry. The publish step's own `Compare-Object`
  exact check names the 2 Linux assets too. Nothing was loosened: every original name and both checks
  remain.
- **The manifest writer cannot drop one silently:** a missing asset throws (as before), and so do an
  unplatformed name, a duplicate and a version mismatch between sidecars. The unplatformed-name
  refusal was run locally under pwsh: `asset 'mystery.bin' names no platform`.
- **The published manifest is the tested one:** a step fails if the final manifest's Linux
  sha256, size or platform differs from what the Linux smoke installed.
- **Not shown:** a run where the Linux job FAILS and the Windows job is then skipped. That is GitHub's
  `needs:` semantics, not code here. QA may want to see it, for example with a dispatch on a scratch
  commit that breaks `build-release.sh`.

### 6. CI green across more than one run

Head da0b8da: `ci` push **44052237424** green and `ci` pull_request **44052286161** green, each on
windows, linux and macos. The `release` dry run **44052244930** is green. A second release dry run on
the handoff commit is recorded in the PR comment, along with the CI runs on that commit.

## Tests run (by the developer, with counts)

- `AGENTEYES_FFMPEG_TESTS=1 dotnet test tests/AgentEyes.Tests.Linux -c Release`: 474 total, 473
  passed, 1 skipped, failed 0 on 9 consecutive runs. **The first run had 1 failure (472 passed),
  unidentified.** It ran concurrently with a parallel build of another project, and its name was not
  captured. This change touches no code the Linux suite compiles.
- `dotnet test tests/AgentEyes.Tests.DesktopApp -c Release`: 23 passed.
- `install.sh` from source (the path every fleet machine uses, changed here) was run in a fresh
  container: the same image plus `dotnet-sdk-8.0`, from `git archive HEAD`, with `--no-signin`.
  Result: rc 0, both builds made, unit `active`, `/health` ok, `agenteyes.png` present in the app
  folder, and its two temporary build folders removed (0 `/tmp/tmp.*` left). `install.sh --bogus` and
  `--prebuilt /nonexistent-dir` both exit 1 with a clear message before doing anything.
- `dotnet build/test AgentEyes.sln` cannot run on Linux (WPF). Its gate ran on the Windows CI job:
  2540 and 2544 tests, failed 0, as counted above. Mac on CI: 432 total, failed 0.

## How QA should test

1. `gh workflow run release.yml --ref issue-194-linux-release-asset` (a dry run; it publishes
   nothing). Confirm both jobs are green and these steps are SKIPPED: "Verify tag matches project
   version" and "Publish release to this repo's own releases". Confirm `gh release list` and the
   remote tags are unchanged.
2. `gh run download <run> --name release-dry-run --dir <empty dir>`. Expect exactly 8 files. Then
   `scripts/linux/release-smoke.sh <that dir>` on a Linux machine with docker. It must print the
   one-byte refusal (exit 1, nothing installed), then `active` and `/health` ok. It takes about 2-5
   minutes, needs network for apt, and runs entirely in a container. It does NOT touch a host install
   or port 7882 on the host.
3. Try loosening: delete a Linux name from either asset list in `release.yml` or from
   `build-release.sh`, and a dry run must fail.
4. Do NOT run the bootstrap script directly on a machine with a live install unless you mean to
   replace that install.

CenCon impact: no change to the component map or the privacy posture (visible and controllable). The
installed product is identical to a source install; only where the bits come from changes.

## What the macOS half still needs (left open on #194)

- #181 first: Developer ID signing, hardened runtime and notarisation. An unsigned or ad-hoc
  download is worse than none.
- Then a `macos` job in `release.yml` that builds `AgentEyes.app` plus the service, signs and
  notarises them, and produces a `-mac-arm64` / `-mac-x64` asset. `write-manifest.ps1`'s name rule
  needs a `-mac-` case; until then, a macOS asset name is REFUSED by the manifest writer, which is
  intended. The Windows job would `needs:` it, and both exact lists would name it.
- Criterion 1 evidence: a never-had-AgentEyes Mac, the Screen Recording prompt, and the install
  transcript. Also criterion 3's corrupted-download refusal for the macOS install path.

I believe the Linux half is finished.
