#!/usr/bin/env node
// docs.mjs — keeps the documentation's own cross-references honest.
//
// The decisions log already had this check, for one file, because its hand-written index rotted the moment
// a title was reworded. Nothing checked the rest — so a rename could leave the README pointing at a moved
// page and the guide's table of contents pointing at nothing, and both would look like navigation right up
// until someone clicked.
//
// A broken link is worse than a missing one: it is authoritative-looking and it goes nowhere.
//
// Checks, over every .md in the repo:
//   1. every relative link resolves to a file on disk
//   2. every in-page anchor resolves to a heading in that same file
//   3. every doc the config calls REQUIRED is present
//
// Exit code is the number of problems, so `doctor` can just add it up.

import { readFileSync, readdirSync, statSync, existsSync } from 'node:fs';
import { join, dirname, resolve, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const { default: cfg } = await import('../project.config.mjs');

const problems = [];
let checked = 0;   // samples actually examined, so "0 problems" cannot mean "0 looked at"

const walk = (dir) => readdirSync(dir).flatMap((e) => {
  if (cfg.ignore.includes(e)) return [];
  const p = join(dir, e);
  return statSync(p).isDirectory() ? walk(p) : [p];
});

const show = (file) => relative(root, file).replace(/\\/g, '/');

// GitHub's anchor rule: lower-case, drop punctuation, spaces become hyphens.
const anchor = (heading) =>
  heading.toLowerCase().replace(/[^\w\s-]/g, '').replace(/\s/g, '-');

const docs = walk(root).filter((f) => f.endsWith('.md'));

for (const file of docs) {
  const body = readFileSync(file, 'utf8');
  const here = dirname(file);

  const headings = new Set(
    [...body.matchAll(/^#{1,6} (.+)$/gm)].map((m) => anchor(m[1].trim())));

  // `](target)` and `](target#fragment)` — skipping absolute URLs, which are somebody else's problem.
  for (const m of body.matchAll(/\]\((?!https?:|mailto:)([^)\s]*?)(?:#([^)\s]*))?\)/g)) {
    const [, target, fragment] = m;

    if (target) {
      const resolved = resolve(here, target);
      if (!existsSync(resolved)) {
        problems.push(`${show(file)}: links to ${target}, which is not on disk`);
        continue;
      }
      // An anchor into ANOTHER file is not checked: that file's headings are its own business, and a
      // cross-file fragment is far rarer than the two cases above.
      continue;
    }

    if (fragment && !headings.has(fragment))
      problems.push(
        `${show(file)}: links to #${fragment}, which is not a heading in this file — ` +
        'a reworded title leaves the link pointing nowhere');
  }
}

for (const required of cfg.requiredDocs ?? [])
  if (!existsSync(join(root, required)))
    problems.push(`${required} is missing, and something in this repository claims it exists`);

// ── the samples in the guide are the samples that compile ────────────────────────────────────────
// A fenced code block is the part of a document nothing can invalidate: rename a method and the prose
// keeps claiming the old one, which is wrong exactly when a newcomer is trusting it. So every C# sample in
// the guide must ALSO exist inside a project that builds, and this is what refuses one that does not.
//
// Not a copy — the same text. Indentation and blank lines are ignored, nothing else is.
for (const { doc, project } of cfg.compiledSamples ?? []) {
  const docPath = join(root, doc);
  const projectPath = join(root, project);
  if (!existsSync(docPath) || !existsSync(projectPath)) {
    problems.push(`${doc} or ${project} is missing, so its samples cannot be checked`);
    continue;
  }

  const normalise = (text) =>
    text.split('\n').map((l) => l.trim()).filter((l) => l.length > 0);

  const compiled = normalise(
    readdirSync(projectPath).filter((f) => f.endsWith('.cs'))
      .map((f) => readFileSync(join(projectPath, f), 'utf8')).join('\n')).join('\n');

  // `\r?\n`, and the count guard below, because of how this was nearly shipped broken: the first version
  // required a bare `\n`, so a guide saved with Windows line endings matched ZERO fences and the check
  // reported success having examined nothing. A check that passes when it cannot parse its input is worse
  // than no check, because it is believed.
  const fences = [...readFileSync(docPath, 'utf8').matchAll(/```csharp\r?\n([\s\S]*?)```/g)];

  if (fences.length === 0) {
    problems.push(`${doc}: no C# samples found at all — this document is configured as one that has them`);
    continue;
  }

  checked += fences.length;

  for (const [, fence] of fences) {
    const wanted = normalise(fence).join('\n');
    if (compiled.includes(wanted)) continue;

    problems.push(
      `${doc}: a C# sample starting "${normalise(fence)[0].slice(0, 60)}" is not in ${project} — ` +
      'every sample in this document is compiled, so one that is not there is one nothing checks');
  }
}

if (problems.length === 0) {
  console.log(
    `docs: ${docs.length} documents, every link and anchor resolves; ${checked} samples compile.`);
  process.exit(0);
}
console.error(`docs: ${problems.length} problem(s)\n` + problems.map((p) => `  - ${p}`).join('\n'));
process.exit(problems.length);
