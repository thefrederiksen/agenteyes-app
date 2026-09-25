<!-- Carried from the archived private repo thefrederiksen/AgentEyes, where it was written
     against PR #189 (issue #186) and never committed before that repo was archived.
     Preserved here because its findings shaped how the consolidation was executed:
     notably that pushing a branch and a tag together can leave the tag accepted while
     the branch is rejected, publishing a release from an unverified tree. -->

VERDICT: REJECT

# Independent review gate - PR #189 / issue #186 - round 2

Reviewed PR head `0220e3e02bbd573e27f8cf32f9dda637f41571f0` against base
`63e6c9690c405de303ee4aad959be80727d41fcc` in the isolated worktree
`D:\ReposFred\AgentEyes-gate186`.

The round-2 change really is documentation only and the product code and pins remain sound. The new
sequence also fixes the original error that copying content could migrate an installed v1.4.4. It is
still not safe to execute verbatim. The delete can be licensed by an inventory that cannot establish
its own completeness, the release tag is not bound to the synced commit, the UI check cannot
distinguish the old registry from the new one, and fixed `Invoke-WebRequest -OutFile` paths can make a
failed fetch validate stale bytes.

## Blocking findings

### 1. The delete is still licensed by an install inventory that cannot establish its completeness

Files: `docs/cencon/proof/issue-186/sequencing.txt:27-32`, `:380-387`, `:405-406`, `:457-490`;
`tests/AgentEyes.Tests/UpdateChannelTests.cs:281-287`;
`docs/cencon/proof/issue-186/qa-report-round2.html:250-296`.

The file starts with the required property: no software anywhere may still ask the retired repo for
anything. Step 5 then tells the operator to say "one install" because a test comment says there is
one, and asks whether the operator knows of another. That is an absence-shaped inventory. Ignorance
of an install produces the same answer as a complete inventory, so it cannot trigger the fallback
that is supposed to protect that install.

The repository has no telemetry or install registry. The live release API provides no machine
identity: it shows five GUI setup downloads, one each at v0.2.3, v0.2.4, v0.6.0, v0.8.7 and v1.4.4.
Those five may all belong to the owner, but they cannot prove that. The QA report turns that limit
into the inference that a second install is unlikely. Likelihood is not the deletion precondition,
and a code comment is not an install roster.

Concrete failure: a second machine installed one of those public downloads and is unknown to the
operator. The operator says the prescribed count, migrates the owner's machine, satisfies every
per-machine assertion and deletes the repository. The unknown binary still has both retired URLs
compiled in and loses both updates and its plugin catalog.

Required correction: the fallback must be the default while no exhaustive inventory exists. If
deletion is to remain an available branch, require a positive, named, owner-approved inventory and
an explicit reconciliation of how AgentEyes was distributed; absence of that artifact must select
RETAIN. Otherwise remove the delete branch and retain the populated compatibility endpoint.

### 2. The release cut is not bound to the commit that step 2 verified

Files: `docs/cencon/proof/issue-186/sequencing.txt:331-368`;
`scripts/new-release.ps1:20-49`; `.github/workflows/release.yml:13-16`, `:154-178`.

Step 2 keeps `$remoteMain`, but step 4 never compares the clone's HEAD to it. `git clone` uses an
implicit `agenteyes-app` directory, no command proves that clone succeeded, and Windows PowerShell
does not stop on a native command's non-zero exit. If that directory already exists, clone fails and
`cd agenteyes-app` can enter an older checkout. `new-release.ps1` rejects tracked dirt but does not
prove ancestry or the remote it is about to push to.

The final push is also non-atomic. I reproduced its exact two-ref shape with local repositories: the
stale clone's `main` was rejected as non-fast-forward, `git push` exited 1, but the new `v1.4.5` tag
was accepted. The workflow triggers on that tag and can publish a complete release, so every step-4
presence can pass even though the release did not descend from `$remoteMain`.

This is not hypothetical source shape. Current private `origin/main` has the update channel on
`agenteyes-app` while `PluginRegistry.DefaultUrl` still names `AgentEyes-releases` and
`plugins/registry.json` is absent. If PR #188 is merged and synced before #189, as the prerequisite
allows, an existing clone can contain exactly that intermediate state.

Concrete failure: clone fails into such an existing checkout; the version script tags it; the tag
push succeeds while the stale main push fails; the workflow publishes v1.4.5. Step 7a passes because
that source has the new update channel. Step 7b also passes for the reason in finding 3. The delete
then strands the plugin catalog in the newly installed v1.4.5.

Required correction: clone into a newly created, proven-empty path and stop on every native failure.
Before bumping, require the exact origin URL and `HEAD -ceq $remoteMain`. After bumping, require
`HEAD^ -ceq $remoteMain` and exactly the intended tag at HEAD. Push `main` and the tag with
`git push --atomic`, then read back the remote tag commit and prove its ancestry before accepting the
release checks.

### 3. Step 7b proves two plugin names, not the registry endpoint

Files: `docs/cencon/proof/issue-186/sequencing.txt:428-454`;
`src/AgentEyes.App/PluginCatalogDialog.cs:41-49`;
`src/AgentEyes.App/PluginRegistry.cs:85-99`.

The live retired registry and the committed new registry both contain exactly `Doc Companion` and
`QA Walk Companion`. They differ in the load-bearing property: the old entries download from
`MyQuietShadow-releases`, while the new entries download from `agenteyes-app`. Seeing the two names
therefore cannot establish the claim at lines 453-454 that the installed software fetched from the
new repo.

Concrete failure: the stale build from finding 2 has a null config override and fetches the old
registry. The catalog displays both required names, so the documented pass condition holds. Deleting
the old repository then removes the catalog and both zip URLs.

The UI already exposes the decisive presence at `PluginCatalogDialog.cs:43`. Required correction:
step 7b must also require the visible line to equal exactly
`Registry: https://raw.githubusercontent.com/thefrederiksen/agenteyes-app/main/plugins/registry.json`.
Keep the two-name assertion as a separate content check.

### 4. The content-chain downloads can validate stale files after a failed request

Files: `docs/cencon/proof/issue-186/sequencing.txt:237-257`, `:316-325`, `:389-400`;
`docs/cencon/proof/issue-186/qa-report-round2.html:194-204`.

The fixed output paths are not proven absent before `Invoke-WebRequest`. I placed known bytes at the
exact kind of existing output path, requested a live 404 with `-OutFile`, and observed all three
arms: `Invoke-WebRequest` threw, the file still existed, and its length and content were unchanged.
That directly contradicts the QA report's inference that its absent-file 404 control proves no stale
file can be hashed or parsed.

Concrete failure: `$env:TEMP\registry-main.json` survives a prior successful run. On a rerun the
request fails, PowerShell continues to the separately entered hash and parse commands, and both
validate the old file. The sequence records that the branch URL served the expected bytes when no
bytes were fetched. The same pattern exists for `registry-pinned.json`, `dc.zip`, `qwc.zip` and the
installer.

The branch check also does not compare the current `main` ref to `$remoteMain`. A cached expected raw
response can therefore pass while public main has moved to different registry bytes; the check only
waits when cache staleness produces a mismatch, not when stale bytes happen to be the expected ones.

Required correction: use a newly created, proven-empty temp directory for the run, set
`$ErrorActionPreference = 'Stop'` (or `-ErrorAction Stop`), and bind each request and its hash in one
failing block. Compare the contents API blob at `ref=main` to `$expectedRegistryBlob` immediately
before the delete, as well as checking the immutable commit-pinned response. A failed request or an
existing destination must stop before any hash can be accepted.

## Checks that passed

- GitHub and the worktree both reported PR head `0220e3e02bbd573e27f8cf32f9dda637f41571f0`.
- The round-2 delta from rejected head `511c8f2` is exactly five files and every one is under `docs/`.
  Blob IDs for `PluginRegistry.cs`, `package-plugin.ps1`, `plugins/registry.json`, `docs/signing.md`,
  `PluginRegistryChannelTests.cs` and `UpdateChannelTests.cs` are byte-identical across the two heads.
- All 12 named `PluginRegistryChannelTests` passed, including their known-bad controls.
- The actual PowerShell command candidates parse. Fifty-five literal lines parsed; the sole parse
  error is line 217's explicitly non-literal `<REVIEW_TOKEN>` template.
- The PR #188 prerequisite check prints zero paths on current main, and the instruction treats fewer
  than two as STOP. That check fails closed today.
- The live endpoints still reproduce section A: new registry 404, retired registry 200, new Latest
  API 404, retired Latest API 200. The one running local install is v1.4.4 and its parsed
  `PluginRegistryUrl` is null.
- `gh release create --help` explicitly accepts `--latest=false`.
- `dotnet build AgentEyes.sln -c Release`: Build succeeded, 0 errors, with the two pre-existing
  xUnit1031 warnings in `PostRecordingQueueTests.cs`.
- `dotnet test AgentEyes.sln -c Release --no-build`: Failed 0, Passed 773, Skipped 0, Total 773.

## What I did not check or change

I did not merge or modify either PR, run or push the public sync, publish or edit a release, upload
an asset, push a tag, trigger a workflow, change a repository setting, delete or archive a repo, or
modify the installed product. The non-atomic push reproduction used only throwaway local Git
repositories under this worktree's `.temp` directory. I did not read, move, rename or modify anything
under `%USERPROFILE%\Videos\AgentEyes` and did not touch PR #171, PR #188 or the primary checkout.
