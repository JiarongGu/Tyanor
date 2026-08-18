#!/usr/bin/env node
// release-notes.mjs — the notes for one release, taken from the CHANGELOG section that names it.
//
//   node devtools/scripts/release-notes.mjs --version 0.1.0 [--link]
//
// Read from the changelog rather than generated from commit subjects, which is the other common choice and
// the wrong one HERE: this repository's changelog is written by hand and says why, while its commit
// subjects are one line each. A generator would produce a worse document than the one already sitting in
// the tree, and then two accounts of the same release could disagree.
//
// It invents nothing. If the section is missing it fails, because a release whose notes are "" is a release
// nobody can read — and the section's absence is exactly what `release` refuses to publish over.

import { readFileSync } from 'node:fs';
import { join, dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const { default: cfg } = await import('../project.config.mjs');

const args = process.argv.slice(2);
const valueOf = (name) => {
  const at = args.indexOf(`--${name}`);
  return at >= 0 ? args[at + 1] : undefined;
};

const version =
  valueOf('version') ??
  readFileSync(join(root, cfg.versionProps), 'utf8').match(/<VersionPrefix>([^<]+)<\/VersionPrefix>/)?.[1];

if (!version) {
  process.stderr.write('release-notes: no --version given and no <VersionPrefix> to fall back on\n');
  process.exit(1);
}

/**
 * The body under `## <version>`, up to the next `## ` heading.
 *
 * Matched on the version PREFIX rather than the whole line, so a heading that carries a date or a title —
 * `## 0.2.0 — 2026-09-01` — is still found. Anchored at the start of a line so a version mentioned in prose
 * cannot be mistaken for the section about it.
 */
const sectionFor = (changelog, wanted) => {
  const lines = changelog.split('\n');
  const start = lines.findIndex((l) => new RegExp(`^##\\s+v?${wanted.replace(/\./g, '\\.')}\\b`).test(l));
  if (start < 0) return null;

  const rest = lines.slice(start + 1);
  const end = rest.findIndex((l) => /^##\s/.test(l));
  return (end < 0 ? rest : rest.slice(0, end)).join('\n').trim();
};

const notes = sectionFor(readFileSync(join(root, 'CHANGELOG.md'), 'utf8'), version);

if (notes === null) {
  process.stderr.write(
    `release-notes: CHANGELOG.md has no "## ${version}" section. Write the release's notes before ` +
    'publishing it — generated-from-commits notes would be a second, worse account of the same release.\n');
  process.exit(1);
}

process.stdout.write(notes + '\n');

if (args.includes('--link')) {
  const url = cfg.repositoryUrl;
  process.stdout.write(
    `\n---\n\nPackages: ${(cfg.packages ?? [])
      .map((p) => p.split(/[\\/]/).pop().replace('.csproj', ''))
      .map((id) => `[${id}](https://www.nuget.org/packages/${id}/${version})`)
      .join(' · ')}\n` + (url ? `\nSource: ${url}\n` : ''));
}
