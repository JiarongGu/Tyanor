#!/usr/bin/env node
// check-sensitive.mjs — scan the working tree for credentials before they reach a commit.
//
// Tyanor holds cloud credentials by nature, so a test fixture or a debugging paste is one `git add` away
// from being permanent. Git history is effectively unerasable once pushed, which is why this runs BEFORE
// a commit rather than as a review step.
//
// It is deliberately noisy-but-cheap to silence: a real finding is rare, and a false one costs one
// `// tyanor:allow-secret` comment on the line. The opposite tuning — quiet and occasionally wrong — is
// the one that leaks.

import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const { default: cfg } = await import('../project.config.mjs');

/** Each pattern names a REAL credential shape. Generic "password" matching would be all noise. */
const PATTERNS = [
  { name: 'AWS access key id', re: /\b(?:AKIA|ASIA)[0-9A-Z]{16}\b/ },
  { name: 'AWS secret access key', re: /aws_?secret[_a-z]*\s*[=:]\s*['"][A-Za-z0-9/+=]{40}['"]/i },
  { name: 'private key block', re: /-----BEGIN (?:RSA |EC |OPENSSH |PGP )?PRIVATE KEY-----/ },
  { name: 'GitHub token', re: /\bgh[pousr]_[A-Za-z0-9]{36,}\b/ },
  { name: 'Slack token', re: /\bxox[abprs]-[A-Za-z0-9-]{10,}\b/ },
  { name: 'JSON Web Token', re: /\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b/ },
  { name: 'connection string password', re: /(?:password|pwd)\s*=\s*[^;"\s]{8,}/i },
  { name: 'bearer token', re: /\bBearer\s+[A-Za-z0-9._-]{24,}\b/ },
];

/** Extensions worth reading. A binary match would be a false positive nobody can act on. */
const TEXT = /\.(cs|csproj|slnx|json|md|mjs|js|ts|yml|yaml|props|targets|ps1|sh|txt|toml|xml|config)$/i;

const walk = (dir) => readdirSync(dir).flatMap((e) => {
  if (cfg.ignore.includes(e)) return [];
  const p = join(dir, e);
  return statSync(p).isDirectory() ? walk(p) : [p];
});

const findings = [];
for (const file of walk(root).filter((f) => TEXT.test(f))) {
  const rel = file.slice(root.length + 1).replace(/\\/g, '/');
  // This file necessarily contains the patterns it searches for.
  if (rel === 'devtools/scripts/check-sensitive.mjs') continue;

  const lines = readFileSync(file, 'utf8').split(/\r?\n/);
  lines.forEach((line, i) => {
    if (line.includes('tyanor:allow-secret')) return;
    for (const { name, re } of PATTERNS)
      if (re.test(line))
        findings.push(`${rel}:${i + 1}  ${name}\n      ${line.trim().slice(0, 100)}`);
  });
}

if (findings.length === 0) {
  console.log('sensitive: nothing that looks like a credential.');
  process.exit(0);
}
console.error(
  `sensitive: ${findings.length} possible credential(s)\n` +
  findings.map((f) => `  - ${f}`).join('\n') +
  '\n\n  If one is a false positive, add `tyanor:allow-secret` as a comment on that line.\n');
process.exit(findings.length);
