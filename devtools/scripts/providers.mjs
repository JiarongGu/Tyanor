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
//   2. it runs UnitDriverContract, UNGATED
//   3. it runs FailureClassifierContract, UNGATED
//   4. every declared unit KIND is named in an ungated file that runs UnitDriverContract
//
// This header used to say the opposite of (4) — "it cannot check that every KIND is covered" — describing a
// version of the file that had already been replaced. A comment contradicting the code it heads is bad
// anywhere and worse here, since a reader deciding whether to trust the check reads the header first.
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

/**
 * Whether a file only runs when an environment variable says so — in which case, for an ordinary run, it
 * runs never.
 *
 * This is the distinction that made the whole check overstate itself. `UnitDriverContract` was constructed
 * for the AWS stack driver in exactly one place: inside the live deployment test, behind `TYANOR_LIVE_AWS`,
 * which returns before doing anything when the variable is unset. Nothing has ever reached AWS from this
 * repository, so that suite had run against that driver ZERO times — while this script cheerfully reported
 * "4 unit kinds, each held to 2 contract suites", because it could not tell RUNNING a suite from NAMING one
 * in a file that returns early.
 *
 * A gated file is not coverage; it is a promise about a run nobody has done. Deliberately blunt: a file that
 * reads an environment variable at all stops counting, and the fix is to put the ungated suite in its own
 * file — which is what it wanted to be anyway.
 */
const gated = (body) => /GetEnvironmentVariable/.test(body);

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

  // Only the UNGATED files count. One that returns early on an environment variable proves nothing about
  // an ordinary run, and reading it as coverage is what let a whole driver go unchecked.
  const bodies = readdirSync(tests)
    .filter((f) => f.endsWith('.cs'))
    .map((f) => readFileSync(join(tests, f), 'utf8'));

  const sources = bodies.filter((body) => !gated(body)).join('\n');

  for (const suite of required)
    if (!constructs(sources, suite))
      problems.push(
        bodies.some((body) => gated(body) && constructs(body, suite))
          ? `${provider}: ${suite} is only constructed inside an environment-gated file, so an ordinary ` +
            'run never reaches it. That is a promise about a run nobody has done, not coverage — move the ' +
            'ungated suite into its own file'
          : `${provider}: never constructs ${suite} — the add-provider skill lists it as required, and the ` +
            'one provider that skipped it turned out to be failing four of its checks');

  // ── and every KIND, not just the provider ──────────────────────────────────────────────────────
  // `UnitDriverContract` tests ONE unit, so a provider with two kinds needs two fixtures — and the kind
  // nobody wrote one for is the kind that was broken. That is not hypothetical: the AWS provider ran the
  // suite for its stacks and not its content, and the content driver was failing four checks.
  //
  // A kind is a `public const string XxxKind = "…"` beside the provider. Requiring the CONSTANT to appear
  // in a file that also builds the driver contract is what ties the two together — a kind named anywhere
  // else in the suite does not count.
  const kinds = kindsOf(provider);

  const contractFiles = bodies
    .filter((body) => !gated(body))
    .filter((body) => constructs(body, 'UnitDriverContract'));

  kindsChecked += kinds.length;

  for (const kind of kinds)
    if (!contractFiles.some((body) => body.includes(kind)))
      problems.push(
        `${provider}: the unit kind ${kind} is never named in an UNGATED file that runs UnitDriverContract ` +
        '— one fixture per kind, because the suite tests one unit and the untested kind is the broken one');
}

/**
 * A provider's declared unit kinds, from the options file beside it.
 *
 * NO options file means NO declared kinds, and that is a legitimate provider rather than a mistake: one
 * whose units are all the same thing — every CloudFormation unit is a stack — implements `IUnitDriver`
 * directly and never declares a kind at all. Such a provider is still held to the suites above; there is
 * simply nothing per-kind to check.
 *
 * This used to return a path to a file called `.no-options` for that case, which `readFileSync` then threw
 * on — so the shape the framework explicitly supports crashed the check with an ENOENT stack trace instead
 * of a sentence. A gate that dies on a legal input is a gate people learn to skip.
 */
function kindsOf(provider) {
  const dir = join(root, 'src', provider);
  const options = readdirSync(dir).find((f) => f.endsWith('Options.cs'));
  if (!options) return [];

  // `\w+Kind`, not `\w*Kind`: the latter also matched the option NAME — `public const string Kind =
  // "kind"` — which then "passed" because the literal Kind is a substring of DirectoryKind. A check that
  // counts six kinds where there are four is a check that is not reading what it thinks it is.
  return [...readFileSync(join(dir, options), 'utf8')
    .matchAll(/public\s+const\s+string\s+(\w+Kind)\s*=/g)].map((m) => m[1]);
}

if (problems.length === 0) {
  console.log(
    `providers: ${providers.length} providers, ${kindsChecked} unit kinds, each held to ` +
    `${required.length} contract suites.`);
  process.exit(0);
}
console.error(`providers: ${problems.length} problem(s)\n` + problems.map((p) => `  - ${p}`).join('\n'));
process.exit(problems.length);
