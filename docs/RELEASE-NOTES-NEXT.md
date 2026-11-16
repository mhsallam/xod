# Release notes for the next release (draft)

What changes for users in the next release, collected as each fix lands. The version number and the
release itself are the owner's decision; nothing here is published. Each item links the plan that
records it. The last release: [2.0.3](RELEASE-NOTES-2.0.3.md).

## New

## Behaviour changes

- **A transaction belongs to the code that began it.** While it is open, a write from another thread no longer
  joins it (to be taken back by its rollback after it had returned). It waits for the transaction to end, up to
  `TransactionTimeoutMs`, and is then made on its own, or throws `TransactionException` with nothing written. An
  `await`, a `Task.Run` or an `*Async` method inside the transaction is still part of it. `BeginTransaction` inside
  a write (a `BeforeAction` or `AfterAction` handler) now throws `TransactionException`. A write from inside a
  query's predicate on another thread, while a transaction is open, throws at once. `Flush`, `ClearCache`,
  `GetStatistics`, `DropType` and a password change from another thread wait for it too. A transaction begun inside
  an async method belongs to that method, so its caller's writes wait for it; begin it where its writes are made. A
  transaction never disposed keeps writes from other threads waiting until the context closes.
  ([`PLAN-065`](plans/PLAN-065-a-transaction-is-its-flow-s.md))

## Fixed

- **A transaction completed twice at once no longer ends the next one.** A `Commit` on one thread and a `Dispose` on
  another could both go ahead; the second then ended, discarded or committed the transaction another thread had
  begun meanwhile. A transaction now ends once. ([`PLAN-065`](plans/PLAN-065-a-transaction-is-its-flow-s.md))
