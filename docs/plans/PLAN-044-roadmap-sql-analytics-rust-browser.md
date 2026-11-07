# PLAN-044: Roadmap - SQL, analytics, a Rust core and XOD in the browser

- **Task ID**: `TASK-055` (the roadmap), `TASK-056` (step 0: make 1.0 installable)
- **Status**: Proposed (2026-11-07). The direction is the owner's; the order, the packed-file design and
  the open decisions below await the owner. Nothing is built.
- **Related**: `ISS-045` (the install path), `ISS-046` (memory and the device claim), `ISS-044` (reads
  barely scale), [`PLAN-043`](PLAN-043-stress-publishing-run.md) (the measured numbers quoted here)

## What the owner wants (2026-11-07)

1. Analytical capabilities.
2. A SQL-like scripting language to work with the database.
3. A native Rust version, faster if it can be.
4. XOD in WASM, ideally declared in HTML (a `<link>` to a `.xod` file) and queried in the page: dynamic
   SQL and analytics on the client.

## Where XOD stands

### What makes it special

- **Unique: a real database whose data you can read.** ACID transactions, indexes and concurrent readers
  over pages that are XML (the default) or JSON files: open them in an editor, diff them in a pull
  request, grep them, keep them in git. SQLite and LiteDB store binary files; plain JSON files have no
  transactions or indexes. This is the hook to lead with.
- **Shared with others:** objects in and out with lambda queries, no SQL and no migrations (LiteDB does
  this too); speed (every engine claims it - XOD's plus is publishing its runs in full).

### Who it is convenient for today

- .NET developers building desktop apps, tools, small services and device software: one package, no
  server, no schema.
- Not yet for: other languages; teams that want SQL or BI tools; heavy analytics on large data; several
  processes writing to one database.

### Frictions, measured

- **Nobody can install the current library** (`ISS-045`): the site's `dotnet add package Xod` gets 0.1.1031 from 2017.
- **Memory** (`ISS-046`): private memory peaked at 13.2 GB for a 4.85 GB file in the 10M-row capacity
  runs, about 2.7x the data (PLAN-043). The landing page's "Raspberry Pi, industrial PCs and .NET boards"
  card has never been measured.
- **Under a writer** (the 60-minute soak, PLAN-043): garbage collection paused the process 20.4% of the
  time; the slowest read took 906 ms and the slowest commit 302 ms. Reads barely scale with threads
  (`ISS-044`).
- **Readable files cost something:** XML costs disk and parse time, and most people never open their
  database files. The people who do value it a great deal; aim at them.
- **Maturity:** new, few users. Trust comes from releases, docs, samples and the published benchmarks.

### How the roadmap changes that

- SQL is the biggest convenience gain for most people: everyone knows it, and BI tools and LLMs write it.
- A Rust core opens XOD to other languages and is likely to cut the memory and pause costs - measured
  first (step 2).
- WASM in the page is distinctive, for a narrower audience (below).
- Through all of it, "data you can read" stays the centre - which is why the packed file below is a zip of
  the same readable pages.

## The order

**Why this order: the four depend on each other.**

- **SQL and analytics are one feature.** Most of what "analytics" means is what the language can express
  (GROUP BY, SUM, window functions); design them together.
- **Rust decides where SQL is built.** SQL built in C# and then a move to Rust means writing the query
  engine twice, and two copies drift.
- **WASM depends on Rust.** Rust compiles to small, fast WASM; .NET in the browser ships its runtime
  (several MB), which suits Blazor apps, not one script tag.

### Step 0: ship the current .NET library to GitHub and NuGet (`TASK-056`, `ISS-045`)

Before any feature, a reader must be able to install and read what the site describes. **The owner's
direction (2026-11-07):** ship the current .NET XOD (`xod2`) to GitHub and NuGet under the existing
names, `github.com/mhsallam/xod` and the `Xod` package, with the 2017 code moved to a `legacy` branch.

What stands there now (checked 2026-11-07): `mhsallam/xod` is public, its only branch is `master` (the
2017 code, last pushed 2017-04-01), it has no tags and 3 forks; `Xod` on NuGet lists 0.1.0-beta to
0.1.1031 (2016-2017) and holds 1.0.0-beta to 1.0.0-beta03 unlisted, all from the old project.

**Decisions first (the owner's).** Decided 2026-11-07: **2.0.0**, and **a fresh public history**.

1. **Version number.** The project file and the site say 1.0.0; the docs call the old library "XOD 1.x"
   and this one 2.0 (`MIGRATION-GUIDE.md`, `TEST-PLAN-2.0.md`, the first commit); the tags are
   `internal-1.2.x`; and NuGet already holds the old project's 1.0.0 pre-releases, so a new 1.0.0 would
   read as their final release. **Recommended: 2.0.0**, and the project file, the site's version stamp
   and the docs follow it.
2. **What history goes public.** `xod2`'s 120 commits carry the machine a run used: 24 tracked files (13
   stress results, 8 archived results, the comparison result, two plans), touched by 15 commits.
   Publishing the history as it is exposes what the owner keeps off the site.
   - **Recommended: a fresh public history** - one commit from a cleaned snapshot; the full local
     history stays private. The commits the site cites (`4926465`, `9ddc203`) would not exist publicly,
     so the publishable runs are made again from the public commit (about 70 minutes for the soak and
     capacity runs, plus the comparison suite) and the site cites those.
   - The alternative, rewriting the history (`git filter-repo`) to strip the machine fields, keeps the
     story but changes every commit id: the stamps inside the result files and the site's citations
     would point at commits that no longer exist, so the runs are needed again anyway.
   - Either way, first make the harness keep the machine identity out of committed files (a git-ignored
     sidecar for reproducing a run), or the next run puts it back. **Done 2026-11-07:** a stress header
     carries only `onAcPower`, a comparison stamp the code and the .NET major version, and the rest goes
     to `.machine/<result>.json` beside the result (`Machine.RecordPath`), which git ignores
     (`benchmarks/results/**/.machine/`). Tests: the header and stamp carry no machine fields, the record
     is written, git ignores it - each seen failing (the ignore test caught a rule that covered only the
     top-level folder, not `stress/`). The site reads the power reading from either place (old and new
     files), with a test that failed before.
3. **Clean the public copy:** two docs named local folders (`HANDOFF-2026-11-04.md`, `PLAN-034`) -
   replaced with plain names on 2026-11-07, none left in tracked files. **Secret scan, 2026-11-07:**
   gitleaks 8.30.1 (the official release, checked against its published checksums), secrets masked in
   the output, over an export of every tracked file (229) and over all 121 commits. One finding, the
   same in both: the value a test model gives its `SecretApiKey` property in
   `tests/Xod.Tests/XodTests.cs` (since the first commit) - a 14-character placeholder that only looks
   like a key prefix, test data rather than a key. In the public snapshot it becomes an obviously fake
   value so the public repository scans clean; the snapshot is scanned again before it is pushed.

**Done locally on 2026-11-07:** the library is 2.0.0 (`src/Xod/Xod.csproj`; the CLI already was), and
the licence note (README, NOTICE) says versions before 2.0.0 - the 0.1.x line and the 1.0.0 betas -
stay MIT. Gates: `Xod.Tests` 60/60 on .NET 8 and .NET 9, `XodBench.Tests` 25/25, site tests 31/31.

**Public snapshot, built locally on 2026-11-07 (nothing pushed):** a branch `main` with one commit and
no parent, author `mhsallam`, message "XOD 2.0.0". Its tree is `master`'s at the commit that records
this, without the 42 result files from before that date (`benchmarks/results/archive/`, `stress/`,
`unpublished/` and the comparison result at the top; `benchmarks/results/README.md` stays) and without
`docs/screens/benchmarks.png`, a screenshot of the old site that shows the .NET patch level: 186 files.
Before it was built, the plans that named the machine (`PLAN-040`, `PLAN-042`, `PLAN-043`) were
reworded and the test placeholder became `not-a-real-key`. Checked on `main`: no machine identity or
local path (searched), gitleaks finds nothing in its commit, `Xod.Tests` 60/60 on .NET 8 and .NET 9,
`XodBench.Tests` 26/26, and the publishable check passes with a clean stamp (dry run). `master` keeps
the full private history and the old result files, which the site's data build still reads (`xod-site`
reads `../xod2/benchmarks/results`): don't rebuild the site from a `main` checkout until the new runs
are committed there.

**Found while checking it, fixed (2026-11-07):** the harness found its repository by looking for a
`.git` folder, which a linked worktree or a submodule does not have (`.git` is a file there). From a
worktree the comparison suite stamped its results `unknown` and wrote them under `bin/`, and the
harness test of the ignore rule failed; the stress stamp asked git from the current folder instead, a
second answer to the same question. Now one owner, `Machine.RepoRoot()`, asks git
(`rev-parse --show-toplevel`) from the folder the binary runs in, and both stamps use it. Test
`The_harness_finds_the_checkout_it_was_built_from`: seen failing in a worktree with the old lookup,
passing in a worktree and in the main checkout with the new one. Named debt: two git runners
(`Machine.Git`, `BenchStamp.Git`) and two definitions of a dirty tree (`Machine.IsDirty` ignores
`benchmarks/results/`; the comparison stamp also ignores `benchmarks/dashboard.html`).

**Next, in order (each outward step on the owner's go):** the publishable runs from the public commit,
with the machine left alone (the soak and capacity runs take about 70 minutes, the comparison suite's
length is not recorded); then GitHub, NuGet and the site, below. The README line about the `legacy`
branch (GitHub step 2) goes in as a commit on `main` before the push.

**GitHub** (nothing force-pushed, nothing overwritten):

1. Rename `master` to `legacy` on `mhsallam/xod` and tag its head (`legacy-final`). GitHub keeps
   redirects for links to the old branch name; the 3 forks keep their copies.
2. Push the release as `main` and make it the default branch; the README says at the top that the 2017
   library lives on `legacy`.
3. Update the repository's description (now "Light-weight yet powerfull XML-based OO/NoSQL thread-safe
   embedded database"), topics and homepage (xod.sabaeon.com).
4. The test workflow (`.github/workflows/tests.yml`, Windows and Linux, both frameworks) runs for the
   first time; a green run is part of the release.
5. Tag the release (`v2.0.0` if decided) and publish release notes.

**NuGet:**

1. `dotnet pack -c Release` with the README, licence, repository link and commit (Source Link) and
   symbols; install the package in a clean project on .NET 8 and .NET 9 and run open, insert, query,
   reopen before pushing.
2. Push `Xod` with the owner's API key: the owner runs the push, or the key comes from an environment
   variable, never the command line.
3. Deprecate the old listed versions (0.1.x) and `Xod.Core` with a message pointing at the new version
   (deprecation keeps old restores working; unlisting does not help them).
4. Optional: the CLI as a .NET tool (`Xod.Cli`: `stats`, `convert`, `secure`, `loose`, later `pack`).

**Site:** Get started and the Source link point at the new default branch and version. `ISS-045` closes
when `dotnet add package Xod` in a clean project installs the release.

### Step 1: two specs, no engine code

- **File format:** pages, index sidecars and the journal, exact enough that a second implementation can
  read today's files. The Rust trial and the browser both need it.
- **Query language:** a real SQL subset rather than a new language - people, BI tools and LLMs already
  write SQL. XOD extensions only where the object model needs them (walking `Customer.Orders`).
  Read-only `SELECT` first; how writes look is decided here too.
- **A conformance set:** a fixed dataset, queries and their expected results that every implementation
  must pass. It keeps C#, Rust and WASM giving the same answers (the differential idea of `TASK-049`,
  applied to XOD's own implementations).

### Step 2: a Rust trial, measured (alongside the language spec)

- Port only the read path and inserts, read today's files, run today's `XodBench` workloads.
- Targets from the soak and capacity runs: GC paused 20% of the time, slowest read 906 ms, memory 2.7x the
  file.
- Set the bar for "clearly faster" before starting, as the soak rules were set before the soak.
- It also separates runtime cost from design cost in `ISS-044`: if reads stop scaling because of the
  locking design, Rust will not fix it.

### Step 3: build the query engine once, in whichever language wins

- Parser, plan, then the operators: filter, project, join, group, sort.
- First analytics set: COUNT, SUM, AVG, MIN, MAX; GROUP BY; DISTINCT; ORDER BY and LIMIT; JOINs over the
  object relations.
- An analytics suite in `XodBench` (scans and group-bys at 1M and 10M rows) so "fast" is measured, not
  claimed.

### Step 4: WASM and the browser (if Rust won)

- Keep the core browser-friendly from step 3: storage behind an interface, no assumptions about files or
  threads. Then this step is mostly packaging.
- Load a packed `.xodx` (next section) by HTTP range requests: the directory first, then only the pages a
  query touches.
- The `<link>` works, but not natively: browsers ignore link types they do not know, so a small module
  script finds them:

  ```html
  <link rel="xod" href="/data/shop.xodx">
  <script type="module" src="/xod.js"></script>
  ```

  then `const db = await xod.open('shop')` and `db.query("SELECT region, SUM(total) ...")`.
  `<link rel="preload">` can start the download early, and that part is native.
- Read-only first; saving in the browser (its private file storage, OPFS) later.

**Who it suits, honestly:** a minority, very well. Most web apps keep data on a server and need no
database in the page. It suits static sites and dashboards with no backend, articles or docs with a
dataset readers can explore, prototypes, offline tools and internal reports. Limits that apply to all:

- anything sent to the browser can be downloaded - it suits data you would publish anyway, or per-user
  data fetched after sign-in;
- a lookup fetches a few pages, but analytics over a whole table needs most of it: fine at megabytes, not
  gigabytes, above all on phones;
- writes stay in that browser unless sync is built, and sync is the hard part;
- DuckDB-WASM and SQLite-WASM already run SQL in the browser. XOD's edges: two lines of HTML and no build
  tools, the same file in the .NET app and the page, and data you can read.

### Step 5: .NET moves onto the Rust core

C# keeps what makes it XOD - objects in and out, lambda queries turned into plans - and calls the core.
Switch only when it beats the C# engine on the same benchmarks; until then the C# engine ships.

### Step 6: heavier analytics, when the numbers call for it

Window functions and time buckets; a columnar storage format only if the analytics suite shows row pages
are too slow for scans. It would be a fourth format, so XML stays the readable default.

### Decisions before step 1

- **One core or two products.** Rust as the single core with C# a layer over it (recommended: two engines
  doing one job is the drift problem again), or Rust as a separate product.
- **Who the browser user is**, given DuckDB-WASM and SQLite-WASM.

XOD 1.0 in C# keeps shipping throughout; a new core replaces it only when the benchmark says so.

## The packed file and "data you can read"

**The owner's question: does one packed file contradict "you can read"?** It would if "packed" meant an
opaque binary file. The proposal is one database in two containers, with the same readable files inside:

- **Folder (the default, to work in):** what XOD is today - a directory holding the root `.xod` file, one
  file per page, the `.xidx` index sidecars and `transaction.xwal`. Readable and diffable; the form to
  write to and to keep in git.
- **Packed, `.xodx` (to ship):** a standard zip of the same files, one file to download, attach, host or open in
  the browser.
  - Still readable: any unzip tool, Windows Explorer and macOS Finder open it and show the same pages.
  - A zip keeps its directory at the end of the file, so a reader can fetch the directory and then only
    the entries it needs, by range requests - what step 4 relies on.
  - Entries stored as they are or compressed: readable byte ranges against size (HTTP compression already
    covers transfer). Decided in step 1.
- **`xod pack` and `xod unpack`** in the existing CLI (beside `stats`, `convert`, `secure` and `loose`),
  and the library opens either form. The packed form is read-only at first: rewriting a zip on every
  commit is the wrong shape. Work in the folder, pack to ship - like source and a build.
- **git:** keep the folder in git, not the pack. A committed pack can still show text diffs through a git
  diff driver (`textconv` over `unzip -p`); optional.
- **Extension: `.xodx`** (the owner, 2026-11-07, for now). Like `.docx`, which is a zip of XML parts,
  the name tells a reader it is a zip they can open (rename it `.zip`). Not `.xodb`, which reads as XOD's
  Binary storage format; not `.xodz`, which implies compression and sits next to ASAM OpenDRIVE's
  `.xodrz`. No existing use of `.xodx` was found (web search, 2026-11-07). A separate extension also keeps
  the pack apart from Apryse's `.xod`, its web-optimised XPS document format - a name the database folder
  and root file already share.

## Sources (checked 2026-11-07)

- NuGet registration for `Xod`: newest listed version 0.1.1031 (published 2017-04-01); 1.0.0-beta to
  1.0.0-beta03 unlisted; `xod2`'s `Xod` 1.0.0 (`src/Xod/Xod.csproj`) not published.
- GitHub `mhsallam/xod`: last push 2017-04-01, 12 stars. `xod2` has no remote.
- Memory, GC, slowest read and commit: PLAN-043, Results.

## Verdict

Proposal only. Every fact above is quoted from a measured run or a source checked on the date given;
nothing in it is built or decided.
