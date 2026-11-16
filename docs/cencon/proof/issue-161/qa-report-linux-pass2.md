# Issue #161 pass 2 - QA report, LINUX (devlinux)

- **Verdict: Linux PASS on the contract of the Lead's ruling (6924190378) and the N1 ruling.**
  `flow:ready-qa` is LEFT in place: Windows (the derivation) is still owed and is being routed separately.
- **Measured defeats of the guard (F1, F2) and an unpinned half of the union (M-A).** None of them
  is a regression from pass 1. The Lead has ruled on all three, and each goes to a pass 3 (section 9).
- **Head tested:** `f9aba6a` (PR #175; last code change `b780cd8`, and `f9aba6a` adds only `handoff.md`).
- **Binary run:** `agenteyes.dll` built here at that head, which embeds `1.11.11+f9aba6aa2f645cb387c54ede02c73d580ee8de04`.
  - The merged-tree binary embeds `1.11.11+29334b32f74ff9e7abe6a8a2165c77be0a8b9953`. That is QA's local trial merge commit, never pushed.
- **Instrument:** every AgentEyes process I started, every build and every test ran under a Landlock deny on BOTH real
  roots. The harness is `qa-linux/harness/lldeny.c`, rebuilt and byte-identical to `linux-pass2/lldeny.c`. Product runs
  also ran under `strace -f`. MSBuild node reuse was OFF throughout, because a foreign, unconfined MSBuild node (pid
  1846471) was alive and a reused node would have run my build steps outside the sandbox.
- **Artefacts** are in `qa-linux-pass2/`, and the harness scripts in `qa-linux-pass2/harness/`.
  - Paths are scrubbed: `<home>` is the owner's home, `<owner>` the account, `<uid>` its uid, `<scratch>`/`<S>` my scratch folder.
  - Identity: no email or name appears in any committed file (grep for them = 0 hits, with a positive control).

## Verdict per job

| # | Job | Linux verdict |
|---|-----|---------------|
| 0 | Instrument | Control proven FIRST: `ls`/`touch` on both real roots and the live-log folder are EACCES, scratch is writable, strace saw all 15 real-root accesses. A same-run positive control (P0, O2) accompanies every absence below. |
| 1 | The attack + x1-x4 | **PASS.** The exact attack (with `serve`) and x1-x4 all exit 5 with **0 opens** of a real root. x2, x3, x4 name ONLY the passwd derivation, so the account half alone refused them. Eight more spoofs are refused (y1-y8). **The union holds** (section 1.3), but only its account half is pinned (**M-A**). **F1** and **F2** are measured defeats. |
| 2 | N1 | **PASS.** Missing, 0555 and file values are refused (exit 5) by a REAL write, and nothing is created. A writable folder in a 0555 parent is correctly ACCEPTED. **ORDER:** the overlap refusal wins and there are **zero probe syscalls** on every overlap-refused start. The M-C mutation (probe first) is caught by 2 tests. |
| 3 | Banner | **PASS.** stderr, the WARN log line and the serve stdout print the SAME list `Decide` compared against (`LibraryRedirect.cs:311`, `:328`, `:350`). It is not re-derived. A symlinked root prints its resolved target, and a refusal names that same resolved path. Wording problems are F1. |
| 4 | Merged tree | **Does NOT merge clean** with current main 277a9bc: 5 add/add conflicts (test csprojs and the IL pin list). With QA's union resolution, the merged tree builds and passes the Linux suite at 494 passed, 1 failed, 2 skipped. The 1 failure is #156's `sg` test under Landlock (no_new_privs) and fails identically on main alone. DesktopApp tests: 25/25. The merged `AgentEyesDesktop` exits 5 redirected, partial and spoofed, with 0 opens and no connect to 7882. |
| 5 | Port guards, rest of pass 1 | **PASS.** The port-guard bodies are byte-identical to c595c41, and pass 2's only change in these files is the banner. Re-run: a redirected CLI is refused by an unredirected service after ONE `GET /status` (and sends no POST on `record start`). An unredirected CLI still drives one. A redirected `serve` is refused before `bind`. |
| 6 | IL pins, collection | **PASS.** The real guard failed on CI on exactly the 6 pass-2 lines before f063cb7 pinned them. Mutating an import or a write makes the guard's own scanner report FOUND-not-PINNED, and the stale pin too. The xunit collection is serialisation, not a port fix (#174). |
| 7 | Gates | **PASS.** Local results: Linux 387 passed / 1 skipped (388 with the opt-in), DesktopApp 17/17, Mac suite compiles. CI run 43929135704 attempts 1 AND 2 are two distinct green runs on all three platforms, macOS 351/0 in each. Attempt 2 is a same-commit re-run. The run at f9aba6a itself still has macOS QUEUED. |

## 0. The instrument (`qa-linux-pass2/00-landlock-control.txt`)

`lldeny <home>/Videos/AgentEyes <home>/.local/share/AgentEyes -- strace -f control.sh` produced:
- `ls` and `touch` on both real roots and on `.../AgentEyes/logs`: **Permission denied**;
- `touch` on scratch: exit 0;
- in strace, 15 lines naming a real root, 6 of them EACCES. The instrument SEES a real-root access.

Landlock does not refuse `stat`, so every run below also counts syscalls by kind.

## 1. The attack, the matrix, the union, and the guard defeats

### 1.1 The predecessor's matrix, verbatim (`01-predecessor-matrix.txt`)

I ran `qa-linux/harness/matrix.sh`, unchanged, at f9aba6a:
- **All 20 cases:** every case except the positive control `p` exits 5; `p` exits 0. **Zero opens or creates of a real
  root in any case.** The only real-root syscalls are 2 `newfstatat` + 1-2 `readlink` on the cases whose requested
  path IS or links to a real root. That is pass 1's link resolution of the REQUESTED path, unchanged.
- **x1** (control, real HOME): exit 5. Its refusal names both derivations: `passwd entry for uid <uid>, home <home>, XDG default + this process's environment`.
- **x2** (`HOME=<scratch>`), **x3** (`XDG_DATA_HOME=<scratch>`) and **x4** (`HOME=<scratch>`, VIDEOS=`<home>/Videos`) were
  all ACCEPTED at c595c41. All three now exit 5, and their refusals name ONLY `passwd entry for uid <uid> ...`. The
  environment half named a scratch folder, so **the account half alone refused them**, as the developer reported.
- **d** (missing folders) and **e** (0555) were accepted at c595c41 and are now refused by N1.
- **Probe files:** they appear only in `e` and `p`, the two cases that passed the overlap rules.

### 1.2 The exact attack, and eight more spoofs (`02-pass2-attacks.txt`, harness `p2matrix.sh`)

| Case | Spoof | Result |
|------|-------|--------|
| **P0** | positive control: no redirect, `--help` | exit 0. **6 opens/creates of the real state root**, the live log among them, all EACCES. The instrument sees opens in this run. |
| **ATK** | `HOME=<scratch> DATA=<home>/.local/share VIDEOS=<scratch> agenteyes serve --port 17163` | **exit 5, 0 opens** (3 metadata syscalls) |
| y1 | HOME + XDG_DATA_HOME + XDG_CONFIG_HOME all scratch, DATA=`<home>/.local/share` | exit 5, 0 opens |
| y2 | the same, VIDEOS=`<home>/Videos` | exit 5 (`XDG_VIDEOS_DIR in <home>/.config/user-dirs.dirs`) |
| y3 | `HOME` unset | exit 5 |
| y4 | `DATA=<scratch>/../../../../../../../../../..<home>/.local/share` | exit 5 (normalised) |
| y5 | DATA = a scratch symlink to `<home>/.local/share`, HOME spoofed | exit 5 (`which resolves to`) |
| y6 | DATA=`<home>/Videos`, so the state root lands ON the real recordings root | exit 5 (cross-root overlap) |
| y7 | `DATA=/proc/self/cwd` with cwd = `<home>/.local/share` | exit 5 (`/proc/self/cwd/AgentEyes which resolves to <home>/.local/share/AgentEyes`) |
| y8 | `DATA=/proc/self/root<home>/.local/share` | exit 5 |

### 1.3 Does the union really "only add refusals"? Judgment

**Yes, in the code.**
- `RealLibrary.DeriveForThisProcess` (`RealLibrary.cs:75-93`) builds one list in three steps:
  - the account's roots from the passwd entry of the effective uid, ALWAYS on Linux and macOS (no passwd entry means it throws, and the redirect is refused);
  - plus the environment's roots (pass 1's `MachineLocalAppData` / `MachineVideos`), each kept when fully qualified - the same skip pass 1 had;
  - plus the `SUDO_UID` account's roots, which only add.
- Deduplication merges only identical `(Name, Parent)` pairs and keeps every derivation string.
- `Decide` refuses on an overlap with ANY root in the list (`LibraryRedirect.cs:208-228`).
- So the pass-2 list is a superset of pass 1's list, and the refusals are a superset of pass 1's refusals. N1 and a
  failed derivation add refusals of their own.

Measured: M-B (drop the account half) is caught by 2 real-process tests. x2/x3/x4/y1/y2 show the account half refusing
with the environment fully spoofed. **zc/zvc** show the environment half refusing a relocated library that the account
half cannot see.

**But only the account half is pinned. M-A** (`RealLibrary.cs:83`: pass `"", ""` instead of
`AppDataPaths.MachineLocalAppData, AppDataPaths.MachineVideos`) leaves **all 69 redirect tests green**
(`08-union-mutations.txt`). The only test that names the environment half,
`Derive_SpoofedHome_StillProtectsTheAccountHomeLibrary` (`LibraryRedirectTests.cs:304`), calls the pure `Derive` with
the values passed in, so it cannot see the process-level call drop them. Dropping that half removes the ONLY protection
a relocated library has (F1), and no test notices.

**And the union protects only what it can name.** The claim "a spoofed environment cannot remove the account's [roots]"
(`ISOLATING_A_REAL_PROCESS.md:50`) is true. It does not mean "the person's library cannot be removed from protection".
When the person's library is relocated, a spoofed environment removes it (F1).

### 1.4 F1 - a relocated library is not protected under a spoofed environment (MEASURED)

The developer's handoff 8.1 predicted this. I measured it with the library simulated in scratch, so the reproduction
below runs anywhere and touches nothing real:

```
S=$(mktemp -d); mkdir -p $S/reloc/xdg/AgentEyes $S/reloc/cfg $S/reloc/vids/AgentEyes $S/spoof $S/v $S/d $S/cfg2
echo person-library > $S/reloc/xdg/AgentEyes/MARKER
printf 'XDG_VIDEOS_DIR="%s"\n' "$S/reloc/vids" > $S/reloc/cfg/user-dirs.dirs
# zc  CONTROL: the person's environment names it -> REFUSED, exit 5 ("derived from: this process's environment")
XDG_DATA_HOME=$S/reloc/xdg AGENTEYES_REDIRECT_DATA=$S/reloc/xdg AGENTEYES_REDIRECT_VIDEOS=$S/v agenteyes --help
# za  DEFEAT: the same redirect with XDG_DATA_HOME spoofed -> ACCEPTED, exit 0; the log lands IN the person's library
XDG_DATA_HOME=$S/spoof AGENTEYES_REDIRECT_DATA=$S/reloc/xdg AGENTEYES_REDIRECT_VIDEOS=$S/v agenteyes --help
ls $S/reloc/xdg/AgentEyes        # -> logs/AgentEyes-YYYYMMDD.log  MARKER
# zvc CONTROL / zva DEFEAT for recordings: XDG_CONFIG_HOME=$S/reloc/cfg -> refused; XDG_CONFIG_HOME=$S/cfg2 -> ACCEPTED
XDG_CONFIG_HOME=$S/cfg2 AGENTEYES_REDIRECT_DATA=$S/d AGENTEYES_REDIRECT_VIDEOS=$S/reloc/vids agenteyes --help
```

Measured at f9aba6a: zc exit 5, za **exit 0** (the person's state root then held `./logs/AgentEyes-20261116.log` beside
`MARKER`), zvc exit 5, zva **exit 0**.

The ACCEPTED banners then print "real library kept out". `serve` prints "this is NOT the person's real library"
(`HeadlessHost.cs:89`) about a folder that IS the person's library. That is the wording the Lead has ruled must change.

**On this machine F1 does not bite today.** Neither the owner's service, the tray, my shell nor the systemd user manager
sets `XDG_DATA_HOME` or `XDG_CONFIG_HOME`. The owner's library is at the XDG defaults, which the account half names.

**Is a stronger rule available that keeps scratch-under-home working?** The Lead asked. I think two are. Neither
refuses a path merely for being under the home, and each only ADDS refusals:

1. **Ask the environments the seat did not set, not just its own.** The person's real service decides where the real
   library is. Its environment can be read without trusting the seat's:
   - `/proc/<pid>/environ` of the euid's running AgentEyes service (`XDG_DATA_HOME`, `XDG_CONFIG_HOME`, `HOME`).
   - The systemd user manager's environment, reached by a path derived from the uid (`/run/user/<uid>/...`), not from
     `$XDG_RUNTIME_DIR`. This is where `environment.d` relocations live.
   - The `DataRoot` / `RecordingsRoot` of a post-#161 service on `127.0.0.1:7882`.

   Add those roots to the union. That covers a relocated library whenever the person's service is installed or running.
   The residue is a relocation set only in a shell profile, with no service. Write that residue into the doc.
2. **Recognise a library by its content, not its path.** On first redirected use, write a marker
   (e.g. `.agenteyes-scratch-library`) into `<value>/AgentEyes`. Refuse an EXISTING, non-empty `<value>/AgentEyes`
   that lacks the marker. The refusal would say: "this folder already holds an AgentEyes library that was not created
   as a scratch library".
   - A fresh scratch folder (scratch-under-home included) is empty, so it passes and is marked.
   - A real library, relocated or not, has no marker, so it is refused.
   - It costs one directory read of the requested folder, which is metadata. It must run AFTER the overlap rules, like N1.
   - It also closes the cheapest F2 shape: a scratch root a seat populated by hand.

### 1.5 F2 - a link INSIDE a scratch root reaches the real library (MEASURED; Lead: DEFECT)

`Decide` resolves links only on the path TO each redirected root (`LibraryRedirect.cs:193-194`). It compares those
paths alone (`:195`, `:208-228`). Nothing under `<value>/AgentEyes` is examined.

**Against the real library, under Landlock (L1, `02-pass2-attacks.txt`):**
- Setup: `<scratch>/L1/data/AgentEyes/logs -> <home>/.local/share/AgentEyes/logs`; the redirect is otherwise all scratch.
- Result: **exit 0** (accepted). The banner says `real library kept out ...`.
- strace shows the redirected process went through the link:
  - `mkdir("<scratch>/L1/data/AgentEyes/logs") = EEXIST` x5;
  - `openat("<scratch>/L1/data/AgentEyes/logs/AgentEyes-20261116.log", O_WRONLY|O_CREAT) = -1 EACCES` x5.

  That file is the owner's live log, reached through the link, and the EACCES is Landlock's.

**This is why my real-root grep said 0.** strace prints the path the process used. A grep of real-root path strings
CANNOT see a link-borne access, so an "0 opens of a real root" count is blind to this whole class unless the denial
itself is looked for.

**L2:** a link `<scratch>/L2/videos/AgentEyes/linked-real -> <home>/Videos/AgentEyes` was run under a 35 s `serve`.
- The repair pass and housekeeper ran on the scratch root and `stat`ed through the link 24 times (12 ok, 12 ENOENT).
- They opened nothing, because the link pointed at the root folder, not at one recording.
- A link to ONE real recording folder is the dangerous shape: the repair pass then works on a real recording. I did
  not run that against the owner's recordings.

**Scratch-only reproduction for the developer** (`02b-f2-scratch-repro.txt`): it needs no sandbox, and the bug is
visible as a file growing.

```
S=$(mktemp -d); D=$(date +%Y%m%d)
mkdir -p $S/fake-real/AgentEyes/logs $S/data/AgentEyes $S/videos
echo person-log-line > $S/fake-real/AgentEyes/logs/AgentEyes-$D.log
ln -s $S/fake-real/AgentEyes/logs $S/data/AgentEyes/logs
# control (protected): XDG_DATA_HOME=$S/fake-real AGENTEYES_REDIRECT_DATA=$S/fake-real AGENTEYES_REDIRECT_VIDEOS=$S/videos agenteyes --help -> exit 5
XDG_DATA_HOME=$S/fake-real AGENTEYES_REDIRECT_DATA=$S/data AGENTEYES_REDIRECT_VIDEOS=$S/videos agenteyes --help
# BUG at f9aba6a: exit 0; the banner lists <S>/fake-real/AgentEyes as "real library kept out";
#                 the protected log went from 1 line to 6
```

**Variants pass 3 should cover:**
- links deeper down (`recordings/<one recording>`, `models`, `run/ffmpeg`);
- FILE links (`config.json`, the credential);
- **hard links**. `/tmp` and `/home` are ONE filesystem on this machine, so a hard-linked credential inside a scratch
  root is possible, and no symlink resolution sees it. A dev/ino comparison would. NOT measured, because creating a
  hard link changes the real file's link count.

### 1.6 The other uncovered cases the developer listed

- **Root without sudo: NOT measured, and not reachable unprivileged here.**
  - `kernel.apparmor_restrict_unprivileged_userns=1`, so `unshare -Ur` and `bwrap --uid 0` both fail with
    `uid_map: Permission denied`.
  - By reading: with euid 0 and no `SUDO_UID`, the account half names root's home and the environment half names
    whatever `HOME` says. A spoofed `HOME` therefore leaves the owner's library unprotected, which is pass 1's defect
    for root.
  - The account here is in the `docker` group, so "root without sudo" is one `docker run -u 0 -v <home>:...` away. I
    did not run that, because a daemon-spawned process escapes Landlock.
  - Also for pass 3: `doas` (`DOAS_USER`) and `pkexec` (`PKEXEC_UID`) set no `SUDO_UID`.
- **Windows derivation: NOT covered.** It is routed separately.

## 2. N1 - missing and unwritable, and the ORDER (`03-n1-missing-unwritable-order.txt`)

| Case | Result | Probe syscalls (in order) | Created |
|------|--------|---------------------------|---------|
| n1 data missing | exit 5 `does not exist ... Fix: create it (mkdir -p '...')` | none | nothing |
| n2 videos missing | exit 5 | data: `openat(O_CREAT\|O_EXCL)=fd`, `unlink=0` | nothing left |
| n3 videos 0555 | exit 5 `is not writable: a test write of ... failed (UnauthorizedAccessException ...)` | data ok, then videos `openat = -1 EACCES` | nothing left |
| **n4 both folders writable, their PARENT 0555** | **exit 0 (correct)** | both ok + unlink | `data/AgentEyes/logs/...` only |
| n5 missing, parent 0555 | exit 5 `does not exist` | none | nothing |
| n6 value is a file | exit 5 `is a file, not a folder` | none | nothing |
| **n7 ORDER: VIDEOS=`<home>/Videos` AND data 0555** | **exit 5 on the OVERLAP rule** (not on N1) | **none, anywhere** | nothing |

Further evidence on the order:
- **Matrix:** no probe syscall in ANY overlap-refused case (f, g, g2, h, i, i2, x1-x4, ATK, y1-y8).
- **Code:** the probe is the last block of `Decide` (`LibraryRedirect.cs:235-242`), after the overlap loop (`:213-228`).
- **Pin:** the M-C mutation (probe moved before the overlap rules) fails
  `Decide_UsableFolderCheck_RunsOnlyAfterEveryOverlapRuleSoItNeverProbesTheRealLibrary`, and also the spoofed-HOME
  real-process test, whose probe into `<home>/Videos` hit Landlock first.
- **No residue after M-C:** `find <home>/Videos <home>/.local/share -maxdepth 1 -name '.agenteyes-redirect-probe*'`
  returns 0. Positive control: a probe planted in scratch is found.

## 3. The banner (`04-banner.txt`)

The run: `serve --port 17377` with `HOME` spoofed, `XDG_DATA_HOME=<scratch>/xlink` (a symlink to
`<scratch>/xtarget`), and DATA/VIDEOS on scratch.

- **Same text on every surface.** stderr, the `[WARN] [LibraryRedirect]` log line and the serve stdout all print the
  same three roots:
  - passwd state: `<home>/.local/share/AgentEyes [passwd entry ..., XDG default]`
  - passwd recordings: `<home>/Videos/AgentEyes [..., XDG_VIDEOS_DIR in <home>/.config/user-dirs.dirs]`
  - environment state: `<scratch>/xlink/AgentEyes (which resolves to <scratch>/xtarget/AgentEyes) [this process's environment]`

  The environment recordings root is absent, as it should be: there is no Videos folder under the spoofed HOME, so
  .NET answers "" (#169).
- **`/status` matches:** `DataRoot`/`RecordingsRoot` are the scratch roots, `LibraryRedirected: true`, and
  `LibraryRedirect: environment (...)`. The whole serve made 0 real-root syscalls.
- **Control:** the same environment with DATA=`<scratch>/xtarget` is refused with "overlaps the REAL state root
  `<scratch>/xtarget/AgentEyes`", the very resolved path the banner printed.
- **The list is the one used, not re-derived.** `realRoots` is derived once (`LibraryRedirect.cs:311`), passed to
  `Decide` (`:328`), and the banner list is built from that same object (`:350`).
  - One nuance, not a defect: link resolution runs a second time for display (`:351` vs `:210`), with the same
    function and input. A link swapped in between would desynchronise them.
- **Wording:** `HeadlessHost.cs:89` asserts "this is NOT the person's real library", which the guard cannot establish (F1). The Lead has ruled it changes.

## 4. The merged tree (`05-merged-tree.txt`)

`git merge origin/main` (277a9bc: #118 + #156) into f9aba6a **conflicts in 5 files**. GitHub agrees: PR #175 reports
`mergeable: CONFLICTING`.
- **Csprojs:** `AgentEyes.Tests.DesktopApp.csproj`, `AgentEyes.Tests.Linux.csproj`, `AgentEyes.Tests.Mac.csproj` and
  `AgentEyes.Tests.csproj`. #161's test links collide with #118's `MacBundleTests` and #156's
  `CaptureOwnership`/`CaptureWriterReap`/`ProcessIdentity` tests. `MuxLockTests` is on both sides.
- **`ManifestWriterIlTests.cs`:** #161's `libc.so.6!geteuid/getpwuid_r` pins collide with #156's
  `libc!kill/geteuid/getuid/statx/sysctl` and `SystemNative_Stat`.

Every conflict is add/add at the same anchor. QA resolved each as the union of both sides (`MuxLockTests` kept once).
That is a resolution for measurement, NOT the branch's; the merge is pass 3's. On that tree:
- **Entry points:** exactly one `ApplyFromEnvironment` per entry point (Core `Program.cs:30`, `App.xaml.cs:68`,
  `DesktopApp/Program.cs:37`). There is no duplicate, and main added no new `Main`. #156's new host-id file is under
  `AppDataPaths.Root`, so it is redirected.
- **Builds:** Linux, DesktopApp and Mac test projects all build with 0 errors.
- **Linux suite: 494 passed, 1 failed, 2 skipped (497).** DesktopApp: **25/25**.
- **The 1 failure is a test-environment interaction, not a defect.** It is
  `ProcessIdentityTests.ReadLinuxProcOwner_LiveSubjectWhoseUidAndGidDiffer_ReadsUidNotGid` (#156), which runs
  `sg adm`. `sg` is setgid, and Landlock sets `no_new_privs`, under which setgid is ignored:
  - `sg adm -c 'id -g'` outside the sandbox prints 4; inside it prints `setgid: Operation not permitted`.
  - The same test fails identically on **pure main 277a9bc** under the same sandbox (424 passed, 1 failed, 2 skipped, 427 total).
  - 497 = 427 + 70 (#161's Linux tests). **Any seat running the suite under Landlock will see this one failure.**
- **Merged `AgentEyesDesktop`:** under strace, redirected, partial and spoofed-HOME-onto-real all exit 5 with 0 opens.
  The redirected refusal names `http://127.0.0.1:7882`. **There is no connect to 7882**, and the grep for it was shown
  able to see a connect (curl to closed port 17999 gives 1 hit).

## 5. Port guards and the rest of pass 1 (`06-port-guards.txt`, `06b-port-guards-diff.txt`)

**By diff (c595c41..b780cd8):**
- `HeadlessCli.cs`, `RecordingService.cs` (/status), `App.xaml.cs`, `Program.cs`, `CliDispatcher.cs` and `AppDataPaths.cs` are unchanged.
- `ServicePortRefusal` (both overloads) and `ServiceLibraryRefusal` are byte-identical.
- `HeadlessHost.cs` changed. The handoff's "untouched" list does not name it, but the change is only the serve-stdout
  banner (lines 84-92).
- The only other non-`Decide` hunk in `LibraryRedirect.cs` makes `Normalize` internal.

**Re-run under strace with the HTTP request lines captured:**

| Case | Service | Result |
|------|---------|--------|
| G1 redirected `record status` | stand-in: UNREDIRECTED, post-#161, scratch roots via spoofed HOME, :17481 | refused (`acting on a DIFFERENT library`), exit 1; ONE `GET /status` |
| G2 redirected `record start` | same | refused, exit 1; ONE `GET /status`, **no POST** |
| G3 unredirected `record status` | same | **drives it** (`state=idle`) |
| O1 redirected `record status` | **the owner's real service, :7882** (pre-#161) | refused (`it predates issue #161 ... REAL library`); ONE `GET /status` |
| O2 unredirected `record status` | the owner's real service | **drives it** (`state=idle signedIn=True`). Its own log attempts on the owner's log: 16 opens, 8 EACCES, which is also this run's positive control. |
| O3 redirected `serve` (no `--port`) | - | refused (`must not listen on the default port 7882`); **no `bind`, no connect** |

Only G1's and G2's exit codes were captured; the reason is in the incidents section. The owner's service received
exactly two read-only `GET /status` from me (O1, O2) and nothing else.

## 6. The two things CI found (`07-il-pins.txt`)

- **The real IL guard bit.** CI run 43925309084 (6ba5165, before f063cb7) failed `EveryFileWriteInTheProduct_IsAPinnedCallSite`
  and `EveryNativeImport_IsPinned`. FOUND minus PINNED was exactly
  `LibraryRedirect::UsableFolderProblem -> File::Delete` / `FileStream::.ctor` and
  `{libc.so.6, /usr/lib/libSystem.B.dylib}!{geteuid, getpwuid_r}`. PINNED minus FOUND was empty.
- **The guard still bites at the pinned head.** I ran the guard's OWN scanner (`CompiledCode.cs`, linked unchanged,
  `harness/ilscan2`) over the Linux-built dll. At f9aba6a all six lines are FOUND and PINNED. Mutant:
  `[DllImport("libc.so.6", EntryPoint="getpwuid_r")]` changed to `"libc"`, and the probe cleanup changed to
  `File.Move` + `File.Delete`. The scanner then reports two unpinned lines, `libc!getpwuid_r` and
  `UsableFolderProblem -> File::Move`, plus a stale pin, `libc.so.6!getpwuid_r`. The test would fail on either.
  This is a proxy: the real test runs on the Windows-built dll, which compiles out `Platform.Linux`, so the 4
  Linux-only write lines unpinned in this scan are expected.
- **The xunit collection** (b780cd8) serialises `HeadlessControlServerFramingTests` and `LibraryRedirectTests`. That
  is a serialisation workaround, not a port fix; the probe-then-release port race remains (#174). The collision it
  hides is visible in the same CI run's linux job:
  `HeadlessControlServerFramingTests.IsUnframedBody_MethodAndFraming_...(post) [FAIL]`.

## 7. Gates (`09-gates-ci.txt`)

- **Local, f9aba6a, under Landlock:**
  - Linux **387 passed / 0 failed / 1 skipped** (388); with `AGENTEYES_FFMPEG_TESTS=1`, **388/0/0**.
  - DesktopApp **17/17**.
  - `AgentEyes.Tests.Mac` builds with 0 errors, but cannot run here.
  - The Windows `.sln` cannot be built here (no WindowsDesktop targets).
- **CI run 43929135704 (b780cd8), each attempt fetched separately:**

  | Attempt | macOS | linux | windows |
  |---------|-------|-------|---------|
  | 1 | **351/0/1** | 387/0/1 + opt-in 388/0 | 2436/0/22 + opt-in 2455/0/7, DesktopApp 17/17 x2 |
  | 2 | **351/0/1** | same | same |

  - They differ in timestamps and durations (windows suite 44 s vs 38 s), so they are two real runs.
  - **What attempt 2 changed: nothing in code.** It is a re-run of all three jobs at the same commit on fresh runners,
    and its value is a second sample of the flaky port collision.
  - **Trap:** `gh run view --job <attempt-1 job id> --log` returned the ATTEMPT-2 log here (identical md5). Fetch with `--attempt N`.
- **Run 43930682195 at f9aba6a itself:** windows and linux succeeded, macOS was **queued** when this report was
  written. f9aba6a adds only `handoff.md` to b780cd8.

## 8. My footprint and incidents

- **Everything that ran AgentEyes code ran under Landlock**, with the control proven first: builds, tests, every
  product binary, and the IL scanner.
- **Outside Landlock:**
  - `git`, `gh` and `curl` to scratch ports;
  - `getent passwd` and a read of `<home>/.config/user-dirs.dirs` (configuration);
  - the XDG/HOME keys of `/proc/<pid>/environ` for the owner's service and tray (read-only, to check for a relocated library);
  - a name-only `find -maxdepth 1` in `<home>/Videos` and `<home>/.local/share` (the parents, not the library);
  - `sg adm -c 'id -g'`;
  - the compile of `harness/ilscan2`, a compile only.
- **I never listed, read or wrote inside either real root.** Every attempt to do so is an EACCES in the artefacts.
- **The owner's service got two read-only `GET /status` requests (section 5).**
- **Incident (mine, scratch only).** `ports.sh` ended its stand-in with `pkill -INT -f "serve --port 17481"`. The
  invoking shell's own command line contained that text, so pkill killed it, and with it the `tee` of the summary,
  after all runs had finished. Consequences:
  - The stand-in had already exited.
  - The pattern could not match the owner's service (`agenteyes serve`, no port).
  - The summary was rebuilt from the saved per-case files, and the exit codes of G3, O1, O2 and O3 were lost.
  - The committed harness uses the built-in timeout instead.
- **Left running:** nothing of mine. The owner's service (pid 6560), tray (1298614), the go2rtc ffmpeg (72110) and
  the foreign MSBuild node were identified by command line and not touched.
- **Worktrees:** QA's scratch worktrees (`merged`, `main`, `mut`) are in my scratch folder, local only and never pushed.

## 9. For pass 3 (the Lead's rulings, 2026-11-16)

1. **F2, DEFECT.** `LibraryRedirect.cs:193-228` resolves only the path TO each root, and the banner (`:376`) then
   claims the real library is kept out. Fix: either resolve what is under the roots, or refuse a root containing a
   link that escapes it. Cover file links, deeper links and hard links (dev/ino). Start from section 1.5's scratch reproduction.
2. **F1, wording and doc.** `HeadlessHost.cs:89` and `LibraryRedirect.cs:376` must not assert a fact the guard cannot
   establish. They should state what was compared and leave the reader to judge. Write the residual hole into
   `ISOLATING_A_REAL_PROCESS.md`; it is absent today, and `:50` reads as unconditional. Two stronger rules are proposed
   in section 1.4, for the Lead to accept or decline.
3. **M-A.** Pin the environment half at the process level, for example zc/zvc as real-process tests, and show
   mutation M-A failing.
4. **Doc staleness.** `ISOLATING_A_REAL_PROCESS.md:69` quotes the banner as `(derived from the account, not from HOME)`.
   The code prints `(each root with where it was derived from)`, so a reader grepping for the documented text will not find it.
5. **Merge** current main (5 add/add conflicts, section 4).

## 10. What I did NOT cover

- **Windows, all of it.** That includes the known-folder derivation under a spoofed `USERPROFILE`/`LOCALAPPDATA`,
  `TestIsolationTests` / `ManifestWriterIlTests` on a real Windows host, and the tray app. It is routed separately.
- **macOS.** I relied on CI only (351/0 in two attempts); there was no local run and no re-measurement of the macOS
  `pw_dir` offset beyond CI.
- **Root without sudo, `doas`, `pkexec`, containers via the docker group.** Reasoning only (section 1.6).
- **The relocated library against a REAL relocation.** There is none on this machine; it was simulated in scratch.
- **Hard links into the real library, bind mounts, and TOCTOU** (a link swapped after the check). None were measured.
- **A link to ONE real recording under a running repair pass.** Not run against the owner's recordings.
- **`record start` and `stop` against any service by an UNredirected CLI.** Only `status` was run (no capture started).
- **Heavy smokes.** None were run; no capture, audio or UI path changed in pass 2.
- **The opt-in ffmpeg tests on the merged tree.** Only the branch head was run with them.
- **Screenshots folder.** A redirected capture still goes to `<home>/Pictures/Screenshots` (`CaptureService.cs:84+`).
  That folder is outside both roots and outside #161's two-root contract, and it was not tested.
- **The exit codes of G3, O1, O2 and O3** (lost, section 8).
