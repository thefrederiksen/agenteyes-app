# Issue #218 - wrap-aware detection, parked from #173

This branch (`issue-218-wrap-detection`) is #173's tip after the owner's split (Section 7a), plus ONE
commit that puts the wrap work back. #173 merged single-line detection only; this commit is where
#218 starts.

What the commit restores, exactly as it stood at #173's `388b111` (QA round 3's subject):
- `scripts/check-proof-identity.py` - the console-wrap discriminator (column-0 continuation, tier 1
  mid-word at any width, tier 2 beside punctuation at WRAP_WIDTH 80), the joined view, the UTF-16 scan.
- `scripts/test-check-proof-identity.py` and `tests/evidence-identity-corpus/` with all four classes:
  green, red, declared-miss, declared-flag.
- The CI `evidence-identity` job's wrapped positive controls and the corpus step, and the
  DEVELOPMENT_METHOD.md paragraph describing the wrap rule.

What it adds, ported from #173 round 4 so #218 does not regress what main will hold:
- systemd unit names allowed by last label (service, socket, device, mount, automount, swap, path,
  timer, slice, scope - none a delegated TLD; .target is, so it needs a numeric instance).
- the container test accounts `tester` and `qauser` as allowed home users.
- corpus files green/fp22-fp25, red/leak23-leak24, declared-flag/flag03.

State at this commit: corpus 57/57 as declared (green 25, red 24, declared-miss 5, declared-flag 3);
the guard over a tree that includes main 5e6e727 is green (0 hits).

The open defects are QA round 3's, unchanged here on purpose - they are #218's specification:
`docs/cencon/proof/issue-173/qa/qa-report-round3.html` (D2: the 80-column gate misses PowerShell's
79-column wraps; D3: an indented split exactly at the separator is missed, and the docstring says it
is caught).

## How #218 picks it up

After #173 is squash-merged to main:

    git fetch origin
    git checkout -b issue-218-<slug> origin/main
    git cherry-pick <this commit>     # the single "park" commit at the tip of issue-218-wrap-detection

The cherry-pick applies onto main's single-line guard. If #173 changed after this branch was cut,
resolve each conflict in favour of this commit for the four paths above, then re-apply anything new
from main by hand.
