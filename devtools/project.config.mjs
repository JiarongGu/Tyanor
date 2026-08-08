// project.config.mjs — the ONLY project-specific inputs for the devtools toolkit.
//
// Everything under scripts/ is otherwise generic. To reuse this toolkit in another .NET library repo,
// copy devtools/ and edit THIS file. Nothing else should name Tyanor.

export default {
  name: 'Tyanor',

  /** Solution to build and test. */
  solution: 'Tyanor.slnx',

  /** Packable projects, in dependency order. `pack` builds these; `doctor` checks their shape. */
  packages: [
    'src/Tyanor.Core/Tyanor.Core.csproj',
    'src/Tyanor.Engine/Tyanor.Engine.csproj',
    'src/Tyanor.Extensions.DependencyInjection/Tyanor.Extensions.DependencyInjection.csproj',
    'src/Tyanor.Providers.Local/Tyanor.Providers.Local.csproj',
  ],

  /**
   * Projects that must take NO PackageReference at all. This is a real architectural claim the README
   * makes, and a claim nobody checks is one that quietly stops being true — so it is a test, not a hope.
   */
  dependencyFree: [
    'src/Tyanor.Core/Tyanor.Core.csproj',
    'src/Tyanor.Engine/Tyanor.Engine.csproj',
  ],

  /** Single source of the version, mirrored into the changelog headline. */
  versionProps: 'src/Directory.Build.props',

  /** The decisions log `decisions` validates. */
  decisions: 'docs/DECISIONS.md',

  /** Rules whose index must list every file beside it. */
  rulesDir: '.claude/rules',
  rulesIndex: '.claude/rules/RULES_INDEX.md',

  /** Paths `check-sensitive` never reads (generated, vendored, or its own fixtures). */
  ignore: ['bin', 'obj', 'node_modules', '.git', 'artifacts', 'TestResults'],
};
