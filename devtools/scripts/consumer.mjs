#!/usr/bin/env node
// consumer.mjs — build the packages, then use them the way a stranger would.
//
// `doctor` asks "is this repo healthy?" and `release` asks "is it shippable?". Both compile against the
// SOURCE TREE, and there are defects that only exist across an assembly boundary — so both can be green
// while the thing a consumer actually installs is wrong.
//
// This packs, creates a throwaway project OUTSIDE the repository, restores the packed .nupkg into it, and
// runs devtools/consumer/Program.cs against nothing but the public surface.
//
// It was a ritual before it was a script: 0.1.0, 0.1.1 and 0.2.0 were each checked by hand this way, after
// publishing. 0.2.0's check found a real defect — a fixture whose own answer was silently ignored, because
// C# fixes an interface mapping at the class naming the interface. Nothing in the repository could have
// found it. But it ran AFTER the publish, which made a fixable thing a shipped thing, and the packages
// exist at pack time. So it runs before now.
//
// Two isolations matter, and without either the check passes while testing the wrong bits:
//
//   - the local folder is the ONLY source. Otherwise a restore falls back to nuget.org and quietly tests
//     the version already published — the exact thing this is meant to get ahead of.
//   - a private packages folder, not the global cache. The version being cut may already be in ~/.nuget
//     from an earlier run or an actual install, and the cache would serve that instead.
//
// Exit code is the number of problems, so it composes like every other check here.

import { spawnSync } from 'node:child_process';
import { readFileSync, writeFileSync, mkdtempSync, mkdirSync, cpSync, rmSync, readdirSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname, resolve, basename } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '../..');
const { default: cfg } = await import('../project.config.mjs');

const sh = (file, args, opts = {}) =>
  spawnSync(file, args, { cwd: root, encoding: 'utf8', shell: process.platform === 'win32', ...opts });

const problems = [];
const say = (m) => process.stdout.write(`${m}\n`);

const consumer = cfg.consumer;
if (!consumer) {
  say('consumer: not configured — set `consumer` in project.config.mjs');
  process.exit(1);
}

const version = readFileSync(join(root, cfg.versionProps), 'utf8')
  .match(/<VersionPrefix>([^<]+)<\/VersionPrefix>/)?.[1];
if (!version) {
  say(`consumer: no <VersionPrefix> in ${cfg.versionProps}`);
  process.exit(1);
}

const source = join(root, consumer.source);
if (!existsSync(source)) {
  say(`consumer: ${consumer.source} does not exist — it holds the program a stranger runs`);
  process.exit(1);
}

// ── pack ─────────────────────────────────────────────────────────────────────────────────────────
// A caller that has ALREADY packed hands the folder over rather than making it happen twice — `release`
// packs to inspect what is inside the .nupkg, and packing again would double the slowest step for nothing.
const given = process.argv[2];
const feed = given ?? mkdtempSync(join(tmpdir(), 'consumer-feed-'));
const work = mkdtempSync(join(tmpdir(), 'consumer-app-'));

try {
  const packed = given ? { status: 0 } : sh('dotnet', ['pack', cfg.solution, '-c', 'Release', '-o', feed, '--nologo', '-v', 'q']);
  if (packed.status !== 0) {
    problems.push('dotnet pack failed — nothing to hand a consumer');
  } else {
    const ids = consumer.packages.map((p) => basename(p, '.csproj'));
    for (const id of ids)
      if (!readdirSync(feed).includes(`${id}.${version}.nupkg`))
        problems.push(`${id}.${version}.nupkg was not produced, so a consumer cannot reference it`);
  }

  if (problems.length === 0) {
    const ids = consumer.packages.map((p) => basename(p, '.csproj'));

    // ── a project a stranger could have written ──────────────────────────────────────────────────
    cpSync(source, work, { recursive: true });
    mkdirSync(join(work, 'packages'), { recursive: true });

    writeFileSync(join(work, 'app.csproj'),
      `<Project Sdk="Microsoft.NET.Sdk">\n` +
      `  <PropertyGroup>\n` +
      `    <OutputType>Exe</OutputType>\n` +
      `    <TargetFramework>${consumer.targetFramework}</TargetFramework>\n` +
      `    <Nullable>enable</Nullable>\n` +
      `    <ImplicitUsings>enable</ImplicitUsings>\n` +
      `    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>\n` +
      `    <RestorePackagesPath>packages</RestorePackagesPath>\n` +
      `  </PropertyGroup>\n` +
      `  <ItemGroup>\n` +
      ids.map((id) => `    <PackageReference Include="${id}" Version="${version}" />\n`).join('') +
      `  </ItemGroup>\n` +
      `</Project>\n`);

    // MAPPED, not cleared. The obvious version of this — the local folder as the only source — fails
    // honestly: the library's own dependency is on nuget.org, so nothing restores. The obvious fix after
    // that, adding nuget.org back, is the dangerous one: the version being cut may ALREADY be published,
    // and a restore that quietly preferred it would test the previous release while reporting on this one.
    //
    // Source mapping says it exactly: these ids come from the packed folder and may not come from
    // anywhere else; everything else comes from upstream.
    writeFileSync(join(work, 'nuget.config'),
      `<?xml version="1.0" encoding="utf-8"?>\n` +
      `<configuration>\n` +
      `  <packageSources>\n` +
      `    <clear />\n` +
      `    <add key="packed" value="${feed}" />\n` +
      `    <add key="upstream" value="${consumer.upstream}" />\n` +
      `  </packageSources>\n` +
      `  <packageSourceMapping>\n` +
      `    <packageSource key="packed">\n` +
      ids.map((id) => `      <package pattern="${id}" />\n`).join('') +
      `    </packageSource>\n` +
      `    <packageSource key="upstream">\n` +
      `      <package pattern="*" />\n` +
      `    </packageSource>\n` +
      `  </packageSourceMapping>\n` +
      `</configuration>\n`);

    // Built and run as separate steps, because they fail for opposite reasons and a caller needs to know
    // which. "The public surface a consumer compiles against changed" and "it compiles and then misbehaves"
    // send you to different places; one exit code cannot say which happened.
    const built = sh('dotnet', ['build', 'app.csproj', '-v', 'q', '--nologo'], { cwd: work });
    if (built.status !== 0) {
      const errors = `${built.stdout ?? ''}${built.stderr ?? ''}`
        .split(/\r?\n/).filter((l) => /error/i.test(l)).slice(0, 5);
      say(errors.map((l) => `  ${l.trim()}`).join('\n'));
      problems.push('a consumer project does not COMPILE against the packed package — the public surface ' +
        'they build on is not what this repository builds against');
    } else {
      const run = sh('dotnet', ['run', '--project', 'app.csproj', '--no-build', '-v', 'q', '--nologo'],
        { cwd: work });
      const output = `${run.stdout ?? ''}${run.stderr ?? ''}`.trimEnd();
      if (output) say(output.split(/\r?\n/).map((l) => `  ${l}`).join('\n'));

      if (run.status !== 0)
        problems.push(run.status === null
          ? 'the consumer project compiled but could not be run'
          : `the consumer project compiles but ${run.status} check(s) FAILED against the packed artifact`);
    }
  }
} finally {
  // Never the given one: it belongs to the caller, who is still using it.
  for (const dir of given ? [work] : [feed, work]) {
    try { rmSync(dir, { recursive: true, force: true }); } catch { /* a temp dir */ }
  }
}

if (problems.length === 0) {
  say(`consumer: ${version} restores, compiles and behaves from the packed artifact.`);
  process.exit(0);
}
process.stderr.write(`consumer: ${problems.length} problem(s)\n${problems.map((p) => `  - ${p}`).join('\n')}\n`);
process.exit(problems.length);
