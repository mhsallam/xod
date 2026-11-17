# PLAN-065: a transaction is its flow's

- **Task ID**: `TASK-080`
- **Status**: Done (2026-11-16); reads made to wait too by [`PLAN-066`](PLAN-066-reads-wait-for-another-flow-s-transaction.md) (`ISS-175`), which also took out the refusal of a write inside a read (section 2)
- **Issue**: `ISS-132` (from [`REVIEW-2026-11-14`](../REVIEW-2026-11-14-overall.md), probe P3); filed by it: `ISS-175`
- **Related**: [`PLAN-064`](PLAN-064-one-context-per-database.md) (one context per database: what is concurrent is the
  threads of that one context)

## 1. The cause

A context's transaction was the context's, not its caller's. While one was open, every write of the context went into
its batch and its before-images, whichever thread made it: `BeginTransaction` kept a second transaction out
(`transactionGate`), but a plain `Insert`, `Update` or `Delete` from another thread took the engine's locks and wrote
into the open transaction. Thread B's `Insert` returned, thread A rolled back, and B's row was gone, after a reopen
too (the review's probe P3). The `*Async` wrappers (`Task.Run`) relied on it: a wrapper's write inside a transaction
joined it only because every thread's did.

So: **another thread's write joins a transaction because the transaction belongs to the context, so nothing tells its
writes from anyone else's. The change makes it impossible by giving the transaction to the flow that began it, and
making every other flow's write wait for it to end before it takes a lock.**

## 2. The change

- **A transaction is its flow's** (`XmlEngine.flowTransaction`, an `AsyncLocal`, set by `BeginTransaction`, a method
  that isn't async, so it reaches the caller): an await, a `Task.Run` or an `*Async` wrapper inside the transaction is
  still in it. Every other flow is outside it. A second `BeginTransaction` on the same flow is refused, as on the same
  thread before; on another flow it waits, as before.
- **Every write takes a turn before its locks** (`TakeWriteTurn`, in `EnterExclusive` and `EnterTableWriteLock`, the
  two ways a write takes the engine's locks; given back after them). A write of the transaction's flow goes on. Any
  other waits until no transaction is open, up to `TransactionTimeoutMs`, and is then made outside it, or refused with
  `TransactionException`, nothing of it made. A transaction waits for the writes of other flows under way to end
  before it begins. Waiting writes and transactions go in the order they came, a ticket each (`TransactionTurns`): a
  waiting transaction goes before writes that come after it, and a write that came before a transaction goes before
  it, so neither a stream of writes nor transactions back to back keep the other out.
- **The turn's answer is checked under the locks** (`WriteTurnHolds`): the turn decides which transaction a write is
  part of before it takes its locks, and the write path asks the context's `activeTransaction` after. Nothing keeps
  the transaction open between: its commit, from another thread of its flow, can end it, and another flow's
  transaction begin, since the write took no turn. Once the write holds its locks, a begin, commit or rollback can't
  come (each takes the write lock), so the write checks there that the transaction it decided for is still the open
  one. If not, it lets its locks and its turn go, nothing written, and starts over, outside any transaction now. A
  write outside any that finds one open is a broken invariant, and throws, once it has let its locks and turn go.
- **A transaction ends once, while it is the open one** (`XodTransaction.Complete`): a commit, a rollback or a
  dispose asks whether it is still open under the engine's lock, not only before it. Two completions racing (a
  commit on one thread, the dispose of a timeout on another) both found it open outside the lock; the second then ran
  after another flow's transaction had begun, and discarded or committed that one's batch and ended it.
- **The close ends every wait** (`TransactionTurns.Close`): a write or a transaction waiting for its turn goes on and
  finds the context closed, also when the close couldn't roll its transaction back (a close from inside a read).
- **`TransactionTurns`, not a lock:** a monitor and counts. A transaction can end on another thread than it began on
  (after an await), which `ReaderWriterLockSlim` refuses, and every wait has a deadline.
- **The transaction's own begin, commit and rollback, and the close, take the write lock with no turn**
  (`EnterExclusiveLock`): a commit from another thread than the one that began it, or the close's rollback, doesn't
  wait for itself.
- **No wait that can't end:** a write made inside a read on its thread (a query's predicate) doesn't wait for another
  flow's transaction, whose commit needs the lock the read holds: it is refused at once, but it goes before a
  transaction that is only waiting to begin, which holds no lock yet. `BeginTransaction` inside a
  write on its thread (a trigger) is refused at once (it would wait for that write); inside a read, with the message a
  write there gets. The thread's read locks are counted by the engine (`engineReadDepth`, kept by `EnterRead` and
  `ExitRead`), not asked of the lock (`IsReadLockHeld` is for asserts, ISS-101).

Not changed: reads. A read from another flow while a transaction is open still sees what the transaction has written
so far (`ISS-175`, filed). `GetStatistics`, `Flush` and `ClearCache` take the whole context, as a write does, so from
another flow they wait for the transaction too.

What a flow is follows `AsyncLocal`: a value set in an async method doesn't reach its caller after it returns. A
transaction begun inside an async helper (`var tx = await BeginAsync(db)`) is that helper's; its caller's writes
wait for it, and its rollback or commit, called by the caller, still ends it. A transaction never disposed keeps other
flows' writes waiting until the context closes; they used to join it and be lost at the close. Both are in the
`BeginTransaction` documentation, and the wait's message says a transaction belongs to the code that began it.

## 3. Tests

`XodTransactionFlowTests` (new), a table with no relations and one with children for the two ways a write locks:
- another flow's write during a transaction doesn't join it: with a short `TransactionTimeoutMs`, it is refused with
  `TransactionException`, and the rollback leaves nothing; **red before the change** (it returned, and the rollback
  took it back);
- given time, it waits for the transaction and is then written: after a commit both rows are there, after a rollback
  only its own (green before too, when the other write happened to come late);
- the transaction's own flow writes in it across an await, `InsertAsync` and `Task.Run`, and its rollback takes them
  back;
- another flow's write inside a read is refused at once, saying so (red before: an `XodException` of a different kind);
- a transaction can't begin inside a write (red before: it began);
- a transaction waits for another flow's write under way (held inside its trigger) on its turn, and gives up at
  `TransactionTimeoutMs` with `TransactionException`; the write is then made;
- a write of the transaction's own flow, held between its turn and its locks (`FaultInjection.InProcess` at the step
  `write-turn`, for that write's flow only) while its transaction commits and another flow's begins, isn't taken into
  the other transaction: it starts over, waits for it, and is written once it rolls back. Red without the check
  under the locks (break G10): the write returned inside the other transaction, and its rollback took it;
- a transaction's dispose, held before the engine's lock (`transaction-ending`) after finding it open, while its
  commit ends it and another flow's transaction begins and writes, leaves that one open: another flow's write is
  refused, and its rollback takes its own row back. Red without the check under the lock (break G12): the dispose
  rolled the other flow's transaction back.

`TransactionTurnsTests` (new), at the owner of the order: no engine call can hold a write between its turn and its
locks, so these hold each step of `TransactionTurns` directly (the test project sees the library's internals for
them, `InternalsVisibleTo`): a transaction waits for a write under way; a waiting transaction goes before later
writes, and begins once the writes it waited for end; a write that came before a waiting transaction goes before it;
a write that can't wait passes a transaction only waiting, not an open one; the close ends every wait; one that times
out lets the writes go on; a write waits for an open transaction.

These, the test of a write held in its trigger, the test of the own flow's write after its commit and the test of a
second completion were written after the first version of the fix: the first round of breaks left G7 and G8
uncaught, and the two reviews found the gaps the others close. Each was seen failing under its break (section 4).

## 4. Breaks

Run three times: G1 to G8 on the first version of the fix; G1 to G11 after the first review; all fourteen after the
second. Each restored and the source compared before and after the run. The last round's results:

| Break | Caught by |
| :--- | :--- |
| G1 every flow counts as the transaction's | 5 tests: the issue's case (both tables), the write inside a read, the own flow's write after its commit, and more |
| G2 the transaction never marked as its flow's | many: every test whose transaction writes refuses its own writes |
| G3 the flow is the thread (`ThreadLocal`) | 4 tests: the own-flow test (its `InsertAsync` and `Task.Run` refused), the issue's case, the own flow's write after its commit, and more |
| G4 a write inside a read waits | the write-inside-a-read test, by hanging (its ten-minute wait; the run's hang detector) |
| G5 a write's turn never given back | many: the next `BeginTransaction` waits for a write that never ends, and times out |
| G6 a transaction begins inside a write | the trigger test |
| G7 a transaction doesn't wait for writes under way | 6 tests: `TransactionTurnsTests` (5) and the write held in its trigger |
| G8 writes don't give way to a waiting transaction | `TransactionTurnsTests`: a waiting transaction goes before later writes |
| G9 a write's turn not held over the write (given back as it is taken) | the write held in its trigger |
| G10 the decision not checked under the locks | the own flow's write after its commit |
| G11 a write that can't wait doesn't pass a waiting transaction | `TransactionTurnsTests`: a write that can't wait passes a waiting transaction only |
| G12 a completion not checked under the lock | the second completion |
| G13 no arrival order (a transaction goes before writes waiting longer) | `TransactionTurnsTests`: a write that came before a waiting transaction goes first |
| G14 the close ends no wait | `TransactionTurnsTests`: the close ends every wait |

Without G7 the issue comes back in a narrower window: a write of another flow past its turn, not yet at its locks,
takes them after the transaction's begin, and the write path, which asks whether the context has a transaction
open, writes it into the batch.

## 5. Review

One independent review, read-only, with probes in a copy; told the problem as seen, not the design. It found the
cause removed for other flows' writes (the turn before the locks, a transaction waiting for counted writes, a waiting
one going first; the deadlines only bound the waits), every write and maintenance entry taking the turn, every turn
paired, and the claims about `AsyncLocal`, `ExecutionContext.SuppressFlow`, `ReaderWriterLockSlim` (thread affinity;
`IsReadLockHeld` for asserts) and `Monitor.Wait` right per their documentation.

| Finding | What was done |
| :--- | :--- |
| A write of the transaction's own flow, decided before its locks, joins another flow's transaction begun after its own commit (probe: the row lost, after a reopen too, in most of 40 runs) | Fixed: the decision checked under the locks, the write started over outside any (section 2); a test that holds the order exactly, break G10 |
| A transaction begun inside an async helper or a `Task.Run` blocks its caller's writes, with a message that blames another thread (it joined before) | What a flow is (`AsyncLocal`): documented on `BeginTransaction`, the message says whose a transaction is (section 2) |
| `GetStatistics`, `Flush` and `ClearCache` from another flow wait and throw, against "reads don't wait" | Documented: they take the whole context, as a write does |
| A write inside a read is refused while another transaction is only waiting to begin (it succeeded before) | Fixed: it goes before a waiting transaction, which holds no lock (`passWaiting`); break G11 |
| A transaction never disposed keeps other flows' writes waiting until the close | Documented: such writes used to join it and be lost at the close |
| `ExitExclusive` gave the turn back outside a `finally`; two stale comments; docs saying thread where they mean flow | Fixed |

A second independent review, of the version with those fixes, read-only in a copy: the decision under the locks
removes its cause (every change of `activeTransaction` and of a transaction's state runs under the engine's write
lock, which a write's locks exclude), the retry ends in two passes, every turn and lock is paired, and the new test
holds its order exactly (seen red with the check off).

| Finding | What was done |
| :--- | :--- |
| High, older than the change: two completions of one transaction racing (a commit, and a dispose on another thread) both pass the "already completed" check outside the lock; the second ends, discards or commits the next flow's transaction (probe: 9 of 10 runs) | Fixed: the check made again under the lock (`Complete`), so a transaction ends once; a test holding the dispose before the lock while the commit and the next begin run, break G12 |
| High when reached: the broken-invariant check threw with the write's locks and turn held, so the context hung (reached only through the race above) | Fixed: it throws after letting them go |
| Medium: under transactions back to back from two flows, a plain write never gets a turn (probe: 5 of 5 refused) | Fixed: waiting writes and transactions go in the order they came; break G13 |
| Low: a second read of `activeTransaction` outside the locks, which a commit can null between its two reads | Fixed: read once |
| Low: "a commit or rollback takes the write lock, so the answer holds" isn't so for one from the write's own trigger (the lock is reentrant) | The comment says so: the rest of the statement is then written outside any transaction, and no other begins before it ends |
| Low: a close from inside a read with a transaction open can't roll it back, and leaves its turn open: writes waiting time out instead of finding the context closed | Fixed: the close ends every wait; break G14 |
| The tests that hold an order with the hook had waits with no deadline, and the hook is one process-wide slot | Deadlines added; both tests are in one class, which xUnit runs one at a time |
| `DropType` and a password change wait too, missing from the documentation | Added |

Older than the change and left as they were: a trigger that waits for another thread's write still deadlocks on the
engine lock; `BeginTransaction` waits for long reads on that lock with no deadline; reads see uncommitted rows
(`ISS-175`).

## 6. Verdict

**Clean with named debt.** Another flow's write can no longer join a transaction: every write takes a turn before
its locks, a transaction begins only with no write of another flow under way, the decision is checked under the
locks, where no transaction can begin or end, and a transaction ends once, while it is the open one. The debt, named:
reads from other flows see uncommitted writes (`ISS-175`); what a flow is follows `AsyncLocal` (a transaction begun
inside an async helper is the helper's); `GetStatistics`, `Flush`, `ClearCache`, `DropType` and a password change
wait for another flow's transaction; the broken-invariant check under the locks has no test (only a break of the turn
order reaches it).

## 7. How it was checked

- The issue's case red before the change; every test written after the first version seen failing under its break.
- Fourteen breaks, each caught, in three rounds (section 4); the source compared before and after each round.
- Two independent reviews, read-only, probes in copies (section 5); every finding fixed or documented.
- The gate green on the final code: the build, the tests on .NET 8 and 9, conformance, the format readers, the stress
  harness, the machine-details check.
- The whole suite on Linux (the .NET 9 SDK container, no network): 539 passed.
