#!/usr/bin/env node
// dev.mjs — THE entry point for Tyanor's devtools. One command dispatches to every tool:
//
//   node devtools/dev.mjs <command> [...args]        (or: npm run dev -- <command> …)
//
//   doctor [--fix]     build + test + every check below. The one command to run before committing.
//   test               run the test suite
//   build              build the solution
//   release            are we shippable? clean tree, version, packages that contain what they should
//   consumer           pack, then use the packages as a stranger would — outside the repo, public surface only
//   notes              this release's notes, from the CHANGELOG section that names it
//   pack [outDir]      produce the NuGet packages locally
//   decisions          validate docs/DECISIONS.md — references resolve, supersessions point forward
//   rules              validate .claude/rules — every rule listed in the index, every link resolves
//   boundary           the neutral core names no vendor, in code or in a string
//   sensitive          scan the working tree for credentials before they reach a commit
//
// stdout/stderr pass straight through, and the exit code is the tool's. The toolkit is meant to
// self-enhance: add a script, add a row to TOOLS below and to devtools/README.md.
//
// Nothing here names Tyanor — project values live in project.config.mjs.

import { spawnSync } from 'node:child_process';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));

const TOOLS = {
  doctor: 'doctor.mjs',
  decisions: 'decisions.mjs',
  rules: 'rules.mjs',
  docs: 'docs.mjs',
  providers: 'providers.mjs',
  boundary: 'boundary.mjs',
  release: 'release.mjs',
  consumer: 'consumer.mjs',
  notes: 'release-notes.mjs',
  sensitive: 'check-sensitive.mjs',
};

/** Commands that are a thin wrapper over dotnet — kept here rather than as one-line scripts. */
const DOTNET = {
  build: (cfg) => ['build', cfg.solution, '-v', 'q', '--nologo'],
  test: (cfg) => ['test', cfg.solution, '-v', 'q', '--nologo'],
  pack: (cfg, args) => ['pack', cfg.solution, '-c', 'Release', '-o', args[0] ?? 'artifacts', '--nologo'],
};

const [cmd, ...rest] = process.argv.slice(2);
const { default: cfg } = await import('./project.config.mjs');

if (!cmd || ['help', '--help', '-h'].includes(cmd)) {
  process.stdout.write(
    `${cfg.name} devtools — node devtools/dev.mjs <command> [...args]\n\n` +
      [...Object.keys(DOTNET), ...Object.keys(TOOLS)].sort().map((c) => `  ${c}`).join('\n') +
      '\n\nSee devtools/README.md for what each one checks and why.\n',
  );
  process.exit(cmd ? 0 : 1);
}

const run = (file, args, opts = {}) =>
  spawnSync(file, args, { stdio: 'inherit', shell: process.platform === 'win32', ...opts }).status ?? 1;

if (DOTNET[cmd]) process.exit(run('dotnet', DOTNET[cmd](cfg, rest), { cwd: resolve(here, '..') }));

if (TOOLS[cmd]) process.exit(run('node', [resolve(here, 'scripts', TOOLS[cmd]), ...rest]));

process.stderr.write(`dev: unknown command '${cmd}'. Try: node devtools/dev.mjs help\n`);
process.exit(1);
