# PLAN-082: a collection XOD can't read back is refused

- **Task ID**: `TASK-097`
- **Status**: Done (2026-11-18)
- **Issue**: `ISS-184`
- **Related**: [`PLAN-073`](PLAN-073-a-value-xod-cant-store-is-refused.md) (a value XOD can't store is refused), `ISS-201`
  (filed)

## 1. The cause

A read makes a `List<T>` for a collection property, or an array for an array property, and assigns it. The writer
decided what a collection is by another test (`GetPropertyTypeCategory`: a generic type that is enumerable), so the
two disagreed:

- a collection class that isn't generic itself (`class NoteList : List<Note>`, `class Numbers : List<int>`, an
  `ArrayList`) was stored as a class, by its own properties, and read back empty;
- a generic collection a `List<T>` can't be assigned to (`HashSet<T>`, `Collection<T>`, a generic subclass) was
  stored, and its read threw.

So: **a collection was written that the reader can't make. The change states what the reader makes, and refuses any
other collection when its class is first used, naming the property, as `PLAN-073` does for a value XOD can't store.**
Making the reader build every collection type would touch each place that reads a collection's item type; the
documented scope is a collection of stored values, which this keeps.

## 2. The change

- **`ValueHelper.UnreadableCollection(type)`**: a collection is an array, a map, or a type a `List<T>` can be assigned
  to (`List<T>`, `IList<T>`, `ICollection<T>`, `IEnumerable<T>`, `IReadOnlyList<T>`, `IReadOnlyCollection<T>`), and
  so are its items.
- **`PropertyService`** refuses a writable property of any other collection when its class loads: `XodException`
  naming it, nothing written. `[NotMapped]` leaves it out.

Behaviour that changes, by design: a class with such a property is refused at its first use; it was stored and read
back empty, or its read threw.

## 3. Tests

`XodCollectionTypeTests`:

- a subclass of `List<Note>` and of `List<int>`, a `HashSet<string>`, an `ArrayList`, and a `List` of a subclass:
  each refused, naming its property: **red before** (stored);
- a guard: `List<T>`, `IList<T>`, `ICollection<T>`, `IEnumerable<T>`, `IReadOnlyList<T>`,
  `IReadOnlyCollection<T>`, an array and a map round-trip.

Each rule broken once and caught: a list's items not checked, a collection that isn't generic passed, any generic
collection passed.

## 4. Left open

- `ISS-201`: an `object` property holding such a collection at run time is written by the runtime type, not refused.
