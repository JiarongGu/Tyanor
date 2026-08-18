#!/usr/bin/env node
// release.mjs — the preconditions for cutting a release, checked rather than remembered.
//
// `doctor` answers "is this repo healthy?". This answers the different question "is this repo SHIPPABLE
// right now?", which has an extra half nobody was checking:
//
//   - A dirty working tree. `dotnet pack` embeds the current COMMIT in every .nuspec, so packing with
//     uncommitted changes ships packages whose recorded source is not the source inside them. SourceLink
//     then sends a debugger to code that never built this binary. Found by looking inside a .nupkg for the
//     first time, which nobody had.
//   - Packages that build but are missing what makes them usable: the README a reader sees on nuget.org,
//     and the XML docs that are most of this library's value.
//
// It changes nothing and publishes nothing. It tells you whether the next command may be a publish.
//
// Exit code is the number of problems, so it composes like every other check here.

import { spawnSync } from 'node:child_process';
import { readFileSync, existsSync, mkdtempSync, rmSync, readdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname, resolve, basename } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '../..');
const { default: cfg } = await import('../project.config.mjs');

/**
 * The entry names inside a .nupkg, or null when it cannot be read as one.
 *
 * A nupkg is a zip, and a zip stores its entry names as plain bytes in the local file headers — so reading
 * them needs no unzip tool and no dependency, which matters for a script that has to work wherever someone
 * cuts a release. Read as latin1 so every byte maps to a character and nothing is mangled on the way.
 */
const LocalFileHeader = 0x04034b50;

const entriesOf = (nupkg) => {
  const buf = readFileSync(nupkg);
  if (buf.length < 30 || buf.readUInt32LE(0) !== LocalFileHeader) return null;

  const names = [];
  for (let i = 0; i + 30 <= buf.length; i++) {
    if (buf.readUInt32LE(i) !== LocalFileHeader) continue;

    const nameLength = buf.readUInt16LE(i + 26);
    if (nameLength === 0 || i + 30 + nameLength > buf.length) continue;
    names.push(buf.toString('utf8', i + 30, i + 30 + nameLength));
  }
  return names;
};

const problems = [];
const notes = [];

const sh = (file, args) =>
  spawnSync(file, args, { cwd: root, encoding: 'utf8', shell: process.platform === 'win32' });

// ── the version being cut ────────────────────────────────────────────────────────────────────────
const version = readFileSync(join(root, cfg.versionProps), 'utf8')
  .match(/<VersionPrefix>([^<]+)<\/VersionPrefix>/)?.[1];

if (!version) problems.push(`${cfg.versionProps}: no <VersionPrefix> to release`);

const changelog = readFileSync(join(root, 'CHANGELOG.md'), 'utf8');
const head = changelog.split('\n').find((l) => /^## /.test(l))?.replace(/^##\s*/, '').trim();

if (head && !head.startsWith(version))
  problems.push(`CHANGELOG heads at "${head}" but the version being cut is ${version}`);

if (head && /unreleased/i.test(head))
  problems.push('CHANGELOG still heads at "Unreleased" — a release needs a section that names its version');

// ── a clean tree, so the commit in the package is the code in the package ────────────────────────
// Files a release OWNS may be dirty; anything else may not. The workflow writes the new version into the
// working tree, packs, publishes, and commits the bookkeeping only after the push succeeds — so a failed
// release burns no version. What must not happen is a stray edit riding along into a published package.
const allowed = new Set(cfg.releaseFiles ?? []);

const dirty = sh('git', ['status', '--porcelain']).stdout
  .split('\n')
  .map((line) => line.trim())
  .filter((line) => line.length > 0)
  .map((line) => line.replace(/^\S+\s+/, '').replace(/^"|"$/g, ''));

const stray = dirty.filter((path) => !allowed.has(path));
const bookkeeping = dirty.filter((path) => allowed.has(path));

if (stray.length > 0)
  problems.push(
    `${stray.length} uncommitted path(s) a release does not own: ${stray.slice(0, 5).join(', ')}` +
    `${stray.length > 5 ? ', …' : ''}. \`dotnet pack\` stamps the CURRENT commit into every .nuspec, so ` +
    'packing now ships packages whose recorded source is not the source inside them. Commit first.');

if (bookkeeping.length > 0)
  notes.push(`${bookkeeping.join(' + ')} rewritten for this release, not yet committed`);

const commit = sh('git', ['rev-parse', 'HEAD']).stdout.trim();
notes.push(`version ${version}, commit ${commit.slice(0, 10)}`);

// ── the packages themselves ──────────────────────────────────────────────────────────────────────
const out = mkdtempSync(join(tmpdir(), 'tyanor-release-'));
try {
  const packed = sh('dotnet', ['pack', cfg.solution, '-c', 'Release', '-o', out, '--nologo', '-v', 'q']);
  if (packed.status !== 0) {
    problems.push('dotnet pack failed');
  } else {
    // Both extensions, kept apart. Filtering to `.nupkg` first and then asking that list for a `.snupkg`
    // is a question that can only be answered no — every package reported "no symbols" while six symbol
    // packages sat in the same directory.
    const built = readdirSync(out);
    const produced = built.filter((f) => f.endsWith('.nupkg'));
    const symbols = built.filter((f) => f.endsWith('.snupkg'));

    for (const project of cfg.packages) {
      const id = basename(project, '.csproj');
      const nupkg = produced.find((f) => f === `${id}.${version}.nupkg`);
      if (!nupkg) { problems.push(`${id}: no ${id}.${version}.nupkg was produced`); continue; }

      const inside = entriesOf(join(out, nupkg));
      if (inside === null) {
        // Loudly, rather than concluding "the files are missing". The first version of this shelled out to
        // `tar`, which cannot read a zip on every platform — so it reported every package as missing its
        // README and docs when all of them had both. A check that cannot read its input must say so.
        problems.push(`${id}: could not read the package to see what is in it`);
        continue;
      }

      if (!inside.some((n) => n.endsWith('README.md')))
        problems.push(`${id}: no README inside the package — that is the page nuget.org shows`);
      if (!inside.some((n) => n.endsWith(`${id}.xml`)))
        problems.push(`${id}: no XML documentation — most of this library's value is in the why`);
      if (!symbols.includes(`${id}.${version}.snupkg`)) problems.push(`${id}: no symbol package`);
    }

    notes.push(`${produced.length} packages + ${symbols.length} symbol packages build`);
  }
} finally {
  rmSync(out, { recursive: true, force: true });
}

// ── report ──────────────────────────────────────────────────────────────────────────────────────
if (problems.length === 0) {
  console.log(`release: ready — ${notes.join('; ')}.`);
  console.log('  Run `npm run doctor` too if you have not; this checks only what doctor does not.');
  process.exit(0);
}
console.error(
  `release: ${problems.length} thing(s) to do first\n` + problems.map((p) => `  - ${p}`).join('\n'));
process.exit(problems.length);
