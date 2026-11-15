# Fixture: a database written by the 2017 XOD (`xod.core`) under a password that isn't ASCII

The same rows as `../xod.core-2017-encrypted`, written the same way by the same generator, under the password
`pässwort` (the generator received U+00E4 for the `ä`). 1.x hashed a password's ASCII bytes, which turn each
character outside ASCII into `?`, so its files are keyed by `p?sswort`: the database opens in XOD++ with
`"p?sswort"`, not with `"pässwort"` (`ISS-151`, `MIGRATION-GUIDE` section 2.1). Do not regenerate it with the
current code.
