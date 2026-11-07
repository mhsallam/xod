# PLAN-002: Idempotent `[Crypto]` One-Way Property Hashing Across `Update`

- **Task ID**: `TASK-007`
- **Fixes Issue**: [`ISS-007`](../KNOWN-ISSUES.md)
- **Status**: Implemented

## Problem Statement
When a string property is decorated with `[Crypto(CryptoMethod.SHA256)]`, `[Crypto(CryptoMethod.SHA512)]`, `[Crypto(CryptoMethod.MD5)]`, or `[Crypto(CryptoMethod.SHA1)]`, `XmlEngine.WriteProperty` hashes the value whenever `Write` is invoked.
When an existing entity is loaded from the database, its property holds the hex digest string. Calling `db.Update(entity)` (even when only updating an unrelated property like `Score`) invokes `WriteProperty` again and hashes the hex digest a second time, corrupting the stored password hash.

## Design & Refactoring Steps
1. Add `CryptoHelper.HashProperty(string value, CryptoMethod method)` that checks whether `value` is already a valid hex digest of the exact length for that algorithm (32 hex chars for MD5, 40 for SHA1, 64 for SHA256, 128 for SHA512) or carries a digest prefix, and if so, returns it unchanged (idempotent).
2. Add a helper `CryptoHelper.VerifyHash(string plainText, string storedHash, CryptoMethod method = CryptoMethod.SHA256)` so applications can easily verify a user's plaintext password against the stored `[Crypto]` property.

## Verification Plan
- Add unit test `Crypto_Hashed_Property_Is_Idempotent_Across_Multiple_Updates` that inserts a `UserAccount`, updates `Score` multiple times, and verifies `PasswordHash` remains identical to `CryptoHelper.GetSHA256HashData("PlainPassword123")` and passes `CryptoHelper.VerifyHash`.
