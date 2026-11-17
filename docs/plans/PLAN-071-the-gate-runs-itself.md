# PLAN-071: the gate in the repository, and warnings that fail the build

- **Task ID**: `TASK-086`
- **Status**: Done (2026-11-18)
- **Issue**: `ISS-158` (resolved in part)
- **Related**: [`TEST-PLAN-2.0`](../TEST-PLAN-2.0.md) (the gate's layers)

## 1. The cause

The gate (both builds, the tests and the conformance suites on .NET 8 and 9, the format readers, the stress harness's
tests, the machine-details check) was run by hand from a script outside the repository. The CI workflow kept its own
copy of most of those commands, without the machine-details check. Nothing made the two the same, and a warning passed
the build: no analyzer looked at the library's culture-dependent calls.

So: **the rules were kept by hand because the gate had two copies, one outside the repository, and the build
enforced nothing. The change makes the gate one script in the repository, which the workflow runs, and makes a warning
fail every compile.**

## 2. The change

- **`tools/gate.mjs`** runs every step of the gate, and the workflow (`.github/workflows/tests.yml`) runs it on each of
  its machines: `node tools/gate.mjs --framework 8.0` or `9.0`. A local run is `node tools/gate.mjs` (both
  frameworks). It builds the CLI and the sample too, which nothing built before. All steps run; it exits non-zero if
  any failed.
- **`Directory.Build.props`**: `TreatWarningsAsErrors` for every project, on every compile. Passed only on the gate's
  command line, a warning in a project MSBuild found up to date would pass unseen. One exception, NU1900: NuGet
  couldn't fetch the vulnerability data (no network), which is the machine, not the code; it failed the restore in
  the offline Linux run. A known vulnerability in a package (NU1901 to NU1904) still fails the build.
- **`.editorconfig`**: for the library and the CLI (`src/`), the analyzers for culture-dependent formatting, parsing
  and comparison (CA1304, CA1305, CA1309, CA1310, CA1311) are warnings, so errors. CA1307 is left off: the calls it
  finds (`IndexOf(char)` and the like) are ordinal already.
- **The warnings there were**:
  - CA1305: five culture-dependent number formats in the library (page row counts, a code point, a parameter's
    position), now invariant. They are never negative, so what they write doesn't change;
  - xUnit1031: three tests blocked on a task they bound; they await it now (a hang still fails them);
  - CA1416: the stress harness pinned cores on any system; it pins them on Windows only, the one system it reads them
    on.

### What it doesn't do (still open in `ISS-158`)

- **No rule against an empty `catch`**: the SDK has no analyzer for it. It goes with `ISS-144`, which removes the read
  path's swallowing catches.
- **No ban on writing a data file past its owner** (`BannedApiAnalyzers`): it goes with `ISS-134`, which makes one file
  store.
- **`Nullable` stays off**: turning it on is a change across the whole library, of its own.
- The workflow runs on a push to `main` and on a pull request, as before; a commit kept local runs only the local gate.

## 3. Checks

| Break | Caught by |
| :--- | :--- |
| H1 a culture-dependent `int.ToString()` in the library | the gate: the engine's build fails (CA1305), and the script exits 1 |
| H2 a test blocks on a task | the test project's build (xUnit1031) |
| H3 the stress harness pins cores off Windows | the harness's build (CA1416) |

No independent review: the change is the build's configuration and the gate's script; the library's behaviour doesn't
change (the five formats write what they wrote).

## 4. Verdict

**Clean, with named debt.** The gate has one copy, in the repository, and the workflow runs it; a warning fails every
compile, and the library's culture-dependent calls are warnings. The debt, named in section 2: empty catches
(`ISS-144`), direct file writes (`ISS-134`), `Nullable`.

## 5. How it was checked

- The breaks (section 3), each restored and compared.
- The gate, by the new script: every step green.
- The whole suite on Linux (.NET 9, a container with no network, as a user other than root).
- The workflow itself runs at the next push.
