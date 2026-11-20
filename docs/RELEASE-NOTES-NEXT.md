# Release notes for the next release (draft)

What changes for users in the next release, collected as each fix lands. The version number and the
release itself are the owner's decision; nothing here is published. Each item links the plan that
records it. The last release: [2.1.0](RELEASE-NOTES-2.1.0.md).

## New

- **`DatabaseOptions.Legacy1xDigests`, for a database that holds XOD 1.x `[Crypto]` digests.** With it on, an MD5 or SHA1 digest read from it stays a digest when that value is written: inserted into another database, given to another row, or written again with its parent. A copy of it as new text (serialized and back) is still hashed. Off, as before, only an update of its own row keeps it, and any other write hashes it again, so it no longer verifies. Leave it off for a database that may store `[Crypto]` values as plain text, where digits read the same as a 1.x digest. ([`PLAN-089`](plans/PLAN-089-page-bytes-stay-a-readers-and-a-read-digest-stays-a-digest.md))

## Behaviour changes

- **A `Guid? Id` gets a new `Guid` when it is inserted null**, as an `int? Id` gets a number: by convention it is an autonumber now. Before, every row inserted with a null `Id` kept a null key. Rows already stored keep theirs. ([`PLAN-090`](plans/PLAN-090-four-small-fixes-stray-files-interface-maps-snapshot-failures-guid-ids.md))
- **A map property of a type XOD can't make is refused when its class is first used**: `ReadOnlyDictionary<K, V>`, `ImmutableDictionary<K, V>`, `FrozenDictionary<K, V>`. Before, it was accepted, read back null when it was null, and threw when it held a value. Declare it as `IReadOnlyDictionary<K, V>` or `Dictionary<K, V>`. ([`PLAN-090`](plans/PLAN-090-four-small-fixes-stray-files-interface-maps-snapshot-failures-guid-ids.md))

- **A page file a table names that isn't there, or an index entry whose row isn't on its page, is an error.** A read throws `DatabaseFileException` naming the page, or the index file to delete (it is built again from the pages). Before, the rows were left out with no error, so a query could return fewer rows than `Count` said. ([`PLAN-087`](plans/PLAN-087-a-page-is-on-disk-while-a-table-names-it.md))

## Fixed

- **A file beside the database that names another database file is left alone.** The open deleted a stray `other.xod.tmp`, or turned other leftovers into a second database file that every later open refused, and a password change was refused over them. Only what this database's own crash left is recovered now. ([`PLAN-090`](plans/PLAN-090-four-small-fixes-stray-files-interface-maps-snapshot-failures-guid-ids.md))
- **A property declared as `IDictionary<K, V>` or `IReadOnlyDictionary<K, V>` is stored as a map.** It was refused, and in 2.0.x stored as text it couldn't read back. ([`PLAN-090`](plans/PLAN-090-four-small-fixes-stray-files-interface-maps-snapshot-failures-guid-ids.md))
- **A failure while saving an index is thrown as itself**, whatever the number of indexed properties, not wrapped in an `AggregateException`. ([`PLAN-090`](plans/PLAN-090-four-small-fixes-stray-files-interface-maps-snapshot-failures-guid-ids.md))

- **A page a crash left behind is removed.** A crash between writing a page and writing the table that names it could leave a page file nothing used. The next open now deletes it. Only pages the crashed process was writing are touched, so a table file that is missing for another reason never costs its pages. ([`PLAN-088`](plans/PLAN-088-a-page-no-table-names-is-deleted-at-the-open.md))
- **A reference of type `object` to a row that was deleted reads as null.** It threw, saying the type isn't registered, and the row holding it couldn't be read. ([`PLAN-088`](plans/PLAN-088-a-page-no-table-names-is-deleted-at-the-open.md))
- **A crash no longer leaves a table naming a page that isn't there.** A new page is written before its table, and a page a delete empties, or a dropped table's pages, are deleted after the table stops naming them, in every mode. ([`PLAN-087`](plans/PLAN-087-a-page-is-on-disk-while-a-table-names-it.md))
- **An update filter takes a relation and its key together.** Targeting `BoxId` alone moves the child to the new box, and skipping `Box` or `BoxId` leaves both; the same for a `[ForeignKey]` reference. ([`PLAN-086`](plans/PLAN-086-an-update-filter-takes-a-relation-and-its-keys-together.md))
