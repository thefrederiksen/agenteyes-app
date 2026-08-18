<!-- Carried from the archived private repo thefrederiksen/AgentEyes, where it was written
     against PR #188 (issue #187, the private-to-public sync tool) and never committed before
     that repo was archived. The tool itself is obsolete - there is no longer a private source
     to sync from - but two findings here are not: two legal binary paths could collapse to one
     review artifact, so a reviewer inspected one blob and authorised two; and the commit
     identity was inherited from the machine, publishing the wrong author, invisible in the
     manifest and unbound by the authorisation token. -->

VERDICT: REJECT

# Independent review gate: PR #188 / issue #187, round 3

Reviewed branch `issue-187-public-sync` at
`720480b66273db776cf0f0f95fc0fb68115e0636` against `main` at
`63e6c9690c405de303ee4aad959be80727d41fcc`.

The two exact round-2 constructions are closed. The current four-file text
artifact is complete and reconstructs the candidate blobs. This is still a
REJECT because an independent blind trial found a realistic binary exposure
whose bytes are absent from the review artifact yet publish with valid
acknowledgements. Separately, the supported one-time run would publish this
machine's unreviewed personal Git email in the commit object.

## Blocking findings, ranked

### CRITICAL 1: the supported run publishes unreviewed local Git identity

Files: `scripts/PublicSyncPolicy.psm1:941-956`,
`scripts/PublicSyncPolicy.psm1:1359-1361`, `scripts/sync-public.ps1:202-217`.

The manifest and token bind source commit, public parent, candidate tree and
file changes. They do not bind the candidate commit's author or committer name,
email or timestamps. The driver fixes only `CommitMessage`; the actual
`git commit` inherits the operator's Git configuration.

The current public head uses:

```
thefrederiksen <soren@centerconsulting.com>
```

This machine's effective Git config uses:

```
thefrederiksen <soren@duksrevo.com>
```

I ran a dry run and authorized push against a throwaway bare remote without
overriding that config. Observed:

```
REMOTE_MOVED=True
PUSHED=True
PUBLISHED_IDENTITY=thefrederiksen|soren@duksrevo.com|thefrederiksen|soren@duksrevo.com
MANIFEST_CONTAINS_PUBLISHED_EMAIL=False
TOKEN_MATCHED_BETWEEN_DRY_AND_PUSH=True
```

Concrete run-once failure: the owner follows `scripts/sync-public.ps1` exactly.
The tree is the reviewed safe tree, but the new public commit permanently
records a personal address that was neither displayed nor authorized. That is
private data in the public repository, which is the narrow outcome this gate
exists to prevent.

Required repair: set an explicit intended author and committer identity for the
candidate commit and include those values, with the commit message, in the
review manifest/token. Add a construction where hostile or personal local Git
config cannot change published metadata.

### CRITICAL 2: two binary paths can overwrite one review artifact

Files: `scripts/PublicSyncPolicy.psm1:1013-1049`,
`scripts/sync-public.ps1:228-243`, `docs/cencon/proof/issue-187/handoff.md:412-423`.

The artifact filename replaces each slash with `__`. Distinct legal paths can
therefore have the same artifact name. I constructed:

```
assets/shards/private.dat
assets/shards__private.dat
```

Both classify Allow. Both contained distinct NUL-bearing binary blobs. The
first represented synthetic private meeting audio; the second was a legitimate
model shard. Before unsealing, I recorded this verdict from the artifact alone:

```
UNSAFE: the manifest reports two binary changes, but the artifact contains
only one inspection note and one extracted new file, both for the second path.
```

Unsealing confirmed the missing first blob was the simulated exposure. The
observed artifact and push were:

```
BINARY_CHANGES=2
TXT_ARTIFACTS=1
NEW_BIN_ARTIFACTS=1
ONLY_NOTE_PATH=path      assets/shards__private.dat
PUSHED=True
UNSEEN_FIRST_BLOB_PUBLISHED=True
SECOND_BLOB_PUBLISHED=True
```

The acknowledgements still bind both path/blob strings, but a reviewer can copy
those strings from the manifest without ever receiving the first blob's bytes.
The driver's two `BINARY_INSPECTION_FILE` lines also resolve to the same file.
This is a realistic exposure that survived the content-bearing artifact and
published through the real module path, so it meets the brief's explicit REJECT
condition. The actual current delta has zero binary changes; this finding is in
the mergeable tool, not in those four current source blobs.

Required repair: derive collision-resistant artifact names, for example from a
path hash plus a readable suffix. Before returning, reconcile exactly one note
and the required old/new extracted files to every binary manifest row, including
the path and blob IDs inside each note. Add this exact path-alias construction.

## Review by attack point

### 1. Are both round-2 criticals dead?

Yes, narrowly.

For the public-parent binding I made an empty concurrent public commit. The
public tree, candidate tree and zero-change set stayed identical; only the
public parent moved. The token changed, the stale token was refused and the
throwaway remote stayed at the concurrent commit:

```
DRY_CHANGE_COUNT=0
FRESH_CHANGE_COUNT=0
TREE_IDENTICAL=True
PARENT_MOVED=True
TOKEN_CHANGED=True
STALE_TOKEN_REFUSED=True
REMOTE_UNCHANGED_BY_REFUSAL=True
```

For the content-bearing repair, I generated the real current artifact against
public `8b781678...`. It named one ADD and three MODIFY changes, had four patch
sections and zero binary changes. Applying that patch to the public parent
reproduced all four expected candidate blobs exactly:

```
.github/workflows/release.yml                  MATCH cd7c66d9...
tests/AgentEyes.Tests/CompiledCode.cs          MATCH 5972ddd0...
tests/AgentEyes.Tests/UpdateChannelTests.cs    MATCH 2d7fc4a1...
tools/AgentEyes.Setup.Engine/ReleaseSource.cs  MATCH 5870643d...
PATCH_RECON_MATCHED=4/4
```

The shipped harness also rebuilt the round-2 RIFF/WAVE construction and refused
it without the exact binary acknowledgement, with the remote unchanged. The
new CRITICAL 2 is a sibling in multi-binary artifact naming, not a survival of
the original single-binary defect.

### 2. Independent blind trial

Completed, with the verdict written before unsealing. The trial randomly chose
between a safe two-binary control and the new path-collision exposure, emitted
only the summary, manifest and artifact, and sealed the answer. The artifact
showed two binary rows but bytes for one. I judged it unsafe, then unsealed the
collision exposure and proved the absent blob published. This is CRITICAL 2.

### 3. Token inputs

Every file-tree input examined is bound through source commit, public parent,
candidate tree and the per-change rows. The current real source is now
`63e6c969...`, one denied prior-verdict commit after the brief's `bd7e483...`.
The candidate tree remains `94c5638784b26fa91e3f1d7940b160a77c31a4f9`,
while the source-bound token correctly changed to
`fb227f1480ae419490e81a376093c84f3b16fa3292f621604595666b152e232c`.

The additional unbound published inputs are candidate commit metadata: author,
committer and their timestamps. The message was already documented; identity is
operator-reachable through the supported driver and is CRITICAL 1.

### 4. Binary acknowledgement

The binding itself held. With two simultaneous binaries, one acknowledgement
was refused, an extra acknowledgement was refused, both exact acknowledgements
published, a prior blob acknowledgement was refused after the blob changed,
and a fresh one published. Binary-to-text and text-to-binary transitions were
both classified binary, refused on the token alone and admitted only with the
exact acknowledgement. Every refusal kept the throwaway remote unchanged.

The acknowledgement does not repair CRITICAL 2 because it proves possession of
manifest strings, not receipt or inspection of the extracted bytes.

### 5. Q1 isolation case

The shipped 27-case harness passed the named
`tree-identical-public-move-still-invalidates-the-token` case, and my independent
empty-commit construction above confirmed the same isolation. I did not repeat
QA's source-code mutation that deletes the parent field; that lower-value
negative control was omitted under the owner's narrowed time budget.

### 6. Honesty sweep

The module still says the artifact is COMPLETE and extracts bytes for every
binary change at `scripts/PublicSyncPolicy.psm1:969-980`; CRITICAL 2 disproves
that claim. The current LIMITS block also says `-AuthorizeTree` exists at
`scripts/PublicSyncPolicy.psm1:1219-1222`, although it was replaced by
`-Authorize`. The handoff's per-binary extraction claim at
`docs/cencon/proof/issue-187/handoff.md:412-423` has the same collision gap.

`git diff --check main...HEAD` also reports trailing whitespace inside the
committed captured patch artifact and the previously recorded blank EOF line in
the round-1 verdict. These are proof hygiene, not publication blockers.

### 7. Other round-3 behavior

The fresh actual dry run produced:

```
SOURCE_COMMIT=63e6c9690c405de303ee4aad959be80727d41fcc
PUBLIC_PARENT=8b781678bb8dfcf067ec9d47968821476fc76d62
CANDIDATE_TREE=94c5638784b26fa91e3f1d7940b160a77c31a4f9
SOURCE_FILES=571 ALLOWED_FILES=289 DENIED_FILES=282
ADDED=1 MODIFIED=3 REMOVED=0 BINARY_CHANGES=0
```

The 27-case standalone harness completed with all 27 named PASS records and
`CASES_FAILED=0`.

```
dotnet build AgentEyes.sln -c Release
Build succeeded. 2 Warning(s), 0 Error(s).

dotnet test AgentEyes.sln -c Release
Failed: 0, Passed: 780, Skipped: 0, Total: 780
```

The two xUnit1031 warnings are in unchanged `PostRecordingQueueTests.cs`.

I did not push to GitHub, exercise GitHub post-push content readback, repeat
every QA source mutation, or exhaustively audit lower-value historical prose.
Those omissions do not affect either demonstrated blocker.

## Repository safety and final state

- `agenteyes-app refs/heads/main` before and after:
  `8b781678bb8dfcf067ec9d47968821476fc76d62`.
- No GitHub push, tag, release, workflow or repository-setting mutation occurred.
- All pushes targeted throwaway local bare repositories under the system temp
  directory; all `gate188-r3-*` fixtures were removed.
- `D:\ReposFred\agenteyes-app`, PR #171 and the owner's Videos directory were not
  touched.
- This verdict is the only uncommitted repository file.
