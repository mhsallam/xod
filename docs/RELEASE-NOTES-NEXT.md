# Release notes for the next release (draft)

What changes for users in the next release, collected as each fix lands. The version number and the
release itself are the owner's decision; nothing here is published. Each item links the plan that
records it. The last release: [2.1.0](RELEASE-NOTES-2.1.0.md).

## New

## Behaviour changes

- **A page file a table names that isn't there, or an index entry whose row isn't on its page, is an error.** A read throws `DatabaseFileException` naming the page, or the index file to delete (it is built again from the pages). Before, the rows were left out with no error, so a query could return fewer rows than `Count` said. ([`PLAN-087`](plans/PLAN-087-a-page-is-on-disk-while-a-table-names-it.md))

## Fixed

- **A crash no longer leaves a table naming a page that isn't there.** A new page is written before its table, and a page a delete empties, or a dropped table's pages, are deleted after the table stops naming them, in every mode. ([`PLAN-087`](plans/PLAN-087-a-page-is-on-disk-while-a-table-names-it.md))
- **An update filter takes a relation and its key together.** Targeting `BoxId` alone moves the child to the new box, and skipping `Box` or `BoxId` leaves both; the same for a `[ForeignKey]` reference. ([`PLAN-086`](plans/PLAN-086-an-update-filter-takes-a-relation-and-its-keys-together.md))
