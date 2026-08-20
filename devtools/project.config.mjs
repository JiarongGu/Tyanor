// project.config.mjs — the ONLY project-specific inputs for the devtools toolkit.
//
// Everything under scripts/ is otherwise generic. To reuse this toolkit in another .NET library repo,
// copy devtools/ and edit THIS file. Nothing else should name Tyanor.

export default {
  name: 'Tyanor',

  /** Where the source lives, for release notes and package metadata. */
  repositoryUrl: 'https://github.com/JiarongGu/Tyanor',

  /** Solution to build and test. */
  solution: 'Tyanor.slnx',

  /** Packable projects, in dependency order. `pack` builds these; `doctor` checks their shape. */
  packages: [
    'src/Tyanor/Tyanor.csproj',
    'src/Tyanor.Providers.Local/Tyanor.Providers.Local.csproj',
    'src/Tyanor.Providers.Aws/Tyanor.Providers.Aws.csproj',
  ],

  /**
   * The dependency BUDGET for the library: every PackageReference it is allowed to take, and no other.
   * A real architectural claim the README makes, and a claim nobody checks is one that quietly stops being
   * true — so it is a test, not a hope. An empty list means genuinely nothing.
   *
   * This was `dependencyFree` with an empty list, back when four packages shipped and the DI wiring lived
   * in its own so the other three could take nothing (D26 merged them). An ALLOWLIST rather than a deleted
   * check, because what mattered was never the number zero — it was that no dependency arrives unnoticed.
   *
   * Note what is still absent: any test framework. The contract suites are meant to be run by whoever wrote
   * an implementation, under whichever framework they already have, and one convenient `xunit` reference
   * here would quietly make that untrue for everyone who uses NUnit.
   */
  dependencyBudget: {
    'src/Tyanor/Tyanor.csproj': ['Microsoft.Extensions.DependencyInjection.Abstractions'],
  },

  /**
   * THE single source of the version, mirrored into the changelog headline. `doctor` also refuses any other
   * project file that declares one — "single-sourced" was previously checked only at the changelog end,
   * while src/ and tests/ each carried a copy.
   */
  versionProps: 'Directory.Build.props',

  /** The decisions log `decisions` validates. */
  decisions: 'docs/DECISIONS.md',

  /** Rules whose index must list every file beside it. */
  rulesDir: '.claude/rules',
  rulesIndex: '.claude/rules/RULES_INDEX.md',

  /**
   * Providers live at src/{providerPrefix}*, and each must have a test project that runs these suites.
   * The skill that describes writing a provider already lists them as required; this is what makes that
   * a check rather than a hope.
   */
  providerPrefix: 'Tyanor.Providers.',
  providerContracts: ['UnitDriverContract', 'FailureClassifierContract', 'DeploymentTargetContract'],

  /**
   * The only files a release may rewrite before packing. The workflow writes the new version (and stamps
   * the changelog) into the working tree, packs, publishes, and commits the bookkeeping only AFTER the
   * push succeeds — so a failed release burns no version and leaves no phantom bump commit. `release`
   * therefore tolerates these being dirty and refuses anything else, which is the part that matters: no
   * stray edit rides along into a published package.
   */
  releaseFiles: ['Directory.Build.props', 'CHANGELOG.md', 'README.md'],

  /**
   * Documents whose C# samples must exist verbatim inside a project that COMPILES. A fenced code block is
   * the part of a document nothing can invalidate, so the guide is checked against a real project rather
   * than trusted. Ignoring indentation, the two hold the same text.
   *
   * `adoption.md` is held to the same rule for a sharper reason than the guide is. A guide gets re-read by
   * whoever changes the API; an adoption document is read ONCE, by someone new, who has no way to tell that
   * the sample they are copying stopped compiling two releases ago.
   */
  compiledSamples: [
    { doc: 'docs/guide.md', project: 'tests/Tyanor.Docs.Tests' },
    { doc: 'docs/adoption.md', project: 'tests/Tyanor.Docs.Tests' },
    { doc: 'docs/providers.md', project: 'tests/Tyanor.Docs.Tests' },
  ],

  /**
   * Documents something in this repository promises exist — the package metadata names the licence, the
   * README points at the guide. `docs` fails if one goes missing, which is the way a promise quietly stops
   * being kept.
   */
  requiredDocs: [
    'README.md', 'LICENSE', 'CHANGELOG.md',
    'docs/guide.md', 'docs/adoption.md', 'docs/providers.md', 'docs/architecture/overview.md',
  ],

  /** Paths the scanners never read (generated, vendored, or its own fixtures). */
  ignore: ['bin', 'obj', 'node_modules', '.git', 'artifacts', 'TestResults'],

  /**
   * The comment marker that silences one line of `check-sensitive`. Project-specific because it appears in
   * this repository's source, and the scripts are meant to be copyable to another repo unchanged.
   */
  allowSecret: 'tyanor:allow-secret',
};
