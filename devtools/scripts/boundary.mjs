#!/usr/bin/env node
// boundary.mjs — nothing vendor-shaped crosses into the neutral core.
//
// This check exists because a compiler stopped doing it. The core used to be its own assembly with no
// reference to any provider, so a leak did not build. Merging the packages turned the boundary into a
// NAMESPACE, and `CLAUDE.md` has said ever since that "nothing but reading will catch it now" — which is
// exactly the shape of claim this repository keeps finding to be false: one guarded only by people
// remembering.
//
// The defect it guards against is the one that made the original code unportable: a "generic"
// DeploymentRequest carrying CdkOutDir and WebDir, so the neutral interface named an AWS tool and assumed a
// single-page app. No second provider could have implemented it, and nobody noticed, because there was only
// ever one.
//
// COMMENTS ARE EXEMPT AND THAT IS THE WHOLE DESIGN. The core is documented by naming what it refuses — "only
// the AWS provider knows this is a CloudFormation assembly" — so banning the words would ban the paragraphs
// that make the boundary teachable, and the check would be deleted within a month. What is banned is a
// vendor in the CODE: a type, a member, a constant, a string an operator could see.
//
// Exit code is the number of problems, so `doctor` can just add it up.

import { readFileSync, readdirSync, existsSync, statSync } from 'node:fs';
import { join, dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const { default: cfg } = await import('../project.config.mjs');

const problems = [];
const { dir, exclude = [] } = cfg.neutralSource ?? {};
const words = cfg.vendorWords ?? [];

if (!dir || words.length === 0) {
  console.log('boundary: nothing configured to check.');
  process.exit(0);
}

/**
 * Split a source file into the code a compiler sees and the string literals inside it, discarding comments.
 *
 * A scanner rather than three regexes, and the first attempt proved why: extracting strings by pattern from
 * the raw text found `"aws"` inside `/// <c>"aws"</c>` in eleven files and reported every one of them. A
 * doc comment full of quoted examples is exactly what this codebase is made of, so a check that cannot tell
 * a quote in prose from a literal in code is a check with an 11-to-0 false positive rate. Comments have to
 * be consumed in the same left-to-right pass as strings, because each can contain the other's opener.
 */
function split(text) {
  let code = '';
  let literals = '';
  let i = 0;

  const readString = (verbatim) => {
    let s = '';
    while (i < text.length) {
      if (verbatim && text[i] === '"' && text[i + 1] === '"') { s += '"'; i += 2; continue; }
      if (!verbatim && text[i] === '\\') { s += text[i + 1] ?? ''; i += 2; continue; }
      if (text[i] === '"') { i++; break; }
      s += text[i++];
    }
    literals += s + '\n';
    code += '""';
  };

  while (i < text.length) {
    const c = text[i];
    const next = text[i + 1];

    if (c === '/' && next === '/') { while (i < text.length && text[i] !== '\n') i++; code += ' '; continue; }
    if (c === '/' && next === '*') {
      i += 2;
      while (i < text.length && !(text[i] === '*' && text[i + 1] === '/')) i++;
      i += 2; code += ' '; continue;
    }
    if (c === '@' && next === '"') { i += 2; readString(true); continue; }
    if (c === '"') { i++; readString(false); continue; }
    if (c === "'") {                                   // a char literal, which can hold a quote or a slash
      i++;
      while (i < text.length) {
        if (text[i] === '\\') { i += 2; continue; }
        if (text[i] === "'") { i++; break; }
        i++;
      }
      code += "''"; continue;
    }

    code += c; i++;
  }

  return { code, literals };
}

/**
 * Whether a vendor word appears as a WORD, including camel-cased into a longer identifier.
 *
 * `\b` alone is not enough for C#, because the leak arrives camel-cased: `CdkOutDir` has no word boundary
 * before `Cdk`, and that identifier IS the historical defect this script exists for. So a boundary is also a
 * lower-or-digit → upper transition on the left, and anything that is not a lowercase letter or digit on the
 * right.
 *
 * The case-insensitive FLAG cannot be used for this, which cost a first version its headline case: with `i`
 * set, the `(?![a-z0-9])` guard on the right also rejects `O`, so `CdkOutDir` — the one example in the
 * comment above it — did not match. Planting that field is how it was found. The word is therefore made
 * case-insensitive letter by letter, and the guards stay case-SENSITIVE, because their whole job is to tell
 * an uppercase letter from a lowercase one.
 */
const names = (text, word) => {
  const anyCase = [...word].map((c) => (/[a-z]/.test(c) ? `[${c}${c.toUpperCase()}]` : c)).join('');
  return new RegExp(`(?:(?<![A-Za-z0-9])|(?<=[a-z0-9]))${anyCase}(?![a-z0-9])`).test(text);
};

const walk = (d) => readdirSync(d).flatMap((e) => {
  if (cfg.ignore.includes(e)) return [];
  const p = join(d, e);
  return statSync(p).isDirectory() ? walk(p) : [p];
});

const base = join(root, dir);
if (!existsSync(base)) {
  console.error(`boundary: ${dir} does not exist`);
  process.exit(1);
}

const files = walk(base)
  .filter((f) => f.endsWith('.cs'))
  .filter((f) => !exclude.some((x) => f.includes(x)));

for (const file of files) {
  const rel = file.slice(root.length + 1).replace(/\\/g, '/');
  const { code: bare, literals: quoted } = split(readFileSync(file, 'utf8'));

  for (const word of words) {
    if (names(bare, word))
      problems.push(
        `${rel}: the neutral core names '${word}' in code. A type or member there that knows a vendor is ` +
        'the CdkOutDir defect — move it into the provider, or express it through DeploymentRequest.Options');

    else if (names(quoted, word))
      problems.push(
        `${rel}: the neutral core has '${word}' in a string literal, which an operator can end up reading. ` +
        'Provider vocabulary is mapped by the provider, never passed through');
  }
}

if (problems.length === 0) {
  console.log(
    `boundary: ${files.length} files in ${dir} name none of ${words.length} vendors — ` +
    'in code or in a string. Comments may, and should.');
  process.exit(0);
}
console.error(`boundary: ${problems.length} problem(s)\n` + problems.map((p) => `  - ${p}`).join('\n'));
process.exit(problems.length);
