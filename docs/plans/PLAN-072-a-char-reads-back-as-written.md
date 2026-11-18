# PLAN-072: a char reads back as written

- **Task ID**: `TASK-087`
- **Status**: Done (2026-11-18)
- **Issue**: `ISS-140`
- **Related**: [`VERIFY-2026-11-18`](../VERIFY-2026-11-18-open-issues.md) (the check that found it still there),
  [`PLAN-046`](PLAN-046-file-format-spec.md) section 7.5 (a `char` is stored as is)

## 1. The cause

`ValueHelper.ParseScalar`, which every reader and the index's key from a row go through, gave a type's default for
text that is all whitespace, before any parse by type. A `char` stores its one character as is, so `' '`, a tab or a
no-break space read back `'\0'` (a `char?` null) in every format, and an index rebuilt from the rows keyed it 0 while
one built from the objects keyed 32: a query by it found the row until the index was rebuilt.

So: **a whitespace `char` read back as `'\0'` because the rule for blank text came before the `char` was read. The
change reads a `char` from its one character first, in the one parser.**

## 2. The change

`ParseScalar` returns a one-character text as the `char` before the blank-text rule. Text of any other length goes on
as before (an empty one is the default, or null for a `char?`). Every reader goes through it: the fast row reader, the
document reader, the index's key from a row, SQL from the class. SQL from the manifest reads a `char` as text, and the
SQL parameters already took a one-character string as the character.

## 3. Tests

`XodTextRoundTripTests.Every_char_reads_back_exactly`: a space, a no-break space, a tab, an em space, CR, LF, `x` and
`<`, flat (the fast row writer and reader, by page too, a `char?`) and in a document (an element and an attribute), in
every format, in the same context, after a reopen and after the index is rebuilt from the rows; an indexed query by
each finds its row, and one by U+0000 finds none. **Red before** in every format.

## 4. Breaks

| Break | Caught by |
| :--- | :--- |
| E1 a whitespace `char` left to the blank-text rule | 4 (the test, in each format) |

## 5. Review

An independent review of this change and `PLAN-073`'s, given the code and the problems, with probes against the code
before the change too. For this one: the cause removed in the one parser every reader shares (the fast row reader, the
index rebuild, SQL and so the CLI, map keys and values); `'\0'` and whitespace text for other types unchanged. Its
findings:

- **Text of more than one character for a `char`** reads two ways: `"ab"` throws, `"  "` reads `'\0'`. No XOD writer
  writes either; only a file edited by hand or written elsewhere holds one. Left as it was (named debt).
- **The Node reader keys a `char` by its text** (`conformance/format/read.mjs`), where the format and the library key it
  by its code point (PLAN-046 section 8.3). No conformance model indexes a `char`, so nothing reaches it: filed as
  `ISS-183`.

## 6. Verdict

**Clean, with named debt.** The parser every reader uses reads a `char` before the blank-text rule. The debt: a `char`
stored as more than one character (by hand) reads two ways; the Node reader's key (`ISS-183`).

## 7. How it was checked

- The test red before, in every format; the break.
- The review (section 5).
- The gate (`node tools/gate.mjs`), and the whole suite on Linux.
