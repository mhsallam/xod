// An independent reader for XOD database files (PLAN-046), written from the spec, not from the library.
// It is the file-format half of the conformance set (PLAN-047):
//
//   node conformance/format/read.mjs <folder> [password]   print a database's content as JSON
//   node conformance/format/read.mjs --check               check every fixture against content*.json
//   node conformance/format/read.mjs --write               rewrite content*.json (after a deliberate change)
//   node conformance/format/read.mjs --check-fresh <root>  check the dataset as the library in the tree writes
//                                                          it (after: dotnet run ... -- data <root>)
//
// Node 18 or later, standard library only (node:crypto for AES-GCM and PBKDF2, node:zlib for Brotli).
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import zlib from 'node:zlib';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, '..', '..');
const td = new TextDecoder('utf-8', { fatal: true });

// ---- the encryption envelope (PLAN-046 section 3.2)
const ENC = Buffer.from('XODENC2\0', 'latin1');
function unwrapper(password) {
  const passphrase = password ? crypto.createHash('sha256').update(password, 'utf8').digest('hex') : null;
  const keys = new Map();
  return (buf) => {
    if (!(buf.length >= 8 && buf.subarray(0, 8).equals(ENC))) return buf;
    if (!passphrase) throw new Error('an encrypted file and no password');
    const salt = buf.subarray(8, 24), nonce = buf.subarray(24, 36), tag = buf.subarray(36, 52), ct = buf.subarray(52);
    const id = salt.toString('hex');
    if (!keys.has(id)) keys.set(id, crypto.pbkdf2Sync(Buffer.from(passphrase, 'utf8'), salt, 100000, 32, 'sha256'));
    const d = crypto.createDecipheriv('aes-256-gcm', keys.get(id), nonce);
    d.setAAD(ENC);
    d.setAuthTag(tag);
    return Buffer.concat([d.update(ct), d.final()]);
  };
}

// ---- documents: three encodings, one tree {name, attrs, children, text, cdata} (sections 3-4)
function detect(buf) {
  if (buf.length >= 5 && buf.toString('latin1', 0, 4) === 'XODB') return 'binary';
  for (const b of buf) {
    if ([0xef, 0xbb, 0xbf, 0x20, 0x09, 0x0d, 0x0a].includes(b)) continue;
    return b === 0x7b ? 'json' : 'xml';
  }
  return 'xml';
}
function parseDoc(buf) {
  const f = detect(buf);
  return { encoding: f, root: f === 'binary' ? parseBinary(buf) : f === 'json' ? parseJson(buf) : parseXml(td.decode(buf)) };
}
function readString(b, at) {
  let len = 0, shift = 0, x, p = at;
  do { x = b[p++]; len |= (x & 0x7f) << shift; shift += 7; } while (x & 0x80);
  return [td.decode(b.subarray(p, p + len)), p + len];
}
function parseBinary(buf) {
  if (buf[4] !== 2) throw new Error('XODB version ' + buf[4]);
  const b = zlib.brotliDecompressSync(buf.subarray(5));
  let p = 0;
  const i32 = () => { const v = b.readInt32LE(p); p += 4; return v; };
  const str = () => { const [s, np] = readString(b, p); p = np; return s; };
  const el = () => {
    const e = { name: str(), attrs: {}, children: [], text: null, cdata: false };
    for (let n = i32(); n > 0; n--) { const k = str(); e.attrs[k] = str(); }
    const count = i32();
    if (count > 0) for (let i = 0; i < count; i++) e.children.push(el());
    else {
      const kind = b[p++];
      if (kind === 1) e.text = str();
      else if (kind === 2) { e.text = str(); e.cdata = true; }
      else if (kind !== 0) throw new Error('binary: node kind ' + kind);
    }
    return e;
  };
  const root = b[p++] === 1 ? el() : null;
  if (p !== b.length) throw new Error(`binary: ${b.length - p} bytes after the root element`);
  return root;
}
function parseJson(buf) {
  const j = JSON.parse(td.decode(buf));
  const el = (o) => ({ name: o.$name, attrs: { ...(o.$attrs || {}) }, children: (o.$children || []).map(el), text: o.$value ?? null, cdata: !!o.$cdata });
  return j.$root ? el(j.$root) : null;
}
const ENTITIES = { lt: '<', gt: '>', amp: '&', quot: '"', apos: "'" };
function parseXml(s) {
  // Deliberately not a conforming XML parser: today's files hold raw control characters and character
  // references to them, and a raw CR must be kept, not normalised (PLAN-046 section 4.2).
  let p = 0;
  const ent = (t) => t.replace(/&(#x[0-9a-fA-F]+|#[0-9]+|lt|gt|amp|quot|apos);/g, (_, e) =>
    e[0] === '#' ? String.fromCodePoint(e[1] === 'x' ? parseInt(e.slice(2), 16) : parseInt(e.slice(1), 10)) : ENTITIES[e]);
  if (s.charCodeAt(0) === 0xfeff) p = 1;
  const ws = () => { while (p < s.length && /\s/.test(s[p])) p++; };
  ws();
  if (s.startsWith('<!DOCTYPE', p)) throw new Error('xml: a DTD');
  if (s.startsWith('<?xml', p)) p = s.indexOf('?>', p) + 2;
  ws();
  function el() {
    if (s[p] !== '<') throw new Error('xml: expected < at ' + p);
    p++;
    let m = /^[^\s/>]+/.exec(s.slice(p, p + 256));
    const e = { name: m[0], attrs: {}, children: [], text: null, cdata: false };
    p += m[0].length;
    for (;;) {
      ws();
      if (s.startsWith('/>', p)) { p += 2; return e; }
      if (s[p] === '>') { p++; break; }
      m = /^([^\s=]+)\s*=\s*(["'])/.exec(s.slice(p, p + 512));
      p += m[0].length;
      const end = s.indexOf(m[2], p);
      e.attrs[m[1]] = ent(s.slice(p, end));
      p = end + 1;
    }
    let text = '';
    for (;;) {
      if (s.startsWith('</', p)) {
        const end = s.indexOf('>', p);
        if (s.slice(p + 2, end).trim() !== e.name) throw new Error('xml: mismatched end tag for ' + e.name);
        p = end + 1;
        break;
      }
      if (s.startsWith('<![CDATA[', p)) { const end = s.indexOf(']]>', p); text += s.slice(p + 9, end); e.cdata = true; p = end + 3; continue; }
      if (s[p] === '<') { e.children.push(el()); continue; }
      const next = s.indexOf('<', p);
      text += ent(s.slice(p, next));
      p = next;
    }
    // Whitespace beside child elements is padding (deleted rows); a leaf keeps its text.
    if (e.children.length === 0 && (text.length > 0 || e.cdata)) e.text = text;
    else if (text.trim() !== '') throw new Error('xml: text beside elements in ' + e.name);
    return e;
  }
  return el();
}

// ---- index keys (section 8.3); the type comes from column-types.json until the manifest exists.
// Keys v1 (2.0: XIDX2 markers -2/-3, a TableIndex document) and keys v2 (the next release: marker -4, TableIndex2).
function normKey(type, text, version) {
  if (text == null) return null;
  return version === 2 ? keyV2(type, text) : keyV1(type, text);
}

const TWO63 = 2n ** 63n;
function keyV2(type, text) {
  if (type === 'int') return (BigInt(text) + TWO63).toString().padStart(20, '0');
  if (type?.enum) {
    // By value: a name, a number, or a [Flags] list such as "Read, Write".
    let v = 0n;
    for (const part of text.split(',').map((s) => s.trim())) {
      if (/^-?\d+$/.test(part)) v |= BigInt(part);
      else if (part in type.enum) v |= BigInt(type.enum[part]);
      else throw new Error('enum value ' + part + ' is not in column-types.json');
    }
    return (v + TWO63).toString().padStart(20, '0');
  }
  if (type === 'decimal') {
    // Zero is "1"; otherwise sign, exponent of the first digit, and the digits without trailing zeros.
    const neg = text.startsWith('-');
    const [ip, fp = ''] = (neg ? text.slice(1) : text).split('.');
    const all = ip + fp;
    const lead = all.search(/[1-9]/);
    if (lead < 0) return '1';
    const digits = all.slice(lead).replace(/0+$/, '');
    const e = ip.length - lead - 1;
    if (!neg) return '2' + String(e + 50).padStart(2, '0') + digits;
    return '0' + String(50 - e).padStart(2, '0') + [...digits].map((d) => 9 - Number(d)).join('') + '~';
  }
  if (type === 'double' || type === 'float') {
    // The IEEE bits of the double (a float widened), flipped so that unsigned order is numeric order.
    let d = type === 'float' ? Math.fround(Number(text)) : Number(text);
    if (Number.isNaN(d)) return null; // NaN has no key
    if (d === 0) d = 0; // -0 takes the key of 0
    const b = Buffer.alloc(8);
    b.writeDoubleBE(d);
    let u = b.readBigUInt64BE();
    u = u & TWO63 ? ~u & (2n ** 64n - 1n) : u | TWO63;
    return u.toString().padStart(20, '0');
  }
  if (type === 'text' || type === 'uuid') return text;
  throw new Error('no key rule for type ' + JSON.stringify(type));
}

function keyV1(type, text) {
  if (type?.enum) return text;
  if (type === 'int' || type === 'decimal') {
    // 10^18 + value, 19 integer digits, 8 decimals, rounded half away from zero.
    const neg = text.startsWith('-');
    const [ip, fp = ''] = (neg ? text.slice(1) : text).split('.');
    let scaled = BigInt(ip + fp.slice(0, 8).padEnd(8, '0'));
    if (fp.length > 8 && fp[8] >= '5') scaled += 1n;
    if (neg) scaled = -scaled;
    const shifted = 10n ** 26n + scaled;
    const digits = shifted.toString().padStart(27, '0');
    return digits.slice(0, 19) + '.' + digits.slice(19);
  }
  if (type === 'text' || type === 'enum' || type === 'uuid') return text;
  throw new Error('no key rule for type ' + type);
}

function readIndex(dir, file, unwrap) {
  const raw = unwrap(fs.readFileSync(path.join(dir, file)));
  const H = 'XIDX2:<TableIndex>\n';
  const idx = {};
  const add = (prop, k, c) => ((idx[prop] ??= {})[k] ??= new Set()).add(c);
  if (raw.toString('utf8', 0, H.length) !== H) {
    const doc = parseDoc(raw).root; // the document form, used with a password (section 8.2)
    const version = { TableIndex: 1, TableIndex2: 2 }[doc.name];
    if (!version) throw new Error(file + ': neither XIDX2 nor a TableIndex document');
    for (const pr of doc.children) {
      idx[pr.attrs.name] = {};
      for (const e of pr.children) for (const c of e.attrs.codes.split(',')) add(pr.attrs.name, e.attrs.key, c);
    }
    return { idx, form: 'document', version, zone: doc.attrs.zone ?? '' };
  }
  let p = H.length;
  const i32 = () => { const v = raw.readInt32LE(p); p += 4; return v; };
  const str = () => { const [s, np] = readString(raw, p); p = np; return s; };
  const marker = i32();
  if (marker !== -4 && marker !== -3 && marker !== -2) throw new Error(file + ': an unframed snapshot (not produced by 2.0 or later)');
  // -4 records what its keys assume about the writing machine: a hash of the local zone's rules for a
  // type with an indexed DateTime, else empty (section 8.1). A reader in another zone rebuilds.
  const zone = marker === -4 ? str() : '';
  for (let n = i32(); n > 0; n--) {
    const prop = str(), entries = i32(), min = str(), max = str(), len = i32(), end = p + len;
    idx[prop] ??= {};
    let seen = 0, lo = null, hi = null;
    while (p < end) {
      const k = str();
      for (let c = i32(); c > 0; c--) add(prop, k, str());
      seen++;
      if (lo === null || k < lo) lo = k;
      if (hi === null || k > hi) hi = k;
    }
    if (seen !== entries || (entries > 0 && (lo !== min || hi !== max))) throw new Error(`${file}/${prop}: block header disagrees with its entries`);
  }
  let lastEnd = p;
  while (p < raw.length) {
    const op = raw[p++], prop = str(), k = str(), code = str();
    if (op === 1) add(prop, k, code);
    else if (op === 2) {
      if (prop) idx[prop]?.[k]?.delete(code);
      else for (const b of Object.values(idx)) for (const s of Object.values(b)) s.delete(code);
    } else if (op === 3) lastEnd = p;
    else throw new Error(file + ': log op ' + op);
  }
  if (marker !== -2 && lastEnd !== raw.length) throw new Error(file + ': log not closed by an end-of-flush record');
  for (const b of Object.values(idx)) for (const [k, s] of Object.entries(b)) if (s.size === 0) delete b[k];
  return { idx, form: 'binary', version: marker === -4 ? 2 : 1, zone };
}

// ---- a whole database (sections 2, 5-7)
export function readDatabase(dir, password, columnTypes = {}, applicationKey = null) {
  const unwrap = unwrapper(password);
  const load = (file) => parseDoc(unwrap(fs.readFileSync(path.join(dir, file))));
  const child = (e, n) => e.children.find((c) => c.name === n);
  if (fs.existsSync(path.join(dir, 'transaction.xwal'))) throw new Error('a journal is present: recover first (section 9)');
  if (fs.readdirSync(dir).some((f) => f.endsWith('.xpwlog'))) throw new Error('a password change record is present: recover first (section 9)');

  const dbFile = fs.readdirSync(dir).find((f) => /\.xod[jb]?$/i.test(f));
  const dbDoc = load(dbFile);
  if (dbDoc.root.name !== 'Database') throw new Error('not a database file: ' + dbFile);

  const tables = {}, rowsByCode = new Map();
  for (const tf of fs.readdirSync(dir).filter((f) => f.endsWith('.xtab')).sort()) {
    const table = tf.slice(0, -5);
    const rows = [];
    for (const page of child(load(tf).root, 'Pages').children) {
      const pageCode = page.attrs.file.replace(/\.xpag$/, '');
      for (const row of child(load(page.attrs.file).root, 'Rows').children) {
        const entity = row.children[0];
        const r = { table, code: `${pageCode}.${row.attrs.code}`, element: entity.name, attrs: entity.attrs, fields: {} };
        for (const f of entity.children) r.fields[f.name] = f;
        rows.push(r);
        rowsByCode.set(r.code, r);
      }
    }
    tables[table] = rows;
  }

  const ref = (code, keys) => {
    const target = rowsByCode.get(code);
    if (!target) throw new Error('a dangling reference: ' + code);
    return { table: target.table, row: tables[target.table].indexOf(target), keys };
  };
  // AES-256 values carry a random nonce, so they differ between otherwise identical databases: the
  // content holds a placeholder (for either form), and the decrypted values are returned apart.
  const decrypt = propertyDecrypter(dbDoc.root, password, applicationKey);
  const secrets = [];
  const scalar = (t) => {
    if (t == null || !t.startsWith('enc:aes256')) return t;
    secrets.push(decrypt(t));
    return 'enc:aes256:<ciphertext>';
  };
  const value = (table, name, f) => {
    if (f.attrs.refType) {
      if (f.attrs.collType) return { [f.attrs.refType]: f.children.map((c) => ref(c.text, c.attrs)) };
      const keys = Object.fromEntries(Object.entries(f.attrs).filter(([k]) => k !== 'refType' && k !== 'hostProp'));
      return { [f.attrs.refType]: ref(f.text, keys), ...(f.attrs.hostProp ? { hostProp: f.attrs.hostProp } : {}) };
    }
    if (f.attrs.collType === 'dictionary') return { map: Object.fromEntries(f.children.map((c) => [c.attrs.key, c.text ?? ''])) };
    if (f.attrs.collType) return { list: f.children.map((c) => c.text) };
    // A scalar element: nil="true" is null, no text an empty string (section 7.2).
    if (f.attrs.nil === 'true') return null;
    return f.cdata ? { cdata: f.text } : canonical(columnTypes[table]?.[name], scalar(f.text ?? ''));
  };
  // Booleans are case-insensitive (section 7.5): the fast writer stores true, the document writer True.
  const canonical = (type, t) => (type === 'bool' && t != null ? t.toLowerCase() : t);
  const content = {};
  for (const [table, rows] of Object.entries(tables))
    content[table] = rows.map((r) => ({
      element: r.element,
      attrs: Object.fromEntries(Object.entries(r.attrs).map(([k, t]) => [k, canonical(columnTypes[table]?.[k], t)])),
      fields: Object.fromEntries(Object.entries(r.fields).map(([k, f]) => [k, value(table, k, f)])),
    }));

  // Every index must equal what the pages say.
  const indexProblems = [];
  const indexVersions = new Set();
  let indexEntries = 0;
  for (const xf of fs.readdirSync(dir).filter((f) => f.endsWith('.xidx')).sort()) {
    const table = xf.slice(0, -5);
    const { idx, version } = readIndex(dir, xf, unwrap);
    indexVersions.add(version);
    for (const [prop, got] of Object.entries(idx)) {
      const type = columnTypes[table]?.[prop];
      if (!type) { indexProblems.push(`${table}.${prop}: no type in column-types.json`); continue; }
      const want = {};
      for (const r of tables[table] ?? []) {
        // The value as a read gives it (section 7.2): an element marked nil="true" is null (no key), an
        // element with no text an empty string (the key ""), a missing element whatever the attribute says.
        const f = r.fields[prop];
        const k = normKey(type, f ? (f.attrs.nil === 'true' ? null : f.text ?? '') : r.attrs[prop], version);
        if (k != null) (want[k] ??= new Set()).add(r.code);
      }
      const canon = (o) => JSON.stringify(Object.keys(o).sort().map((k) => [k, [...o[k]].sort()]));
      if (canon(want) !== canon(got)) indexProblems.push(`${table}.${prop}: index ${canon(got)} but pages ${canon(want)}`);
      indexEntries += Object.keys(got).length;
    }
  }
  return { database: { ...dbDoc.root.attrs }, content, secrets: secrets.sort(), indexEntries, indexProblems, indexVersions: [...indexVersions].sort() };
}

// ---- [Crypto(AES256)] values (PLAN-046 section 7.6)
// The database's data key: the Database document's PropertyKey, AES-GCM (AAD "XOD property key v1")
// under PBKDF2-SHA256(secret, salt, 100000), the secret being the application's key or the password's
// SHA-256 hex. Values "enc:aes256k:" are AES-GCM under the data key; 2.0's "enc:aes256:" under
// PBKDF2(the password's SHA-256 hex, or a constant without one, the 2.0 salt), or a key the database
// kept in a LegacyPropertyKey when its password changed.
const LEGACY_SALT = Buffer.from('XOD++::PropertyEncryptionSalt::v2', 'utf8');
const LEGACY_CONSTANT = 'XOD++DefaultPropertySecretKey';
function gcmOpen(key, payload, aad) {
  const d = crypto.createDecipheriv('aes-256-gcm', key, payload.subarray(0, 12), { authTagLength: 16 });
  if (aad) d.setAAD(aad);
  d.setAuthTag(payload.subarray(12, 28));
  return Buffer.concat([d.update(payload.subarray(28)), d.final()]);
}
function propertyDecrypter(dbRoot, password, applicationKey) {
  const passphrase = password ? crypto.createHash('sha256').update(password, 'utf8').digest('hex') : null;
  const keyElm = dbRoot.children.find((c) => c.name === 'PropertyKey');
  let dataKey = null;
  if (keyElm) {
    const secret = keyElm.attrs.protector === 'application' ? applicationKey : passphrase;
    if (secret) {
      const kek = crypto.pbkdf2Sync(Buffer.from(secret, 'utf8'), Buffer.from(keyElm.attrs.salt, 'base64'), 100000, 32, 'sha256');
      dataKey = gcmOpen(kek, Buffer.from(keyElm.text, 'base64'), Buffer.from('XOD property key v1', 'ascii'));
    }
  }
  const legacy = [passphrase, LEGACY_CONSTANT].filter(Boolean)
    .map((p) => crypto.pbkdf2Sync(Buffer.from(p, 'utf8'), LEGACY_SALT, 100000, 32, 'sha256'));
  if (dataKey) for (const e of dbRoot.children.filter((c) => c.name === 'LegacyPropertyKey'))
    legacy.push(gcmOpen(dataKey, Buffer.from(e.text, 'base64'), Buffer.from('XOD legacy property key v1', 'ascii')));
  return (t) => {
    if (t.startsWith('enc:aes256k:')) {
      if (!dataKey) throw new Error('an AES256 value and no key for it');
      return gcmOpen(dataKey, Buffer.from(t.slice(12), 'base64')).toString('utf8');
    }
    const payload = Buffer.from(t.slice(11), 'base64');
    for (const k of legacy) {
      try { return gcmOpen(k, payload).toString('utf8'); } catch { /* the next key */ }
    }
    throw new Error('a 2.0 AES256 value no key opens');
  };
}

// ---- command line
// The committed fixtures (written by 2.0.1, so keys v1), and the same dataset as the library in the tree
// writes it (--check-fresh <root>: keys v2, and nothing older).
// The dataset's AES256 values (Dataset.cs), and the application key it is written with when there is no
// password (Dataset.PropertyKey; the 2.0 fixtures were written without one, under 2.0's built-in key).
const SECRETS = ['sk-billing-0001', 'sk-mail-0002'];
const DATASET_KEY = 'conformance-property-key';
const DATASET = (root) => [
  { name: 'xml', dir: path.join(root, 'xml'), propertyKey: DATASET_KEY, expect: 'content.json', types: 'dataset', secrets: SECRETS },
  { name: 'json', dir: path.join(root, 'json'), propertyKey: DATASET_KEY, expect: 'content.json', types: 'dataset', secrets: SECRETS },
  { name: 'binary', dir: path.join(root, 'binary'), propertyKey: DATASET_KEY, expect: 'content.json', types: 'dataset', secrets: SECRETS },
  { name: 'password', dir: path.join(root, 'password'), password: 'conformance-password', expect: 'content.json', types: 'dataset', secrets: SECRETS },
];
// The stored-values fixture (ISS-060): written by the library in the tree, so checked fresh too.
const STORED_VALUES = (root) => ({ name: 'stored-values', dir: path.join(root, 'stored-values'), expect: 'content-stored-values.json', types: 'stored-values' });
const FIXTURES = [
  ...DATASET(path.join(repo, 'conformance', 'data')),
  STORED_VALUES(path.join(repo, 'conformance', 'data')),
  { name: 'v1.2.5', dir: path.join(repo, 'tests', 'Xod.Tests', 'fixtures', 'v1.2.5-xml'), expect: 'content-v1.2.5.json', types: 'v1.2.5' },
];
const stable = (o) => JSON.stringify(o, null, 1) + '\n';

function main(args) {
  const types = JSON.parse(fs.readFileSync(path.join(here, 'column-types.json'), 'utf8'));
  if (args[0] === '--check' || args[0] === '--write' || args[0] === '--check-fresh') {
    const fresh = args[0] === '--check-fresh';
    if (fresh && !args[1]) { console.error('usage: read.mjs --check-fresh <root written by: -- data <root>>'); process.exit(2); }
    let failed = 0;
    for (const fx of fresh ? [...DATASET(path.resolve(args[1])), STORED_VALUES(path.resolve(args[1]))] : FIXTURES) {
      let line;
      try {
        const r = readDatabase(fx.dir, fx.password, types[fx.types], fx.propertyKey);
        if (fx.secrets && JSON.stringify(r.secrets) !== JSON.stringify(fx.secrets))
          r.indexProblems.push('AES256 values read as ' + JSON.stringify(r.secrets) + ', not ' + JSON.stringify(fx.secrets));
        if (fresh && r.indexVersions.join() !== '2') r.indexProblems.push('index keys v' + r.indexVersions.join(', v') + '; the library in the tree writes v2 only');
        const expectPath = path.join(here, fx.expect);
        if (args[0] === '--write' && fx.name !== 'json' && fx.name !== 'binary' && fx.name !== 'password') fs.writeFileSync(expectPath, stable(r.content));
        const same = fs.existsSync(expectPath) && fs.readFileSync(expectPath, 'utf8').replace(/\r\n/g, '\n') === stable(r.content);
        const rows = Object.values(r.content).reduce((n, t) => n + t.length, 0);
        if (!same || r.indexProblems.length) failed++;
        line = `${same && !r.indexProblems.length ? 'ok  ' : 'FAIL'} ${fx.name}: ${rows} rows ${same ? 'match' : 'DIFFER FROM'} ${fx.expect}; ${r.indexEntries} index keys${r.indexProblems.length ? '\n     ' + r.indexProblems.join('\n     ') : ' match the pages'}`;
      } catch (e) {
        failed++;
        line = `FAIL ${fx.name}: ${e.message}`;
      }
      console.log(line);
    }
    process.exit(failed ? 1 : 0);
  }
  if (!args[0]) { console.error('usage: read.mjs <folder> [password] [application key] | --check | --write | --check-fresh <root>'); process.exit(2); }
  const r = readDatabase(path.resolve(args[0]), args[1] || null, types.dataset, args[2] || null);
  console.log(stable(r));
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main(process.argv.slice(2));
