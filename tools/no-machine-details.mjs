#!/usr/bin/env node
// No commit names the machine a run used (the owner's rule, 2026-11-14): no CPU model, core, thread or
// logical-processor count, affinity mask, RAM size, GPU, OS build or .NET patch level. Results say "one machine".
// The benchmark harness keeps the machine out of what it writes by construction (Machine.RecordPath writes it to
// a record git ignores); this check covers what people and agents write by hand, which is text, so it reads text.
//
//   node tools/no-machine-details.mjs            the files staged for commit (the pre-commit hook, .githooks)
//   node tools/no-machine-details.mjs --all      every tracked file (a step of the gate)
//
// Exits 1 and names each file and line that matches. Reword the line ("the performance cores", "every logical
// processor", "one machine"); don't add an exception here for one file.
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const PATTERNS = [
  [/\b\d+\s+logical\s+(processors?|cores?|CPUs?)\b/i, 'a logical-processor count'],
  [/\b\d+\s+(physical|performance|efficiency|P|E)[- ]cores?\b/i, 'a core count'],
  [/\b\d+\s+(performance|efficiency)[- ]core\s+threads?\b/i, 'a core-thread count'],
  [/\b\d+[- ]core\s+(CPU|processor|machine|laptop|desktop)\b/i, 'a core count'],
  [/\b\d+\s+(hardware\s+)?threads?\s+(CPU|processor|machine)\b/i, 'a thread count of the machine'],
  [/\b(affinity|mask)\b[^\n]{0,24}\b0x[0-9a-f]{2,}\b/i, 'an affinity mask'],
  [/\b(Core\s+)?i[3579]-\d{4,5}[A-Z]{0,3}\b/, 'a CPU model'],
  [/\bCore\s*\(TM\)|\bIntel\s*\(R\)/i, 'a CPU model'],
  [/\b(Ryzen|Threadripper|Xeon|EPYC|Snapdragon)\b/i, 'a CPU model'],
  [/\bApple\s+M[1-9]\b/, 'a CPU model'],
  [/\b(RTX|GTX|Radeon\s+RX)\s?\d{3,4}\b/i, 'a GPU model'],
  [/\b\d+\s?GB\s+(of\s+)?(RAM|DDR\d|memory\s+machine)\b|\b\d+\s?GB\s+(machine|laptop|desktop)\b/i, 'a RAM size'],
  [/\bWindows\s+1[01]\s+(Pro|Home|Enterprise|Education)\b|\bbuild\s+(19|22|26)\d{3}\b|\b10\.0\.(19|22|26)\d{3}\b/i, 'an OS build'],
  [/\.NET\s+\d+\.\d+\.\d+\b/, 'a .NET patch level'],
];

const repo = execFileSync('git', ['rev-parse', '--show-toplevel'], { encoding: 'utf8' }).trim();
const self = path.relative(repo, fileURLToPath(import.meta.url)).split(path.sep).join('/');
const all = process.argv.includes('--all');

// Staged: the blob that will be committed, not the working file.
const names = execFileSync('git', all ? ['ls-files', '-z'] : ['diff', '--cached', '--name-only', '--diff-filter=ACMR', '-z'],
  { cwd: repo, encoding: 'utf8' }).split('\0').filter(Boolean);

const hits = [];
for (const name of names) {
  if (name === self) continue; // it holds the patterns
  let buf;
  try {
    buf = all ? readFileSync(path.join(repo, name)) : execFileSync('git', ['show', ':' + name], { cwd: repo, maxBuffer: 1 << 28 });
  } catch { continue; } // deleted in the working tree but still tracked: nothing to commit from it
  if (buf.includes(0)) continue; // binary (a page, an index, an image)
  const lines = buf.toString('utf8').split(/\r?\n/);
  lines.forEach((line, i) => {
    for (const [re, what] of PATTERNS) {
      const m = re.exec(line);
      if (m) hits.push(`${name}:${i + 1}: ${what}: "${m[0]}"`);
    }
  });
}

if (hits.length) {
  console.error('Machine details in ' + (all ? 'tracked files' : 'the commit') + ' (the owner does not publish the machine):');
  for (const h of hits) console.error('  ' + h);
  process.exit(1);
}
console.log(`ok   no machine details in ${names.length} ${all ? 'tracked' : 'staged'} files`);
