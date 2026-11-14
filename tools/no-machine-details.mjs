#!/usr/bin/env node
// No commit names the machine a run used (the owner's rule, 2026-11-14): no CPU model, core, thread or
// logical-processor count, affinity mask, RAM size, GPU, OS build or .NET patch level. Results say "one machine".
// The benchmark harness keeps the machine out of what it writes by construction (Machine.RecordPath writes it to
// a record git ignores); this check covers what people and agents write by hand, which is text, so it reads text.
// It is the one list of what counts: xod-site runs this file too, from the xod2 checkout beside it. It checks
// itself like any other file, so each pattern is written not to match its own text (R[y]zen, not the word);
// xod-site's contract test (scripts/machine-details.test.mjs) runs it on this file.
//
//   node tools/no-machine-details.mjs              the files staged for commit (the pre-commit hook, .githooks)
//   node tools/no-machine-details.mjs --all        every tracked file (a step of the gate)
//   node tools/no-machine-details.mjs --dir <dir>  every file under a folder, tracked or not (xod-site's deploy.sh
//                                                  runs it on dist/, which is what it publishes)
//
// Exits 1 and names each file and line that matches; exits 2 when there is nothing it can check (a missing or
// empty folder). Reword the line ("the performance cores", "every logical processor", "one machine"); don't add an
// exception here for one file. Images and other binary files are skipped: a screenshot is checked by looking.
import { execFileSync } from 'node:child_process';
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import path from 'node:path';

const PATTERNS = [
  [/\b\d+\s+logical\s+(processors?|cores?|CPUs?)\b/i, 'a logical-processor count'],
  [/\b\d+\s+(physical|performance|efficiency|P|E)[- ]cores?\b/i, 'a core count'],
  [/\b\d+\s+(performance|efficiency)[- ]core\s+threads?\b/i, 'a core-thread count'],
  [/\b\d+[- ]core\s+(CPU|processor|machine|laptop|desktop)\b/i, 'a core count'],
  [/\b\d+\s+(hardware\s+)?threads?\s+(CPU|processor|machine)\b/i, 'a thread count of the machine'],
  [/\b(affinity|mask)\b[^\n]{0,24}\b0x[0-9a-f]{2,}\b/i, 'an affinity mask'],
  [/\b(Core\s+)?i[3579]-\d{4,5}[A-Z]{0,3}\d?\b/, 'a CPU model'], // also names that end in a digit
  [/\bCore\s+Ultra\s+[3579]\s+\d{3}[A-Z]{0,2}\b/i, 'a CPU model'],
  [/\bCore\s*\(TM\)|\bIntel\s*\(R\)/i, 'a CPU model'],
  [/\b(R[y]zen|Threadr[i]pper|X[e]on|E[P]YC|Snapdr[a]gon)\b/i, 'a CPU model'],
  [/\bApple\s+M[1-9]\b/, 'a CPU model'],
  [/\b(RTX|GTX|Radeon\s+RX)\s?\d{3,4}\b/i, 'a GPU model'],
  [/\b\d+\s?GB\s+(of\s+)?(RAM|DDR\d|memory\s+machine)\b|\b\d+\s?GB\s+(machine|laptop|desktop)\b/i, 'a RAM size'],
  [/\bWindows\s+1[01]\s+(Pro|Home|Enterprise|Education)\b|\bbuild\s+(19|22|26)\d{3}\b|\b10\.0\.(19|22|26)\d{3}\b/i, 'an OS build'],
  [/\.NET\s+\d+\.\d+\.\d+\b/, 'a .NET patch level'],
];

const args = process.argv.slice(2);
const dirAt = args.indexOf('--dir');
const all = args.includes('--all');

function cannotCheck(message) {
  console.error('no-machine-details: ' + message);
  process.exit(2);
}

// Files under a folder, as paths relative to it with forward slashes.
function filesUnder(root, sub = '') {
  return readdirSync(path.join(root, sub), { withFileTypes: true }).flatMap((e) => {
    const name = sub ? sub + '/' + e.name : e.name;
    return e.isDirectory() ? filesUnder(root, name) : e.isFile() ? [name] : [];
  });
}

let names, read, scope;
if (dirAt >= 0) {
  const dir = args[dirAt + 1];
  if (!dir || dir.startsWith('--')) cannotCheck('--dir needs a folder');
  const root = path.resolve(dir);
  if (!existsSync(root) || !statSync(root).isDirectory()) cannotCheck(`--dir: ${dir} is not a folder`);
  names = filesUnder(root);
  if (!names.length) cannotCheck(`--dir: ${dir} holds no files, so there is nothing to check`);
  read = (name) => readFileSync(path.join(root, name));
  scope = 'files under ' + dir;
} else {
  const repo = execFileSync('git', ['rev-parse', '--show-toplevel'], { encoding: 'utf8' }).trim();
  names = execFileSync('git', all ? ['ls-files', '-z'] : ['diff', '--cached', '--name-only', '--diff-filter=ACMR', '-z'],
    { cwd: repo, encoding: 'utf8' }).split('\0').filter(Boolean);
  read = (name) => {
    try {
      // Staged: the blob that will be committed, not the working file.
      return all ? readFileSync(path.join(repo, name)) : execFileSync('git', ['show', ':' + name], { cwd: repo, maxBuffer: 1 << 28 });
    } catch { return null; } // deleted in the working tree but still tracked: nothing to commit from it
  };
  scope = all ? 'tracked files' : 'staged files';
}

const hits = [];
for (const name of names) {
  const buf = read(name);
  if (!buf || buf.includes(0)) continue; // binary (a page, an index, an image, a font)
  const lines = buf.toString('utf8').split(/\r?\n/);
  lines.forEach((line, i) => {
    for (const [re, what] of PATTERNS) {
      const m = re.exec(line);
      if (m) hits.push(`${name}:${i + 1}: ${what}: "${m[0]}"`);
    }
  });
}

if (hits.length) {
  console.error(`Machine details in ${scope === 'staged files' ? 'the commit' : scope} (the owner does not publish the machine):`);
  for (const h of hits) console.error('  ' + h);
  process.exit(1);
}
console.log(`ok   no machine details in ${names.length} ${scope}`);
