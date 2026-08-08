#!/usr/bin/env node
// doctor.mjs — the one command to run before committing.
//
// Everything that can be checked cheaply, in the order that fails fastest, with a single verdict. The
// point is that "is this repo healthy?" should not be a checklist someone has to remember, because the
// step people forget is the step that breaks.
//
// Beyond build + test it checks two architectural CLAIMS the README makes out loud, because a claim
// nobody verifies is one that quietly stops being true:
//   - Tyanor.Core and Tyanor.Engine take no package dependencies
//   - the version in the changelog headline matches the one that will actually ship

import { spawnSync } from 'node:child_process';
import { readFileSync, existsSync } from 'node:fs';
import { join, dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '../..');
const { default: cfg } = await import('../project.config.mjs');

const results = [];
const step = (name, fn) => {
  process.stdout.write(`  ${name} … `);
  const problems = fn();
  const ok = problems.length === 0;
  console.log(ok ? 'ok' : 'FAILED');
  if (!ok) problems.forEach((p) => console.log(`      ${p}`));
  results.push({ name, ok });
  return ok;
};

const sh = (file, args) =>
  spawnSync(file, args, { cwd: root, encoding: 'utf8', shell: process.platform === 'win32' });

console.log(`${cfg.name} doctor\n`);

// ── build + test first: everything else is cheap, but nothing else matters if these fail ─────────
step('build', () => {
  const r = sh('dotnet', ['build', cfg.solution, '-v', 'q', '--nologo']);
  if (r.status === 0) return [];
  // Only the distinct diagnostics — MSBuild repeats each once per project that referenced it.
  return [...new Set((r.stdout + r.stderr).split('\n').filter((l) => /error|warn/i.test(l)))].slice(0, 10);
});

step('test', () => {
  const r = sh('dotnet', ['test', cfg.solution, '-v', 'q', '--nologo']);
  // EVERY summary line — one per test project. Reporting only the first would hide a second project
  // failing behind a first that passed, which is exactly the shape of bug this whole script exists for.
  const summaries = (r.stdout + r.stderr).split('\n').filter((l) => /Passed!|Failed!/.test(l)).map((l) => l.trim());
  if (r.status === 0) {
    console.log();
    for (const s of summaries) process.stdout.write(`      ${s}\n`);
    process.stdout.write('      ');
    return [];
  }
  const failures = summaries.filter((s) => /Failed!/.test(s));
  return failures.length ? failures : summaries.length ? summaries : ['test run failed'];
});

// ── the architectural claims ─────────────────────────────────────────────────────────────────────
step('dependency-free core', () =>
  cfg.dependencyFree.flatMap((proj) => {
    const path = join(root, proj);
    if (!existsSync(path)) return [`${proj}: missing`];
    const refs = [...readFileSync(path, 'utf8').matchAll(/<PackageReference\s+Include="([^"]+)"/g)].map((m) => m[1]);
    // The README says these take none. If that changes deliberately, change the claim too.
    return refs.map((r) => `${proj} now depends on ${r} — the README says it depends on nothing`);
  }));

step('version is single-sourced', () => {
  const props = readFileSync(join(root, cfg.versionProps), 'utf8');
  const version = props.match(/<VersionPrefix>([^<]+)<\/VersionPrefix>/)?.[1];
  if (!version) return [`${cfg.versionProps}: no <VersionPrefix>`];
  const changelog = join(root, 'CHANGELOG.md');
  if (!existsSync(changelog)) return [];
  const head = readFileSync(changelog, 'utf8').split('\n').slice(0, 40).join('\n');
  // Unreleased is fine — it means nothing has been cut yet. A DIFFERENT released version is not.
  const released = head.match(/^## (\d+\.\d+\.\d+)/m)?.[1];
  return released && released !== version
    ? [`CHANGELOG heads at ${released} but ${cfg.versionProps} says ${version}`]
    : [];
});

// ── the knowledge layer ──────────────────────────────────────────────────────────────────────────
for (const [name, script] of [['decisions', 'decisions.mjs'], ['rules', 'rules.mjs'], ['sensitive', 'check-sensitive.mjs']])
  step(name, () => {
    const r = spawnSync('node', [join(here, script)], { encoding: 'utf8' });
    return r.status === 0 ? [] : (r.stdout + r.stderr).trim().split('\n').slice(0, 12);
  });

// ── verdict ──────────────────────────────────────────────────────────────────────────────────────
const failed = results.filter((r) => !r.ok);
console.log();
if (failed.length === 0) {
  console.log(`${cfg.name} is healthy — ${results.length}/${results.length} checks passed.`);
  process.exit(0);
}
console.error(`${failed.length} of ${results.length} checks FAILED: ${failed.map((f) => f.name).join(', ')}`);
process.exit(1);
