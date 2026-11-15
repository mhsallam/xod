# Fixture: a database written by the 2017 XOD (`xod.core`), without a password

The same rows as `../xod.core-2017-encrypted` (12 `Legacy1xMember` rows with 1.x `[Crypto]` digests), written
the same way by the same generator (`../xod.core-2017-encrypted/generator/Program.cs.txt`, given `-` for no
password), so the files are plain XML. Its pages are read as raw bytes, which an encrypted database's are not, so
an update by the primary key index takes the path that reads the row from the raw page (`ISS-151`). Do not
regenerate it with the current code.
