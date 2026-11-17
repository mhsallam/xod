#!/usr/bin/env node
// The gate: every check a change must pass before it is merged, in one place. The CI workflow
// (.github/workflows/tests.yml) runs this script on each of its machines, and a local run is the same
// command, so the two can't drift apart (ISS-158).
//
//   node tools/gate.mjs                    every step, on .NET 8 and 9
//   node tools/gate.mjs --framework 9.0    the tests on one framework; the steps that run once (the file
//                                          format, the stress harness, the machine-details check) run
//                                          with 9.0
//
// Every build treats a warning as an error (Directory.Build.props). The steps all run, and the script exits
// non-zero if any failed.
import { spawnSync } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const args = process.argv.slice(2);
const at = args.indexOf('--framework');
const frameworks = at >= 0 ? [args[at + 1]] : ['8.0', '9.0'];
if (frameworks.some(f => f !== '8.0' && f !== '9.0')) {
  console.error('usage: node tools/gate.mjs [--framework 8.0|9.0]');
  process.exit(2);
}
const once = frameworks.includes('9.0');

const failed = [];
function step(name, command, commandArgs) {
  console.log(`== ${name}`);
  const started = Date.now();
  const r = spawnSync(command, commandArgs, { cwd: root, encoding: 'utf8', maxBuffer: 256 * 1024 * 1024 });
  const out = (r.stdout ?? '') + (r.stderr ?? '');
  const seconds = ((Date.now() - started) / 1000).toFixed(0);
  if (r.status === 0) {
    const summary = out.split(/\r?\n/).filter(l => /Passed!|^ok |no machine details/.test(l.trim()));
    for (const line of summary) console.log('   ' + line.trim());
    console.log(`   passed (${seconds} s)`);
  } else {
    console.log(out.split(/\r?\n/).slice(-60).join('\n'));
    console.log(`   FAILED (exit ${r.status ?? r.error?.message}, ${seconds} s)`);
    failed.push(name);
  }
}

const dotnet = (...a) => ['dotnet', a];
const fresh = mkdtempSync(join(tmpdir(), 'xod-gate-fresh-'));
try {
  step('build engine', ...dotnet('build', 'src/Xod/Xod.csproj', '-c', 'Release', '--nologo'));
  step('build CLI', ...dotnet('build', 'src/Xod.Cli/Xod.Cli.csproj', '-c', 'Release', '--nologo'));
  step('build sample', ...dotnet('build', 'samples/Xod.Playground/Xod.Playground.csproj', '-c', 'Release', '--nologo'));
  step('build crash writer', ...dotnet('build', 'tests/Xod.CrashWriter/Xod.CrashWriter.csproj', '-c', 'Release', '--nologo'));
  for (const f of frameworks) {
    step(`tests net${f}`, ...dotnet('test', 'tests/Xod.Tests/Xod.Tests.csproj', '-c', 'Release', '--nologo', '-f', `net${f}`));
    step(`conformance net${f}`, ...dotnet('test', 'conformance/Xod.Conformance/Xod.Conformance.csproj', '-c', 'Release', '--nologo', '-f', `net${f}`));
  }
  if (once) {
    // The file-format half (PLAN-047): an independent reader (Node, no packages) reads every fixture, then
    // the dataset the library in the tree writes today.
    step('format reader', 'node', ['conformance/format/read.mjs', '--check']);
    const conformance = ['run', '--project', 'conformance/Xod.Conformance/Xod.Conformance.csproj', '-c', 'Release', '-f', 'net9.0', '--'];
    step('fresh data', 'dotnet', [...conformance, 'data', fresh]);
    step('fresh stored values', 'dotnet', [...conformance, 'stored-values', fresh]);
    step('fresh reader', 'node', ['conformance/format/read.mjs', '--check-fresh', fresh]);
    // The stress harness writes the files the site publishes; these pin what those files must say.
    step('stress harness tests', ...dotnet('test', 'benchmarks/XodBench.Tests/XodBench.Tests.csproj', '-c', 'Release', '--nologo'));
    step('machine details', 'node', ['tools/no-machine-details.mjs', '--all']);
  }
} finally {
  rmSync(fresh, { recursive: true, force: true });
}

if (failed.length) {
  console.log(`\nThe gate failed: ${failed.join(', ')}`);
  process.exit(1);
}
console.log('\nThe gate passed.');
