#!/usr/bin/env node
// decisions.mjs — keeps docs/DECISIONS.md honest.
//
// A decisions log rots in one specific way: an entry gets superseded, the entry that supersedes it says
// so, and the ORIGINAL says nothing. A reader arriving at D1 then follows advice that was overturned
// months ago, with no signal at all. That has already happened in this repo — D1 was superseded twice on
// the day it was written — so it is checked rather than trusted.
//
// Checks:
//   1. every decision has an id, a title and a date
//   2. ids are unique and in order
//   3. every D<n> referenced anywhere in the repo actually exists
//   4. if D_x supersedes/amends/scopes D_y, then D_y carries a forward pointer to D_x
//
// Exit code is the number of problems, so `doctor` can just add it up.

import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const { default: cfg } = await import('../project.config.mjs');

const text = readFileSync(join(root, cfg.decisions), 'utf8');
const problems = [];

// ── parse ────────────────────────────────────────────────────────────────────────────────────────
// A heading looks like:  ## D12 — title (2026-08-06) — supersedes D1, D7, D11
const headings = [...text.matchAll(/^## (D(\d+))\b(.*)$/gm)].map((m) => ({
  id: m[1],
  n: Number(m[2]),
  rest: m[3],
  at: m.index,
}));

if (headings.length === 0) problems.push(`${cfg.decisions}: no decisions found — expected headings like "## D1 — …"`);

const seen = new Set();
for (const [i, h] of headings.entries()) {
  if (seen.has(h.id)) problems.push(`${h.id}: duplicate id`);
  seen.add(h.id);

  if (i > 0 && h.n < headings[i - 1].n) problems.push(`${h.id}: out of order (follows ${headings[i - 1].id})`);
  if (!/\(\d{4}-\d{2}-\d{2}\)/.test(h.rest)) problems.push(`${h.id}: no date — every decision records when it was made`);
  if (!/—\s*\S/.test(h.rest)) problems.push(`${h.id}: no title after the id`);
}

// Body of each decision, for the forward-pointer check.
const bodyOf = (i) => text.slice(headings[i].at, headings[i + 1]?.at ?? text.length);

// ── supersession must point BOTH ways ────────────────────────────────────────────────────────────
const RELATION = /\b(supersedes|amends|scopes|replaces)\s+((?:D\d+(?:\s*,\s*)?)+)/gi;
for (const [i, h] of headings.entries()) {
  const body = bodyOf(i);
  for (const m of body.matchAll(RELATION)) {
    for (const target of m[2].match(/D\d+/g) ?? []) {
      const j = headings.findIndex((x) => x.id === target);
      if (j < 0) { problems.push(`${h.id}: ${m[1]} ${target}, which does not exist`); continue; }
      // The superseded entry must mention the one that overtook it. Anywhere in its body is enough —
      // this checks that a reader landing on it CAN find out, not how it is phrased.
      if (!new RegExp(`\\b${h.id}\\b`).test(bodyOf(j)))
        problems.push(
          `${target}: ${h.id} ${m[1]} it, but ${target} never mentions ${h.id} — ` +
          `a reader arriving at ${target} would not know it was overtaken`);
    }
  }
}

// ── every reference in the repo resolves ─────────────────────────────────────────────────────────
const walk = (dir) => readdirSync(dir).flatMap((e) => {
  if (cfg.ignore.includes(e)) return [];
  const p = join(dir, e);
  return statSync(p).isDirectory() ? walk(p) : [p];
});

const cited = new Map();
for (const file of walk(root).filter((f) => /\.(md|cs)$/.test(f))) {
  const body = readFileSync(file, 'utf8');
  // "D12" in prose. Requires the word boundary so it does not match D3D11 or a hex string.
  for (const m of body.matchAll(/\bD(\d{1,2})\b(?=[\s.,;:)\]]|$)/gm)) {
    // Only count it as a citation when DECISIONS is plausibly the subject — a bare "D1" in unrelated
    // prose would otherwise produce noise. Nearby mentions of decision/DECISIONS.md qualify it.
    const around = body.slice(Math.max(0, m.index - 120), m.index + 40);
    if (!/decision|DECISIONS/i.test(around)) continue;
    const id = `D${m[1]}`;
    if (!cited.has(id)) cited.set(id, file.slice(root.length + 1).replace(/\\/g, '/'));
  }
}

for (const [id, where] of cited)
  if (!seen.has(id)) problems.push(`${where}: cites ${id}, which is not in ${cfg.decisions}`);

// ── every in-page link resolves to a heading that exists ─────────────────────────────────────────
// The index at the top is hand-written anchors, which rot the moment a title is reworded — and a broken
// anchor is worse than no index, because it looks like navigation and silently goes nowhere.
const anchor = (heading) =>
  heading
    .toLowerCase()
    .replace(/[^\w\s-]/g, '')   // GitHub drops punctuation…
    .replace(/\s/g, '-');       // …and turns each remaining space into a hyphen

const anchors = new Set(
  [...text.matchAll(/^#{2,3} (.+)$/gm)].map((m) => anchor(m[1].trim())));

for (const m of text.matchAll(/\]\(#([^)]+)\)/g))
  if (!anchors.has(m[1]))
    problems.push(
      `${cfg.decisions}: links to #${m[1]}, which is not a heading in this file — ` +
      'a reworded title leaves the index pointing nowhere');

// ── report ───────────────────────────────────────────────────────────────────────────────────────
if (problems.length === 0) {
  console.log(`decisions: ${headings.length} decisions, all dated, referenced and cross-linked.`);
  process.exit(0);
}
console.error(`decisions: ${problems.length} problem(s)\n` + problems.map((p) => `  - ${p}`).join('\n'));
process.exit(problems.length);
