#!/usr/bin/env node
// rules.mjs — keeps .claude/rules honest.
//
// A rule that is not in the index is invisible to the workflow that reads it, which makes it a file
// nobody will ever apply. A rule linking to something that has moved is worse: it looks authoritative
// and sends the reader nowhere.
//
// Checks:
//   1. every rule file appears in RULES_INDEX.md
//   2. the index lists nothing that does not exist
//   3. every relative markdown link from a rule resolves on disk
//   4. every rule opens with a bold one-line statement of what it enforces

import { readFileSync, readdirSync, existsSync } from 'node:fs';
import { join, dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const { default: cfg } = await import('../project.config.mjs');

const dir = join(root, cfg.rulesDir);
const indexPath = join(root, cfg.rulesIndex);
const indexName = cfg.rulesIndex.split('/').pop();
const index = readFileSync(indexPath, 'utf8');
const problems = [];

const rules = readdirSync(dir).filter((f) => f.endsWith('.md') && f !== indexName && f !== 'TEMPLATE.md');

for (const rule of rules) {
  if (!index.includes(rule))
    problems.push(`${rule}: not listed in ${indexName} — the workflow reads the index, so this rule is invisible`);

  const body = readFileSync(join(dir, rule), 'utf8');

  // A rule should say what it enforces in one bold line near the top, so a reader can decide in seconds
  // whether it applies to them.
  const head = body.split(/\n## /)[0];
  if (!/\*\*[^*]{20,}\*\*/s.test(head))
    problems.push(`${rule}: no bold one-line statement of what it enforces`);

  for (const m of body.matchAll(/\]\((?!https?:)([^)#]+)(?:#[^)]*)?\)/g)) {
    const target = resolve(dir, m[1]);
    if (!existsSync(target)) problems.push(`${rule}: broken link → ${m[1]}`);
  }
}

for (const m of index.matchAll(/\]\((?!https?:)([^)#]+\.md)\)/g))
  if (!existsSync(resolve(dir, m[1]))) problems.push(`${indexName}: lists ${m[1]}, which does not exist`);

if (problems.length === 0) {
  console.log(`rules: ${rules.length} rules, all indexed and linked.`);
  process.exit(0);
}
console.error(`rules: ${problems.length} problem(s)\n` + problems.map((p) => `  - ${p}`).join('\n'));
process.exit(problems.length);
