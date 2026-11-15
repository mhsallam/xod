# Fixture: an encrypted database written by the 2017 XOD (`xod.core`)

Written by the original XOD code, `mhsallam/xod.core` at commit `b50d400` (2017-04-01), XML storage, under
the password `legacy-pass`, by the generator in `generator/Program.cs.txt` (kept as `.txt` so the test project
doesn't compile it; given `-` for no password, it wrote `../xod.core-2017-plain`):

- 12 `Legacy1xMember` rows, `Id` 1 to 12, `Name` "member n";
- `Pin` = "pin-n" under `[Crypto]` (MD5, the 1.x default) and `Secret` = "secret n" under
  `[Crypto(CryptoMethod.SHA1)]`, stored as 1.x digests: the decimal digits of each byte of the hash of the
  value's ASCII bytes, run together.

Every file is encrypted the 1.x way: AES-CBC, the key and IV the first 32 and 16 ASCII characters of the decimal
digits of SHA-256 of the password's ASCII bytes (`xod.core/src/Helpers/CryptoHelper.cs`, `FileCryptoHelper.cs`).

The 2017 sources (`git archive b50d400 src`) were built unchanged against a current .NET (a new project file
targeting net9.0, the `backup` folder left out; the original targets were netcoreapp1.0 and net46), and the
generator ran against that build. The encryption, hashing and file layout are the 2017 code's; only the compiler
and runtime are current. It exists for `ISS-151` (`XodLegacy1xCryptoTests`): a real 1.x artifact, not one made
by the current code, so the tests pass only if XOD reads what 1.x wrote. Do not regenerate it with the current
code.
