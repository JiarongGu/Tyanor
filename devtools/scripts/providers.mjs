#!/usr/bin/env node
// providers.mjs — every shipped provider is held to the contract suites.
//
// The add-provider skill lists the tests that must exist "before the provider is trusted", and the first
// of them is the contract suites. Nothing checked that, and the gap was real: the AWS provider has two unit
// kinds and only ONE of them was ever run through `UnitDriverContract` — the other had no contract coverage
// at all, offline or gated. When it was finally run it failed four checks, all the same defect, and the
// worst of them meant a destroyed unit still reported itself deployed.
//
// A checklist nobody verifies is a checklist that describes the provider somebody wrote first.
//
// Checks, for every src/<providersDir>.* project:
//   1. a matching test project exists
//   2. it runs UnitDriverContract
//   3. it runs FailureClassifierContract
//
// It cannot check that every KIND is covered — that needs to know what a provider's kinds are, which is the
// provider's business. So the per-kind list lives in each suite as separate fixtures, and this checks only
// that the suites are reached at all.
//
// Exit code is the number of problems, so `doctor` can just add it up.

import { readFileSync, readdirSync, existsSync, statSync } from 'node:fs';
import { join, dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const { default: cfg } = await import('../project.config.mjs');

const problems = [];
const required = cfg.providerContracts ?? [];
let kindsChecked = 0;

/**
 * Whether these sources actually BUILD one of the suites, in either of the two shapes C# writes it.
 *
 * Looking for the bare name was the first attempt and it passed on a source file whose only mention was a
 * COMMENT saying "UnitDriverContract found it" — a check satisfiable by talking about the thing instead of
 * doing it. Requiring `new Suite(` fixed that and then missed `Suite Field = new(...)`, which is how both
 * shipped providers happen to write it. Both forms, then; a comment matches neither.
 */
const constructs = (sources, suite) =>
  new RegExp(`new\\s+${suite}\\s*\\(`).test(sources) ||
  new RegExp(`\\b${suite}\\s+[A-Za-z_]\\w*\\s*=\\s*new\\s*\\(`).test(sources);

const providers = existsSync(join(root, 'src'))
  ? readdirSync(join(root, 'src'))
      .filter((d) => cfg.providerPrefix && d.startsWith(cfg.providerPrefix))
      .filter((d) => statSync(join(root, 'src', d)).isDirectory())
  : [];

if (providers.length === 0) problems.push(`no provider projects found under src/${cfg.providerPrefix}*`);

for (const provider of providers) {
  const tests = join(root, 'tests', `${provider}.Tests`);
  if (!existsSync(tests)) {
    problems.push(`${provider}: no tests/${provider}.Tests — a provider nothing tests is a provider nothing trusts`);
    continue;
  }

  const sources = readdirSync(tests)
    .filter((f) => f.endsWith('.cs'))
    .map((f) => readFileSync(join(tests, f), 'utf8'))
    .join('\n');

  for (const suite of required)
    if (!constructs(sources, suite))
      problems.push(
        `${provider}: never constructs ${suite} — the add-provider skill lists it as required, and the ` +
        'one provider that skipped it turned out to be failing four of its checks');

  // ── and every KIND, not just the provider ──────────────────────────────────────────────────────
  // `UnitDriverContract` tests ONE unit, so a provider with two kinds needs two fixtures — and the kind
  // nobody wrote one for is the kind that was broken. That is not hypothetical: the AWS provider ran the
  // suite for its stacks and not its content, and the content driver was failing four checks.
  //
  // A kind is a `public const string XxxKind = "…"` beside the provider. Requiring the CONSTANT to appear
  // in a file that also builds the driver contract is what ties the two together — a kind named anywhere
  // else in the suite does not count.
  const kinds = [...readFileSync(optionsOf(provider), 'utf8')
    // `\w+Kind`, not `\w*Kind`: the latter also matched the option NAME — `public const string Kind =
    // "kind"` — which then "passed" because the literal Kind is a substring of DirectoryKind. A check that
    // counts six kinds where there are four is a check that is not reading what it thinks it is.
    .matchAll(/public\s+const\s+string\s+(\w+Kind)\s*=/g)].map((m) => m[1]);

  const contractFiles = readdirSync(tests)
    .filter((f) => f.endsWith('.cs'))
    .map((f) => readFileSync(join(tests, f), 'utf8'))
    .filter((body) => constructs(body, 'UnitDriverContract'));

  kindsChecked += kinds.length;

  for (const kind of kinds)
    if (!contractFiles.some((body) => body.includes(kind)))
      problems.push(
        `${provider}: the unit kind ${kind} is never named in a file that runs UnitDriverContract — ` +
        'one fixture per kind, because the suite tests one unit and the untested kind is the broken one');
}

/** A provider's options file, where its kinds are declared. Empty when it has none. */
function optionsOf(provider) {
  const dir = join(root, 'src', provider);
  const options = readdirSync(dir).find((f) => f.endsWith('Options.cs'));
  return options ? join(dir, options) : join(dir, '.no-options');
}

if (problems.length === 0) {
  console.log(
    `providers: ${providers.length} providers, ${kindsChecked} unit kinds, each held to ` +
    `${required.length} contract suites.`);
  process.exit(0);
}
console.error(`providers: ${problems.length} problem(s)\n` + problems.map((p) => `  - ${p}`).join('\n'));
process.exit(problems.length);
