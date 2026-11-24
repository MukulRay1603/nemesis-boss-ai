# Nemesis documentation

Everything about how Nemesis was built, changed and measured. These files are version-controlled with the code, so any past state can be recovered with `git log -- docs/`.

| File | What it holds |
|---|---|
| [BUILD_NOTES.md](BUILD_NOTES.md) | Chronological build log: every working session, what changed, why, how it was verified, and the commit. Newest at the bottom. |
| [DECISIONS.md](DECISIONS.md) | Decision log: each design or technical decision, the options considered, what was chosen and who chose it. |
| [RESULTS.md](RESULTS.md) | Every measured number, with how it was measured and where the raw data lives. Nothing unmeasured goes here. |
| [AUDIT.md](AUDIT.md) | The review of the original project (April 2026 state): what worked, what didn't, and where it was heading. |

## How these are kept up to date

1. Each working session adds a dated entry to `BUILD_NOTES.md` (what, why, verification, commit hashes, open items).
2. Any choice between real alternatives goes into `DECISIONS.md` with an ID (`D-0NN`), and the build note links to it.
3. Any new number goes into `RESULTS.md` with its method and data path. Numbers are never edited in place: a re-measurement adds a new row with its date, so history is kept.
4. Notes are committed together with the code they describe, so the log and the code never drift apart.
