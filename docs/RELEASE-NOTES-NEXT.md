# Release notes for the next release (draft)

What changes for users in the next release, collected as each fix lands. The version number and the
release itself are the owner's decision; nothing here is published. Each item links the plan that
records it. The last release: [2.0.3](RELEASE-NOTES-2.0.3.md).

## New

## Behaviour changes

- **A transaction belongs to the code that began it, and other threads wait for it.** While it is open, a read or a
  write from another thread waits for it to end, up to `TransactionTimeoutMs`, then goes on, or throws
  `TransactionException` with nothing done. A write no longer joins the transaction (to be taken back by its rollback
  after it had returned), and a read no longer sees what the transaction has written before its commit. An `await`, a
  `Task.Run` or an `*Async` method inside the transaction is still part of it. So a long transaction holds up other
  threads' reads: keep transactions short. The other way round, `BeginTransaction` waits for other threads' reads and
  writes under way, and throws `TransactionException` if one outlasts `TransactionTimeoutMs`. `Flush`, `ClearCache`, `GetStatistics`, `DropType` and a password change
  wait too. `BeginTransaction` inside a write (a `BeforeAction` or `AfterAction` handler) now throws
  `TransactionException`. A transaction begun inside an async method belongs to that method, so its caller's reads
  and writes wait for it; begin it where its work is done. A transaction never disposed keeps other threads waiting
  until the context closes. ([`PLAN-065`](plans/PLAN-065-a-transaction-is-its-flow-s.md),
  [`PLAN-066`](plans/PLAN-066-reads-wait-for-another-flow-s-transaction.md))

## Fixed

- **A transaction completed twice at once no longer ends the next one.** A `Commit` on one thread and a `Dispose` on
  another could both go ahead; the second then ended, discarded or committed the transaction another thread had
  begun meanwhile. A transaction now ends once. ([`PLAN-065`](plans/PLAN-065-a-transaction-is-its-flow-s.md))
