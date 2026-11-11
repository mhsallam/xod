# XOD conformance set

The judge for "one answer everywhere": every XOD implementation (the C# library today; a Rust core and the
browser later) must give these answers for these files. The design is in
[`docs/plans/PLAN-047-conformance-set.md`](../docs/plans/PLAN-047-conformance-set.md).

| Folder | What |
| :--- | :--- |
| `data/` | the fixtures: one dataset written by XOD 2.0 as XML, JSON, Binary, and XML with a password (`conformance-password`) |
| `cases/` | the cases, one JSON file per group: a XOD SQL query and its expected columns and rows, or its error code |
| `format/` | an independent reader (Node, no packages) and the content every fixture must read to |
| `Xod.Conformance/` | the dataset, the cases in C#, and the runner that checks the C# library |

## Run

```bash
dotnet test conformance/Xod.Conformance -c Release
```

```bash
node conformance/format/read.mjs --check
```

Both run in CI (`.github/workflows/tests.yml`).

## Change

Never edit `cases/` or `data/` by hand: the tests fail when they don't match their source.

```bash
dotnet run --project conformance/Xod.Conformance -f net9.0 -- cases
```

```bash
node conformance/format/read.mjs --write
```

Rewrite the fixtures (`-- data`) only when the dataset or the file format changes on purpose, and say why
in PLAN-047.
