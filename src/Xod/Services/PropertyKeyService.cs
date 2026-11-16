using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Xod.Helpers;

namespace Xod.Services
{
    /// <summary>
    /// The one owner of <c>[Crypto(AES256)]</c> keys (ISS-050, ISS-058; PLAN-049).
    ///
    /// A database encrypts its AES256 values with one random 256-bit <b>data key</b>, made by a context that holds
    /// the database (ISS-150, PLAN-064): when it is created, after a password change, or else at the first value; a
    /// context with no hold is refused. The data key is stored in the database file in <c>PropertyKey</c> elements, each
    /// the key encrypted under a key derived from a <b>protector</b>: the application's
    /// <see cref="DatabaseOptions.PropertyEncryptionKey"/> or the database password. A new key is protected
    /// by the application key when the context has one, else by the password. With neither there is
    /// nothing to protect it with, and the write is refused: a value the library could decrypt by itself
    /// could be decrypted by anyone with XOD.
    ///
    /// A password change protects the same data key again; no value is rewritten. The protection for the
    /// new password is added before the files change (<see cref="PasswordChanging"/>) and the old one
    /// removed after (<see cref="PasswordChanged"/>), so a change that fails part-way leaves the key
    /// openable with whichever password opens the files.
    ///
    /// Values written by 2.0 (<c>enc:aes256:</c>) stay readable. 2.0 derived their key from the password
    /// in effect when they were written, or from a constant without one; when the password changes while
    /// such values exist, the outgoing password's key is kept in a <c>LegacyPropertyKey</c> element,
    /// encrypted with the data key.
    ///
    /// The database file is the record. The data key, once opened, is cached; anything that decides from
    /// the file reads it afresh (<see cref="IOService.Reopen"/>). With one context at a time (PLAN-064) only a file
    /// replaced from outside can differ from the cache; the re-read is the owner's guard, not a coordination.
    /// </summary>
    internal sealed class PropertyKeyService
    {
        internal const string ValuePrefix = "enc:aes256k:";
        internal const string LegacyValuePrefix = "enc:aes256:";
        private const string KeyElement = "PropertyKey";
        private const string LegacyKeyElement = "LegacyPropertyKey";
        private const string ProtectorPassword = "password";
        private const string ProtectorApplication = "application";
        private const int Iterations = 100_000;

        private static readonly byte[] KeyAad = Encoding.ASCII.GetBytes("XOD property key v1");
        private static readonly byte[] LegacyKeyAad = Encoding.ASCII.GetBytes("XOD legacy property key v1");

        private readonly object syncLock = new object();
        private readonly IOService ioService;
        private readonly string databaseFileName;
        private readonly string applicationKey;
        private readonly Func<string> password;

        private byte[] dataKey;
        private List<byte[]> legacyKeys;

        /// <param name="ioService">The file owner: the database file is read and written through it.</param>
        /// <param name="databaseFileName">The database file, which holds the key's protections.</param>
        /// <param name="applicationKey"><c>DatabaseOptions.PropertyEncryptionKey</c>, or null.</param>
        /// <param name="password">The database password as the engine holds it (its SHA-256 hex), read when
        /// needed, since <c>Secure</c> and <c>ChangePassword</c> change it.</param>
        internal PropertyKeyService(IOService ioService, string databaseFileName, string applicationKey, Func<string> password)
        {
            this.ioService = ioService;
            this.databaseFileName = databaseFileName;
            this.applicationKey = string.IsNullOrEmpty(applicationKey) ? null : applicationKey;
            this.password = password;
        }

        /// <summary>Forgets the cached keys: the next use reads the database file again.</summary>
        internal void Reset()
        {
            lock (syncLock)
            {
                dataKey = null;
                legacyKeys = null;
            }
        }

        // ---- values ----

        internal string Encrypt(string plainText, string propertyName)
        {
            if (string.IsNullOrEmpty(plainText))
                return plainText;

            byte[] key;
            lock (syncLock)
            {
                key = dataKey ?? DataKeyUnlocked(DatabaseFile(), create: true, propertyName);
            }
            return ValuePrefix + Convert.ToBase64String(Seal(key, Encoding.UTF8.GetBytes(plainText), null));
        }

        internal string Decrypt(string stored, string propertyName)
        {
            if (string.IsNullOrEmpty(stored))
                return stored;

            if (stored.StartsWith(ValuePrefix, StringComparison.Ordinal))
            {
                byte[] key;
                lock (syncLock)
                {
                    key = dataKey ?? DataKeyUnlocked(DatabaseFile(), create: false, propertyName);
                }
                byte[] plain = Open(key, Payload(stored, ValuePrefix), null)
                    ?? throw new SecurityException($"Property '{propertyName}' couldn't be decrypted: the value doesn't match this database's key.");
                return Encoding.UTF8.GetString(plain);
            }

            if (stored.StartsWith(LegacyValuePrefix, StringComparison.Ordinal))
            {
                byte[] payload = Payload(stored, LegacyValuePrefix);
                foreach (byte[] key in LegacyCandidates())
                {
                    byte[] plain = Open(key, payload, null);
                    if (plain != null)
                        return Encoding.UTF8.GetString(plain);
                }
                throw new SecurityException($"Property '{propertyName}' was encrypted by XOD 2.0 under a password this database no longer records.");
            }

            // Stored before the property was marked [Crypto]: the text as it is.
            return stored;
        }

        /// <summary>
        /// SQL's reading of a stored AES256 value (PLAN-045 section 4, PLAN-060): opened only with a secret the caller
        /// gave this context (the password, or <c>DatabaseOptions.PropertyEncryptionKey</c>) and the keys they open,
        /// never with 2.0's built-in constant (ISS-050), which opens a value with no secret at all. False when nothing
        /// the caller gave opens it; SQL then reads NULL and warns (XQ008). A value stored before the property was
        /// marked [Crypto] is its text.
        /// </summary>
        internal bool TryDecryptWithSecret(string stored, out string plain)
        {
            plain = null;
            if (string.IsNullOrEmpty(stored))
            {
                plain = stored;
                return true;
            }

            if (stored.StartsWith(ValuePrefix, StringComparison.Ordinal))
            {
                byte[] key;
                lock (syncLock)
                {
                    key = dataKey ?? (CanOpen(DatabaseFile().Root()) ? dataKey : null);
                }
                byte[] opened = key == null ? null : Open(key, PayloadOrNull(stored, ValuePrefix), null);
                if (opened == null)
                    return false;
                plain = Encoding.UTF8.GetString(opened);
                return true;
            }

            if (stored.StartsWith(LegacyValuePrefix, StringComparison.Ordinal))
            {
                byte[] payload = PayloadOrNull(stored, LegacyValuePrefix);
                foreach (byte[] key in SecretLegacyCandidates())
                {
                    byte[] opened = Open(key, payload, null);
                    if (opened != null)
                    {
                        plain = Encoding.UTF8.GetString(opened);
                        return true;
                    }
                }
                return false;
            }

            plain = stored;
            return true;
        }

        // The 2.0 keys a secret the caller gave opens: the current password's, and those recorded under the data key
        // (which only a secret opens). Not the built-in constant's.
        private IEnumerable<byte[]> SecretLegacyCandidates()
        {
            string current = password();
            if (!string.IsNullOrEmpty(current))
                yield return CryptoHelper.DeriveKey(current, CryptoHelper.LegacyPropertySalt);

            List<byte[]> recorded;
            lock (syncLock)
            {
                if (legacyKeys != null)
                {
                    recorded = legacyKeys;
                }
                else
                {
                    XFile file = DatabaseFile();
                    recorded = file.Root().Elements(LegacyKeyElement).Any() && CanOpen(file.Root())
                        ? LegacyKeysUnlocked(file)
                        : new List<byte[]>();
                }
            }
            foreach (byte[] key in recorded)
                yield return key;
        }

        private static byte[] PayloadOrNull(string stored, string prefix)
        {
            try
            {
                return Convert.FromBase64String(stored.Substring(prefix.Length));
            }
            catch (FormatException)
            {
                return Array.Empty<byte>();
            }
        }

        // ---- password changes ----

        /// <summary>
        /// Before the password changes from <paramref name="oldPassword"/> to <paramref name="newPassword"/>
        /// (either may be null; both as the engine holds them): adds the protection the data key needs
        /// afterwards, keeping the current ones, and keeps the outgoing password's 2.0 key while values
        /// written under it exist. Throws, changing nothing, when values exist and nothing would protect
        /// their key afterwards. The caller then changes the files' password and calls
        /// <see cref="PasswordChanged"/>.
        /// </summary>
        internal void PasswordChanging(string oldPassword, string newPassword)
        {
            lock (syncLock)
            {
                dataKey = null;
                legacyKeys = null;
                // A key or a legacy key held in memory below is forgotten if anything here fails: it reached the file
                // only if the save at the end did.
                try
                {
                    XFile file = DatabaseFile();
                    XElement root = file.Root();
                    bool hasKey = root.Elements(KeyElement).Any();
                    bool passwordProtected = root.Elements(KeyElement).Any(e => ProtectorOf(e) == ProtectorPassword);

                    // The 2.0 key of the outgoing password: values 2.0 wrote under it must stay readable.
                    byte[] outgoingLegacy = string.IsNullOrEmpty(oldPassword) ? null : CryptoHelper.DeriveKey(oldPassword, CryptoHelper.LegacyPropertySalt);
                    // What protects a password-protected data key afterwards.
                    string secretAfter = newPassword ?? applicationKey;
                    const string NothingProtects =
                        "This database holds AES256 values whose key is protected by its password. Removing the password needs DatabaseOptions.PropertyEncryptionKey to protect that key instead.";

                    if (!hasKey)
                    {
                        // No data key: only 2.0's values can exist, and only a password change concerns them.
                        if (outgoingLegacy == null || !ScanValues().Legacy)
                            return;
                        if (secretAfter == null)
                            throw new SecurityException(NothingProtects);

                        // Protected for before and after: the key opens whichever password the files end up under.
                        dataKey = NewDataKeyUnlocked(file,
                            (ProtectorPassword, oldPassword),
                            (applicationKey != null ? ProtectorApplication : ProtectorPassword, applicationKey ?? newPassword));
                        legacyKeys = new List<byte[]>();
                    }
                    else if (passwordProtected)
                    {
                        if (secretAfter == null)
                        {
                            // Nothing would protect the key. Refused while any value needs it; otherwise the key
                            // and the 2.0 keys it holds go, since nothing uses them.
                            var (legacy, current) = ScanValues();
                            if (legacy || current)
                                throw new SecurityException(NothingProtects);
                            foreach (var elm in root.Elements(KeyElement).Concat(root.Elements(LegacyKeyElement)).ToList())
                                elm.Remove();
                            ioService.SaveNow(file);
                            return;
                        }
                        byte[] key = DataKeyUnlocked(file, create: false, propertyName: null);
                        root.Add(Protect(key, newPassword != null ? ProtectorPassword : ProtectorApplication, secretAfter));
                    }

                    if (outgoingLegacy != null)
                    {
                        // Kept under the data key. A context that can't open the data key (it is protected by an
                        // application key this context doesn't have) can't keep it: refused only while 2.0's
                        // values need it.
                        if (!CanOpen(root))
                        {
                            if (ScanValues().Legacy)
                                throw new SecurityException("This database holds AES256 values written by XOD 2.0 under its password. Changing the password needs DatabaseOptions.PropertyEncryptionKey, which protects this database's AES256 key, to keep them readable.");
                        }
                        else if (!LegacyKeysUnlocked(file).Any(k => k.AsSpan().SequenceEqual(outgoingLegacy)))
                        {
                            root.Add(new XElement(LegacyKeyElement, Convert.ToBase64String(Seal(dataKey, outgoingLegacy, LegacyKeyAad))));
                            legacyKeys = null;
                        }
                    }

                    ioService.SaveNow(file);
                }
                catch
                {
                    dataKey = null;
                    legacyKeys = null;
                    throw;
                }
            }
        }

        /// <summary>
        /// After the files' password changed (the engine holds the new one), still with the database to itself:
        /// removes the protections of the data key by a password that is no longer the database's (never the last
        /// one that opens), and makes the key if there is none and something protects it now (ISS-150).
        /// </summary>
        internal void PasswordChanged()
        {
            lock (syncLock)
            {
                XFile file = DatabaseFile();
                XElement root = file.Root();
                var protections = root.Elements(KeyElement).ToList();
                string current = password();
                var stale = protections
                    .Where(e => ProtectorOf(e) == ProtectorPassword && (current == null || TryOpen(e, out _) != OpenResult.Opened))
                    .ToList();
                if (stale.Count > 0 && stale.Count < protections.Count)
                {
                    foreach (var e in stale)
                        e.Remove();
                    ioService.SaveNow(file);
                }
            }
            MakeKeyIfNone();
        }

        // ---- the data key ----

        private enum OpenResult { Opened, NoSecret, Wrong }

        private static string ProtectorOf(XElement protection) => protection.Attribute("protector")?.Value;

        private OpenResult TryOpen(XElement protection, out byte[] key)
        {
            key = null;
            string secret = ProtectorOf(protection) == ProtectorApplication ? applicationKey : password();
            if (secret == null)
                return OpenResult.NoSecret;
            byte[] salt = Convert.FromBase64String(protection.Attribute("salt").Value);
            key = Open(DeriveProtectionKey(secret, salt), Convert.FromBase64String(protection.Value), KeyAad);
            return key != null ? OpenResult.Opened : OpenResult.Wrong;
        }

        private bool CanOpen(XElement root)
        {
            if (dataKey != null)
                return true;
            foreach (var protection in root.Elements(KeyElement))
            {
                if (TryOpen(protection, out byte[] key) == OpenResult.Opened)
                {
                    dataKey = key;
                    return true;
                }
            }
            return false;
        }

        /// <summary>The data key from <paramref name="file"/> (read afresh by the caller, the owner's guard: see
        /// the class), made if there is none and <paramref name="create"/>.</summary>
        private byte[] DataKeyUnlocked(XFile file, bool create, string propertyName)
        {
            if (dataKey != null)
                return dataKey;

            var protections = file.Root().Elements(KeyElement).ToList();
            if (protections.Count > 0)
            {
                var results = new List<(string Protector, OpenResult Result)>();
                foreach (var protection in protections)
                {
                    OpenResult result = TryOpen(protection, out byte[] key);
                    if (result == OpenResult.Opened)
                        return dataKey = key;
                    results.Add((ProtectorOf(protection), result));
                }

                if (results.All(r => r.Result == OpenResult.NoSecret))
                    throw new SecurityException(results.All(r => r.Protector == ProtectorApplication)
                        ? "This database's AES256 key is protected by DatabaseOptions.PropertyEncryptionKey, which this context doesn't have."
                        : "This database's AES256 key is protected by its password or an application key, neither of which this context has.");
                throw new SecurityException(results.Any(r => r.Protector == ProtectorApplication && r.Result == OpenResult.Wrong)
                    ? "DatabaseOptions.PropertyEncryptionKey isn't the key this database's AES256 values were protected with."
                    : "This database's AES256 key doesn't open with the database password.");
            }

            if (!create)
                throw new SecurityException($"Property '{propertyName}' holds a value encrypted with a key this database doesn't record.");

            if (applicationKey == null && password() == null)
                throw new SecurityException(
                    $"Property '{propertyName}' is [Crypto(AES256)], which needs a database password or DatabaseOptions.PropertyEncryptionKey: " +
                    "without one, anyone with XOD could decrypt the value.");

            // Only a database with no key gets here: 2.0.2's, one created with no secret and opened since with one, or
            // one whose key a password removal dropped (no value used it).
            return MakeDataKeyUnlocked();
        }

        /// <summary>
        /// Makes the data key if the database has none and this context has a secret to protect it with: run by the
        /// engine when it creates a database, and after a password change (<see cref="PasswordChanged"/>), each with the
        /// database to itself. So a database's key exists before two contexts can write its first value.
        /// </summary>
        internal void MakeKeyIfNone()
        {
            if (applicationKey == null && password() == null)
                return;
            lock (syncLock)
            {
                if (dataKey != null || DatabaseFile().Root().Elements(KeyElement).Any())
                    return;
                MakeDataKeyUnlocked();
            }
        }

        /// <summary>
        /// ISS-150: the data key is made with the database to this context, which every context with a hold has
        /// (PLAN-064); one without a hold is refused. The file is read afresh, and a key it already records is opened
        /// instead of a second made (when contexts could open side by side, each made one and the last save won).
        /// Written now, outside any batch or transaction: values written under a key that never reached the file
        /// could never be read; a stored key no value uses costs nothing.
        /// </summary>
        private byte[] MakeDataKeyUnlocked()
        {
            byte[] made = null;
            ioService.WithDatabaseToItself(() =>
            {
                XFile file = DatabaseFile();
                if (file.Root().Elements(KeyElement).Any())
                {
                    made = DataKeyUnlocked(file, create: false, propertyName: null);
                    return;
                }
                made = NewDataKeyUnlocked(file, (applicationKey != null ? ProtectorApplication : ProtectorPassword, applicationKey ?? password()));
                ioService.SaveNow(file);
            });
            return dataKey = made;
        }

        /// <summary>
        /// The one maker of a data key: a random key, protected in <paramref name="file"/> by each of
        /// <paramref name="protections"/> (the caller saves the file). Only with the database to this context
        /// (ISS-150); the owner refuses otherwise, whoever calls.
        /// </summary>
        private byte[] NewDataKeyUnlocked(XFile file, params (string Protector, string Secret)[] protections)
        {
            if (!ioService.HasDatabaseToItself)
                throw new InvalidOperationException("The AES256 key is made only with the database to this context (ISS-150).");
            byte[] key = RandomNumberGenerator.GetBytes(32);
            foreach (var (protector, secret) in protections)
                file.Root().Add(Protect(key, protector, secret));
            return key;
        }

        private static XElement Protect(byte[] key, string protector, string secret)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            return new XElement(KeyElement,
                new XAttribute("protector", protector),
                new XAttribute("salt", Convert.ToBase64String(salt)),
                Convert.ToBase64String(Seal(DeriveProtectionKey(secret, salt), key, KeyAad)));
        }

        // Not cached: each protection has its own random salt, and the secret mustn't sit in a cache.
        private static byte[] DeriveProtectionKey(string secret, byte[] salt) =>
            Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(secret), salt, Iterations, HashAlgorithmName.SHA256, 32);

        private IEnumerable<byte[]> LegacyCandidates()
        {
            string current = password();
            if (!string.IsNullOrEmpty(current))
                yield return CryptoHelper.DeriveKey(current, CryptoHelper.LegacyPropertySalt);

            List<byte[]> recorded;
            lock (syncLock)
            {
                recorded = legacyKeys ?? LegacyKeysUnlocked(DatabaseFile());
            }
            foreach (byte[] key in recorded)
                yield return key;

            yield return CryptoHelper.DeriveKey(CryptoHelper.LegacyPropertyPassphrase, CryptoHelper.LegacyPropertySalt);
        }

        private List<byte[]> LegacyKeysUnlocked(XFile file)
        {
            if (legacyKeys != null)
                return legacyKeys;

            var keys = new List<byte[]>();
            var elements = file.Root().Elements(LegacyKeyElement).ToList();
            if (elements.Count > 0)
            {
                byte[] key = DataKeyUnlocked(file, create: false, propertyName: null);
                foreach (var elm in elements)
                {
                    byte[] legacy = Open(key, Convert.FromBase64String(elm.Value), LegacyKeyAad);
                    if (legacy != null)
                        keys.Add(legacy);
                }
            }
            return legacyKeys = keys;
        }

        /// <summary>
        /// Whether any page holds an AES256 value, in 2.0's form or this one: every page this context sees,
        /// on disk or not written yet. Only a password change asks, and it rewrites every file anyway. Read
        /// from the documents, so every encoding and compression answers alike. A string that isn't
        /// [Crypto] but starts with the prefix counts too: the answer errs towards keeping a key.
        /// </summary>
        private (bool Legacy, bool Current) ScanValues()
        {
            bool legacy = false, current = false;
            foreach (string name in ioService.PageFileNames())
            {
                XElement pageRoot = ioService.OpenFileOrCreate(name, typeof(TablePage), openOnly: true)?.Root();
                if (pageRoot == null)
                    continue;
                foreach (var elm in pageRoot.Descendants())
                {
                    Note(elm.HasElements ? null : elm.Value);
                    foreach (var attr in elm.Attributes())
                        Note(attr.Value);
                    if (legacy && current)
                        return (true, true);
                }
            }
            return (legacy, current);

            void Note(string text)
            {
                if (text == null || !text.StartsWith("enc:aes256", StringComparison.Ordinal))
                    return;
                if (text.StartsWith(ValuePrefix, StringComparison.Ordinal)) current = true;
                else if (text.StartsWith(LegacyValuePrefix, StringComparison.Ordinal)) legacy = true;
            }
        }

        // The database file as it is on disk, as a document of this service's own: what it changes reaches the cache
        // only through a write that succeeded (IOService.SaveNow). A protection added to the cached document itself
        // stayed there when the write failed, and the next value found it and used the key unsaved (second review of
        // ISS-150).
        private XFile DatabaseFile()
        {
            XFile cached = ioService.Reopen(databaseFileName, typeof(Database)) ?? throw new DatabaseFileException();
            return new XFile(new XDocument(cached.Document), typeof(Database), cached.Path);
        }

        // ---- AES-256-GCM: nonce (12) | tag (16) | ciphertext ----

        private static byte[] Seal(byte[] key, byte[] plain, byte[] aad)
        {
            byte[] payload = new byte[12 + 16 + plain.Length];
            Span<byte> nonce = payload.AsSpan(0, 12);
            RandomNumberGenerator.Fill(nonce);
            using var gcm = new AesGcm(key, 16);
            gcm.Encrypt(nonce, plain, payload.AsSpan(28), payload.AsSpan(12, 16), aad);
            return payload;
        }

        /// <summary>The plaintext, or null when the key (or the data) is wrong.</summary>
        private static byte[] Open(byte[] key, byte[] payload, byte[] aad)
        {
            if (payload.Length < 28)
                return null;
            byte[] plain = new byte[payload.Length - 28];
            try
            {
                using var gcm = new AesGcm(key, 16);
                gcm.Decrypt(payload.AsSpan(0, 12), payload.AsSpan(28), payload.AsSpan(12, 16), plain, aad);
                return plain;
            }
            catch (CryptographicException)
            {
                return null;
            }
        }

        private static byte[] Payload(string stored, string prefix)
        {
            try
            {
                return Convert.FromBase64String(stored.Substring(prefix.Length));
            }
            catch (FormatException)
            {
                throw new SecurityException("Corrupted encrypted property payload.");
            }
        }
    }
}
