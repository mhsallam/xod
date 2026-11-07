# Fixture: a XOD v1.2.5 database

Produced by the released **v1.2.5** code (tag `v1.2.5`, commit `bcdbcbd`), XML storage, with a small
generator program that used the same model shape as the relational benchmark:

- 10 `FixtureCustomer` rows, 5 `FixtureOrder` rows each, 3 `FixtureLine` rows per order (150 lines)
- 50 `FixtureProduct` rows with a unique `Sku` and an indexed `Id`

The type names are the plain C# names (`FixtureCustomer`, ...) because XOD keys its files by
`Type.FullName`; the test declares the same types in the global namespace so the fixture maps without a
conversion step.

It exists for gate layer 6 of `TEST-PLAN-2.0`: a real 1.x artifact, not one produced by the current
code, so the migration test can only pass if 2.0 reads a 1.x database. Do not regenerate it with a newer
version - that would make the test circular. To rebuild it, check out the tag and run the generator
against `src/Xod/Xod.csproj` from that tree.
