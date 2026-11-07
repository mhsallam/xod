# PLAN-043: The publishing run (one soak, capacity runs, a page that can be cited)

- **Task ID**: `TASK-054`
- **Status**: Done (2026-11-07). Run on the owner's go and passed; harness committed before it (xod2
  `4926465`), results and docs in `fb79660`. Site pushed as one commit (`e030e3d`; the unpushed
  `55eaf8f`, which held the machine record, was folded into it) and deployed to xod.sabaeon.com with
  `deploy.sh`, checked live. See [Results](#results-2026-11-07).
- **Related**: [`PLAN-042`](PLAN-042-benchmark-reporting-refresh.md) (the harness, the rules a run is
  judged by, and the review this follows), [`PLAN-040`](PLAN-040-stress-and-endurance.md),
  `ISS-044` (reads barely scale; GC pauses and slow commits in the soak)

## Why another run

The stress sections could not be published as they stood:

- The soak on the page ran code that was not committed (`dceb41e-dirty`), on the harness from before
  PLAN-042's review: no build id, no recorded rules, no count of reads that found nothing. The
  project's own rule says such a number is not quotable: "A number is quotable only when its result
  file is committed and its stamp is `clean`" (`benchmarks/results/README.md`). The capacity runs were
  `dceb41e-dirty` too.
- A reader review of the page found it hard to read: an internal plan id in the verdict
  ("PLAN-042/1"), a default chart of three lines each scaled to its own peak with no legend, axis
  labels such as "363,931" and "3,956 ms", six-digit figures from a single run, the 856 ms slowest read
  only in a tile's small print, and no CPU or OS named.
- The owner cannot leave the machine for hours: one 60-minute soak and about ten minutes of capacity
  runs, unattended, is the budget.

## What changed

### Harness (`benchmarks/XodBench`)

- **The commit stamp counts new files.** `Machine.GitCommit` marked a tree dirty only for changes to
  tracked files, so a new source file git did not track yet left the stamp clean. It now reads
  `git status --porcelain --untracked-files=all` and `Machine.IsDirty` counts any change outside
  `benchmarks/results/` (the runs write their own files there). Porcelain v1 paths are relative to the
  repository root whatever the working directory (git 2.49 docs, git-status, "Porcelain Format
  Version 1"). The stamp is read before the run's output file is created.
- **`--publishable`** refuses to start (exit 2, no output file) when the commit is dirty or unknown,
  or the machine reports battery power. The header records the same two readings, so the refusal and
  the file cannot disagree. The site stays the judge of the file; this only saves the hour.
- **The work folder is removed.** A run that made its own folder under `%TEMP%` deletes it after
  writing the end row (a 10M-row database is 4.85 GB; five runs would leave 24 GB). A folder given with
  `--dir` is kept. A failed delete is reported on stderr, not fatal: the result is already written.
- Tests (`XodBench.Tests`, 22): the work-folder test and the `IsDirty` cases failed against the old
  code (the folder was left behind; an untracked `.cs` file read as clean; a results-only change read
  as dirty), as did the `--publishable` test before the check was written (the run started on a
  `-dirty` stamp).

### Site (`xod-site`)

- **Runs that cannot be cited are void** (`scripts/stress-summary.mjs`, `provenanceReasons`, for the
  soak and capacity alike): a commit ending `-dirty`, no commit, or battery power. Capacity lists the
  runs it left out only when they bear on the set shown: runs of the same build, and any attempt newer
  than the set (a newer build that failed must not vanish behind an older one that passed); discarded
  runs of older builds are not listed. Each rule was seen failing its test with the rule switched off.
- **Readability** (`src/pages/benchmarks.astro`, `src/lib/format.mjs`, tested by
  `scripts/format.test.mjs`):
  - the verdict in plain words ("It passed every rule set before it started."); the plan id stays in
    the small print under the table;
  - the commit and build named in the lede (the machine was named too, until the owner withdrew it:
    see [Machine details withdrawn](#machine-details-withdrawn-2026-11-07));
  - tiles rounded to three significant figures (409,000/s); the criteria table keeps exact figures;
  - a sentence with the tails: half the reads under 5.2 us, 99% under 123 us, the slowest 856 ms, the
    slowest commit 554 ms, GC pauses 21% of the time;
  - the chart opens on reads per second in its own units, with coloured keys on the buttons; axes run
    to round values (0, 200k, 400k, 600k; 0, 1 s, 2 s, 3 s), computed once at build time;
  - times in the unit a reader would use ("856 ms", not "855.6 ms"); `ms`/`us` now live only in
    `format.mjs`.
- Until the new run lands, the page shows the old soak as void ("it ran code that was not committed")
  and no capacity section. A preview built from a scratch copy of the files with the stamp marked
  clean was used to check the layout (desktop and 375 px, no overflow); it was not kept.

## The run

On the owner's go, in this order:

1. Commit `xod2` and `xod-site` (plain messages, no attribution lines). The harness refuses an
   uncommitted tree, so this comes first.
2. Mains power; lid open, or lid-close set to do nothing on AC; close other programs; leave the
   machine alone for about 70 minutes.
3. From `xod2`: `powershell -ExecutionPolicy Bypass -File benchmarks/run-stress.ps1`
   - builds Release and runs `XodBench.Tests` (the gate), then `--publishable --dry-run`;
   - 5 capacity runs to 10M rows (about 75 s each), then one 60-minute soak (8 readers, 1 writer
     committing every second, 2M rows seeded, 200k hot, a sample every 30 s);
   - `-SoakMinutes`, `-CapacityRuns`, `-CapacityRows` change the plan.
4. Rebuild the site data and the page, read it, and put the numbers in front of the owner. Publishing
   (deploy) is a separate go.

| step | time |
| :--- | ---: |
| build and harness tests | ~1 min |
| capacity, 5 x 10M rows | ~6-7 min |
| soak | 60 min + ~10 s seed and check |
| total | ~70 min |

### What counts

The soak is judged by the rules its header records (`PLAN-042/2`): after a 5-minute warm-up, minutes
5-15 against minutes 50-60; reads, updates and commits at least 80% of the first window, read p99 at
most 1.5x, private memory at most +15% (every sample), the data checked after a reopen. It is void if
it ran uncommitted code or on battery, has no end row, stopped early, has a gap in its timeline, a
lookup of a row that exists answered nothing, fewer than 80% of samples were quiet (other programs at
most 10% of the CPU, 4 GB free besides the run), or a comparison window has fewer than five quiet
samples. A capacity run counts only if it ended as planned with exit code 0, at its row target, with
no wrong answers, from committed code on mains power; the page shows each number as the median of the
set with its range.

## Results (2026-11-07)

`benchmarks/run-stress.ps1` ran unattended from 04:42 to 05:47 local time; every step exited 0. The build
and the 22 harness tests passed, and the publishable check read commit `4926465` (clean) on mains
power. Build id `e894c3b17d98`.

**Capacity** (`stress-capacity-20261107_014233` to `_014630`): 5 of 5 runs valid, none left out. Each
reached 10M rows, ended as planned with exit 0, no wrong answers, verified after a reopen.

| measure (median of 5) | value | range |
| :--- | ---: | ---: |
| total time | 57.3 s | 56.8-58.7 s |
| insert rate, first million | 230,415/s | 227,273-233,100 |
| insert rate, last million | 221,729/s | 206,612-243,902 |
| close at 10M | 1,207 ms | 1,176-1,314 |
| reopen at 10M | 5.2 ms | 5.0-5.8 |
| reopen to first answer at 10M | 3,491 ms | 2,823-3,585 |
| random-read p99 at 10M (1,000 lookups) | 2,071 us | 1,272-5,608 |
| peak private memory | 13.2 GB | 12.6-13.3 |

The file is 4.85 GB, 521 bytes a row. The median run's slowest 250k-row stretch ran at 65,905 rows/s
at 8.75M rows.

**Soak** (`stress-soak-20261107_014729`): passed every `PLAN-042/2` rule; 120 samples, 100% quiet.

| criterion | minutes 5-15 | minutes 50-60 | ratio |
| :--- | ---: | ---: | ---: |
| reads/s | 437,951 | 394,899 | 0.902 |
| updates/s | 5,611 | 5,857 | 1.044 |
| commits/s | 0.99 | 0.98 | 0.998 |
| read p99 (us, mean of samples) | 115.5 | 125.9 | 1.09 |
| private memory (MB) | 4,006 | 3,888 | 0.971 |

Whole run: 1,478,171,477 reads (411,000/s over quiet samples), 21,074,772 updates in 3,547 commits,
no reads that found nothing, no failed updates. Read p50 5.5 us and p99 123 us in a typical sample;
slowest read 906 ms; slowest commit 302 ms; GC paused the process 20.4% of the time; peak private
memory 4,042 MB. After the run: closed in 266 ms, reopened in 2.4 ms, first answer 962 ms; all
2,000,000 rows, every committed update and the unique key checked.

Reads fall about 10% from minutes 5-15 to minutes 50-60, within the 80% rule. The fall is a step,
not a slope: each 5-minute block of the first 15 minutes averaged 430,000-443,000 reads/s, and every
block from minute 15 to 60 averaged 384,000-419,000/s, with no steady fall across them. GC pause share
(20.0-20.7% per block) and private memory (3.8-4.0 GB) stayed level. The writer made 3,547 commits in
3,600 s against one a second planned.

**Page**: rebuilt from these files and checked at 1,440 px and 375 px (no console errors, no horizontal
overflow). At 375 px the soak chart is drawn into a fixed 340 px-high box at its 900x340 aspect ratio, so
it shrinks to a thin strip with unreadable axis labels. The fixed height predates this task (it is in
`f53cce8`).

**Fixed (2026-11-07).** The cause was the same in all three charts: each was drawn in fixed units (the
soak chart 900x340, the capacity charts 560x240) and scaled to the card, so on a phone 11-unit labels
became about 6 px. The soak chart, drawn in the browser, now sizes its viewBox to the element's own
pixels and redraws when that size changes (a `ResizeObserver`, not a timer): 1,052x340 on a desktop,
295x260 on a phone, and a window resized after load redraws (checked). The capacity charts, drawn at
build time, are drawn in two widths (560 and 340) from the same code and CSS shows the closer one below
560 px. The plan id is gone from the page too: the small print under the criteria table said "The rules
(PLAN-042/2)", which a reader cannot look up; it now says the rules were set before the run and written
into its result file as it started.

## Landing page copy (2026-11-07, owner-approved)

The landing page undersold XOD in places and overclaimed in two, and its numbers were typed in by hand.
Changed in `xod-site` (`src/pages/index.astro`), all approved by the owner:

- Headline "A real database, inside your app." -> "A full ACID database, in files you can read."; the
  line above it no longer repeats "embedded database for .NET"; the lede names ACID transactions,
  indexes and concurrent readers over plain C# objects.
- "Thousands of concurrent readers" (hero and meta description) -> "concurrent readers": no run has
  measured more than eight.
- "A lookup takes four microseconds" (measured 4.7 us) -> "under five microseconds"; "a hundred thousand
  rows load in about a second" (637 ms) -> "ten million rows load in under a minute" from the capacity
  set, bounded by its slowest run.
- The "Reads per second" proof tile showed 172,322 (the comparison suite) under the same label as the
  soak's 410,000 on the card above it; it now shows the soak's rate, so the page gives one number. A
  sentence under the tiles states the soak's record (1.48 billion reads, 21.1 million updates, every
  update there after a reopen). The proof intro says "all on one machine" instead of "the hardware in
  the studio" (it named the machine at first; withdrawn, see below).
- "An embedded database that behaves like a database" -> "A database's guarantees, without a server.";
  "the same engine as the one in your data centre" dropped.

Every sentence with a number now comes from the run files through `src/lib/claims.mjs` (the soak
sentence moved there from `stress.ts`, which re-exports it): a run that does not count or did not pass
supports no claim, and the tile falls back to the comparison figure. The soak card now rounds like the
benchmarks page (411,000, was floored to 410,000). Tests: `scripts/claims.test.mjs`, red against the
missing module and against the old floor rounding; each guard (pass, reopen check, void capacity,
slowest run rather than median) was removed in turn and its test failed. Site tests 28/28; the
benchmarks page text is unchanged; checked at 1,440 px and 375 px, no console errors, no horizontal
overflow.

## Machine details withdrawn (2026-11-07)

The owner does not publish the machine a run used. The pages had printed the CPU model, threads,
memory, Windows build, .NET patch level and core pinning (stress sections, landing proof intro), the
comparison header printed the .NET patch level, and `src/data/benchmarks.json`, which is committed, carried the
whole machine record. The owner also noted that the machine named beside "Ten million rows", with its memory size,
reads as if ten million were XOD's limit.

- `scripts/stress-summary.mjs` no longer passes the header's `machine` into its summaries (the header's
  power reading still voids a battery run); the comparison stamps keep the commit and the runtime's
  major version only (`scripts/public-data.mjs`, `publicStamp`).
- `build-data.mjs` runs `assertPublic` on the data before writing it and refuses machine identity
  anywhere in it. Proven: with the machine field put back in a summary, `npm test` failed and the data
  build refused to write, leaving the file as it was.
- `machineLine` is removed from `src/lib/format.mjs`; the pages say "one machine" and the comparison
  header ".NET 9".
- The capacity lede says the runs stop at ten million rows because that is their target, not a limit
  XOD reached; a run counts only if it ended there as planned.
- The harness still records the machine in its local result files, for reproducing a run; `xod2` has no
  remote and the site does not publish the files. The public `xod` and `xod.core` repos carry no
  machine stamp (searched). The deployed site showed the .NET patch level in the comparison header
  until the deploy of 2026-11-07. The earlier local xod-site commit `55eaf8f`, which held the machine
  record in its data file, was folded into `e030e3d` with the record removed before anything was
  pushed; no branch holds it. **Since then (2026-11-07, `PLAN-044` step 0):** the harness no longer
  writes the machine into result files at all, only into a git-ignored record beside each one.

Site tests 30/30 (the `machineLine` test went with the function).

Still unmeasured on the page: "Raspberry Pi, industrial PCs and .NET boards" and "a gigabyte of rows
fits in about a gigabyte of files".

## Deploy note

`deploy.sh` builds with `pnpm build`, and Astro 7.4.5 refuses Node below 22.23.4. The machine's default
Node was 22.14 (nvm); the deploy ran with the owner's new Node 26.13.0 (nvm folder) first on the path,
the default left as it was.

## Not done (named)

- **"Quiet" is measured across the whole machine**, not on the cores the run is pinned to, with
  Windows' CPU accounting, which under-reads. Light use of the machine during a run may still count as
  quiet; the plan is to leave it alone. A per-core reading would close it.
- **Power is read once**, at the start.
- **`xod2` has no remote**: "committed" means committed locally, so a reader cannot fetch the commit
  the page names. The commit and build id still let the owner reproduce a run.
- Carried from PLAN-042: harness tests use real time; the CI workflow never runs (no remote); numbers
  on the landing page outside the stress sections are typed in.

## Verdicts

| change | verdict |
| :--- | :--- |
| Commit stamp counts untracked files; stamp read before the output file | clean |
| `--publishable` | clean: it mirrors the site's rule as a fail-fast, the site stays the judge (said at both sites) |
| Work folder removed | clean |
| Site: uncommitted or battery runs void; disclosure of left-out runs | clean |
| Site: readability changes | clean; copy awaits the owner's approval |
| Run script | clean; checked to the publishable step on this tree (it stops there, exit 2, because the tree is not committed yet). The runs themselves have not been made |
