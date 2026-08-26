# Can `skills@latest` own Skilly's cross-harness exposure?

Research date: 2026-08-26

## Scope and inspected release

This note answers the parameterization and lifecycle questions in [Skilly issue #14](https://github.com/berghtho/skilly/issues/14). It reports evidence and boundaries; it does not make a product decision.

The npm `latest` dist-tag resolved to **`skills@1.5.23`** during this research. The registry metadata identifies source commit **`435076e78988e1e6ec40d00b0b1d76bdbbc5419a`**, which is tagged `v1.5.23`, requires Node `>=22.20.0`, publishes `bin/cli.mjs` as both `skills` and `add-skill`, and provides package integrity, signatures, and an npm/SLSA provenance attestation. The released CLI itself returned `1.5.23` for `--version`. [npm metadata](https://registry.npmjs.org/skills/1.5.23) [immutable package source](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/package.json#L1-L14) [Node requirement](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/package.json#L142-L149) [npm attestation](https://registry.npmjs.org/-/npm/v1/attestations/skills@1.5.23)

Because `latest` is movable, all source and test links below are pinned to that released commit. `skills@latest` describes what the command resolved to on the research date, not a durable API version.

## Finding

**Yes, for a fresh remote installation, one fully non-interactive command can install a selected skill into the user's canonical `~/.agents/skills/<skill>` folder and expose it to OpenCode, Codex, Claude Code, and GitHub Copilot.** The exact current form is:

```powershell
npx --yes skills@latest add "<source>" --global --yes --skill "<skill-name>" --agent opencode --agent codex --agent claude-code --agent github-copilot
```

The deterministic form for the release inspected here is:

```powershell
npx --yes skills@1.5.23 add "<source>" --global --yes --skill "<skill-name>" --agent opencode --agent codex --agent claude-code --agent github-copilot
```

Both `--yes` placements matter. The first belongs to `npx` and suppresses its package-install prompt; npm requires `npx` options before the package positional argument. The second belongs to `skills add` and suppresses the CLI's skill, agent, scope, method, and confirmation prompts. [npm `npx` behavior](https://docs.npmjs.com/cli/v11/commands/npx/#description) [`skills` options documentation](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/README.md#L72-L82) [tested argument parser](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/add.ts#L2156-L2229)

The four explicit agent IDs are valid together. Repeating `--agent` accumulates values, explicit values bypass installed-agent selection, and invalid values fail validation. [`--agent` examples](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/README.md#L90-L109) [selection implementation](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/add.ts#L1391-L1408) [parser tests](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/add.test.ts#L618-L657)

### Resulting topology on Windows

For those four targets, the default non-copy mode produces:

```text
%USERPROFILE%\.agents\skills\<skill>\       real canonical directory
%USERPROFILE%\.claude\skills\<skill>        directory junction -> canonical directory
OpenCode                                      reads the canonical directory directly
Codex                                         reads the canonical directory directly
GitHub Copilot                                reads the canonical directory directly
```

This is not four junctions. The CLI classifies OpenCode, Codex, and GitHub Copilot as "universal" because their project skill directory is `.agents/skills`; a global universal install uses `~/.agents/skills` directly and deliberately does not create the agent-specific global path shown in the CLI's agent table. Claude Code is non-universal, so it gets the link. [agent definitions](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/agents.ts#L152-L160) [Codex definition](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/agents.ts#L219-L227) [Copilot definition](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/agents.ts#L350-L358) [OpenCode definition](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/agents.ts#L531-L539) [universal classification](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/agents.ts#L838-L880) [global universal install behavior](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/installer.ts#L358-L412)

The three direct consumers officially support that user-level canonical location: OpenCode documents `~/.agents/skills`, Codex documents `$HOME/.agents/skills`, and GitHub Copilot documents `~/.agents/skills` for personal skills. Claude Code documents personal skills at `~/.claude/skills` and explicitly follows a symlinked skill directory. [OpenCode discovery](https://opencode.ai/docs/skills/#place-files) [Codex user skills](https://developers.openai.com/codex/build-skills.md#where-codex-loads-local-skills) [GitHub Copilot personal skills](https://docs.github.com/en/copilot/concepts/agents/about-agent-skills) [Claude Code locations and symlinks](https://code.claude.com/docs/en/skills.md#where-skills-live)

The installer first copies the downloaded/cloned skill into `~/.agents/skills/<sanitized-name>`. On Windows it asks Node to create an absolute directory **junction** for a non-universal target. If junction creation throws, it silently falls back to a real copy at the agent path, marks the result `symlinkFailed`, prints a warning, and still treats that target as a successful installation. [canonical location](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/installer.ts#L98-L100) [Windows junction implementation](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/installer.ts#L193-L263) [copy/link/fallback flow](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/installer.ts#L321-L412) [warning](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/add.ts#L2022-L2033)

`--copy` changes the result. It writes directly to each selected agent's resolved directory and creates no junction. Because the three universal targets all resolve to `~/.agents/skills`, they still share one canonical copy; Claude receives an independent copy in `~/.claude/skills`. [`--copy` documentation](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/README.md#L124-L131) [copy implementation](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/installer.ts#L336-L346) [copy tests](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/tests/installer-copy.test.ts#L14-L76)

## Accepted source forms

The command suffix shown above can be appended to each source form below.

### Documented forms

| Source | Exact form |
| --- | --- |
| GitHub shorthand | `npx --yes skills@1.5.23 add owner/repo <suffix>` |
| GitHub repository URL | `npx --yes skills@1.5.23 add https://github.com/owner/repo <suffix>` |
| GitHub subdirectory URL | `npx --yes skills@1.5.23 add https://github.com/owner/repo/tree/<ref>/<path-to-skill> <suffix>` |
| GitLab repository URL | `npx --yes skills@1.5.23 add https://gitlab.com/group/repo <suffix>` |
| Git SSH URL | `npx --yes skills@1.5.23 add git@github.com:owner/repo.git <suffix>` |
| Generic HTTPS Git URL | `npx --yes skills@1.5.23 add https://git.example.com/owner/repo.git <suffix>` |
| Generic SSH Git URL | `npx --yes skills@1.5.23 add ssh://git@git.example.com/owner/repo.git <suffix>` |
| Local path | `npx --yes skills@1.5.23 add C:\path\to\skills <suffix>` |
| Direct URL | `npx --yes skills@1.5.23 add https://example.com/download/my-skill <suffix>` |

Here `<suffix>` is:

```text
--global --yes --skill "<skill-name>" --agent opencode --agent codex --agent claude-code --agent github-copilot
```

The official README documents GitHub shorthand/repository/tree URLs, GitLab, arbitrary Git/SSH, local paths, private-repository authentication, and direct `SKILL.md` or archive downloads. A direct download may be a valid `SKILL.md` or `.zip`, `.tar`, `.tar.gz`, or `.tgz`; a generic HTTP(S) URL first receives RFC 8615 well-known discovery and then direct-download fallback. [source documentation](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/README.md#L28-L70) [direct downloads](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/README.md#L111-L115) [well-known fallback](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/add.ts#L1126-L1132)

### Tested source behavior not fully documented as CLI syntax

The released parser and tests additionally accept:

| Meaning | Accepted form |
| --- | --- |
| GitHub subpath shorthand | `owner/repo/path/to/skill` |
| Select a skill in GitHub shorthand | `owner/repo@skill-name` |
| Pin a Git ref | `owner/repo#ref` |
| Pin a ref and select a skill | `owner/repo#ref@skill-name` |
| Explicit GitHub prefix | `github:owner/repo`, including `/subpath` or `@skill-name` |
| Explicit GitLab prefix | `gitlab:group/repo`, including nested subgroups |
| GitLab subdirectory URL | `https://gitlab.com/group/repo/-/tree/<ref>/<path>` |
| Generic Git ref | append `#ref` to an SSH or `.git` URL |
| Hosted artifact | raw GitHub/GitLab files and GitHub/GitLab archive or release URLs |

These forms are asserted by the released parser tests. A GitHub `/tree/` URL treats the first segment after `tree/` as the ref, so branch names containing `/` are ambiguous; the fragment form can carry a slash-containing ref. [GitHub parser tests](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/tests/source-parser.test.ts#L15-L71) [shorthand/ref tests](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/tests/source-parser.test.ts#L167-L228) [prefix tests](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/tests/source-parser.test.ts#L459-L508) [parser implementation](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/source-parser.ts#L272-L480)

Despite the help text naming the operand `<package>`, `skills add` has no npm-package source provider. A bare npm spec is not parsed as an npm package; unmatched input falls through to a Git URL. `experimental_sync` separately scans an existing project's `node_modules`, but that is not the global remote-install path evaluated here. [help text](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/cli.ts#L105-L170) [source parser fallback](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/source-parser.ts#L465-L480) [`experimental_sync` implementation](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/sync.ts)

## Existing canonical folders

There is **no link-only/expose command** in `1.5.23`. Pointing `skills add` at a skill already located at `~/.agents/skills/<skill>` does not create the missing Claude junction: the installer detects overlap with the destination/canonical directory and returns a successful `skipped` result before link creation. Consequently, an existing Skilly-managed canonical folder cannot be attached to Claude Code with the CLI's junction mechanism without reinstalling from a non-overlapping source. [overlap behavior](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/installer.ts#L321-L360) [available commands](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/cli.ts#L105-L170)

## Lock and provenance state

A successful **global remote** install attempts to write schema-v3 JSON to `$XDG_STATE_HOME/skills/.skill-lock.json` when `XDG_STATE_HOME` is set, otherwise `~/.agents/.skill-lock.json`. Each skill entry can contain:

```json
{
  "version": 3,
  "skills": {
    "<skill-name>": {
      "source": "owner/repo",
      "sourceType": "github",
      "sourceUrl": "https://github.com/owner/repo.git",
      "ref": "<optional-ref>",
      "skillPath": "path/to/SKILL.md",
      "skillFolderHash": "<Git-tree-SHA-or-content-hash>",
      "installedAt": "<ISO-time>",
      "updatedAt": "<ISO-time>",
      "pluginName": "<optional-plugin>"
    }
  },
  "dismissed": {}
}
```

Well-known entries instead add `sourceBaseUrl` and `wellKnownDigest`. The file is human-readable state, not an installation receipt emitted to stdout. Lock write failures are swallowed and do not fail installation. [schema and path](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/skill-lock.ts#L6-L73) [write/add behavior](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/skill-lock.ts#L105-L193) [Git/global lock population](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/add.ts#L1854-L1898) [well-known lock population](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/add.ts#L916-L935)

The lock does **not** record selected agents, install mode, whether a junction fell back to a copy, junction destinations, or a completed/partial result. `lastSelectedAgents` exists only as interactive prompt history and is not set by the explicit non-interactive command. Local sources and direct-download fallback do not produce a global provenance entry because they have no normalized remote source. [lock schema](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/skill-lock.ts#L10-L60) [noninteractive lock gate](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/add.ts#L1812-L1818) [direct-download test](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/tests/direct-download-add.test.ts#L131-L157)

The folder hash supports change detection but is not a package-manager-style immutable resolution. An unpinned install follows the source's current default branch; the lock records a tree/content hash and later compares it with current upstream state. The separate npm package itself has signed registry metadata and SLSA provenance, but the installed skill lock is not an npm provenance attestation. [hash semantics](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/skill-lock.ts#L24-L29) [update comparison](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/update.ts#L550-L633) [npm release provenance](https://registry.npmjs.org/skills/1.5.23)

## List, update, check, and remove

### List

The sole general machine-readable command is:

```powershell
npx --yes skills@1.5.23 list --global --json
```

It returns a JSON array with `name`, canonical `path`, `scope`, detected agent display names, `source`, `sourceUrl`, and `sourceType`, and emits no ANSI in this mode. It does not return lock hashes, refs, link mode, link target, fallback status, or an installation transaction ID. Agent attribution is reconstructed from current filesystem and agent detection rather than the original requested target set. [documented JSON option](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/cli.ts#L163-L166) [JSON shape](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/list.ts#L97-L128) [filesystem reconstruction](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/installer.ts#L1073-L1167)

### Update and `check`

The fully non-interactive update form for one global skill is:

```powershell
npx --yes skills@1.5.23 update "<skill-name>" --global --yes
```

`upgrade` is an alias. `check` is also routed to the exact same `runUpdate` implementation: it is **not a read-only or dry-run check** and installs updates it finds. `check` is omitted from the public README/help command list even though dispatch and tests retain it. [command dispatch](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/cli.ts#L387-L395) [documented update forms](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/README.md#L174-L199) [update tests](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/tests/update.test.ts)

When an update is found, the global updater invokes the same released CLI entrypoint as `add <recorded-source> --skill <name> -g -y`. It does **not** pass the original `--agent` values or `--copy` mode because those were not recorded. The child `add` redetects agents, always includes all universal agents, defaults back to link mode when multiple directories are involved, refreshes the canonical folder, and recreates a Claude junction if Claude is detected. The original four-agent exposure usually reappears on a machine where the original Claude junction made `~/.claude` detectable, but exact exposure and copy/link preservation are not guaranteed by persisted state. [update child argv](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/update.ts#L674-L720) [auto-selection](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/add.ts#L1471-L1492) [default mode](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/add.ts#L1572-L1609)

In non-interactive mode, a skill deleted upstream is reported but deliberately not removed. GitHub entries with usable folder hashes can be checked through the tree API with authenticated-clone fallback; entries lacking enough provenance are skipped with a human-readable reason. [noninteractive deletion behavior](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/update.ts#L256-L288) [global update eligibility](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/update.ts#L478-L524)

### Remove

The all-exposures removal form is:

```powershell
npx --yes skills@1.5.23 remove "<skill-name>" --global --yes
```

With no `--agent`, removal targets every known agent path, then removes the canonical folder and global lock entry when it concludes no other detected agent uses them. A selective form such as `remove <skill> -g -y --agent claude-code` removes the Claude junction, but preservation of the shared canonical folder depends on current installed-agent detection. OpenCode, Codex, and Copilot cannot be independently unexposed by deleting separate links because all three consume the same canonical folder directly. [remove documentation](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/README.md#L211-L250) [all-agent cleanup](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/remove.ts#L197-L205) [canonical retention/removal](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/remove.ts#L235-L321) [canonical retention tests](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/tests/remove-canonical.test.ts#L17-L104)

## Machine-readable output and exit behavior

| Operation | Machine-readable result | Relevant exit behavior in `1.5.23` |
| --- | --- | --- |
| `add` | None; human Clack/ANSI output only | Obvious argument, source, discovery, and validation failures exit 1. Per-agent failures collected after installation are printed but do not set a nonzero exit code. Junction failure falls back to copy and counts as success. |
| `list -g --json` | JSON array | Invalid agent filters exit 1; a valid empty result is `[]` with exit 0. |
| `update` / `check` | None; human output only | Child reinstall failures set exit code 1. Some inability to check a source is printed without incrementing the failure count, so exit 0 does not prove every source was checked. |
| `remove` | None; human output only | Invalid agents and dangerous `--all` plus named-skill combinations exit 1. A missing requested skill returns after a human error without setting exit 1; per-skill removal failures are printed without setting a nonzero exit code. |

These are source behaviors, not a documented stable automation contract. [add failure reporting](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/add.ts#L2036-L2064) [list behavior](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/list.ts#L76-L128) [update exit handling](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/update.ts#L997-L1008) [update check error path](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/update.ts#L635-L639) [remove no-match and result handling](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/remove.ts#L168-L175) [remove result handling](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/remove.ts#L323-L379)

## Delegation boundary: behavior Skilly would still own

If Skilly invokes this CLI rather than implementing fresh-install exposure itself, the released CLI can own source acquisition, skill discovery/selection, canonical copying, Windows junction creation for Claude, direct exposure to the three universal harnesses, and best-effort global source/hash tracking. The remaining observable responsibilities are:

1. **Runtime and release control.** Supply Node `>=22.20.0` plus npm/npx, decide whether to accept movable `latest` behavior or pin a tested version, and manage upgrade compatibility. The CLI exposes no declared stable JSON automation API for add/update/remove. [release metadata](https://registry.npmjs.org/skills/1.5.23)
2. **Safe process invocation.** Build an argument vector rather than a shell command string; pass source, exact skill name, global scope, both non-interactive flags, and the four explicit agent IDs. Supply Git/GitHub credentials and relevant environment overrides where needed. The CLI's own updater likewise uses `shell: false` to avoid Windows command injection. [private source authentication](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/README.md#L50-L70) [safe update spawning](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/update.ts#L698-L711)
3. **Trust and telemetry policy.** Decide whether and how to review downloaded skills and whether to set `DISABLE_TELEMETRY=1` or `DO_NOT_TRACK=1`; those are CLI policy inputs, not outcomes recorded in the lock. [telemetry documentation](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/README.md#L524-L543)
4. **Postcondition verification and status translation.** Because add/update/remove lack JSON and some partial failures still exit 0, inspect `list --global --json` and, when junction semantics matter, filesystem/reparse-point state. Translate human/partial outcomes into Skilly's machine-facing status and error model.
5. **Exposure inventory and reconciliation.** Persist the intended harness set and requested mode separately. The global lock does not contain them, updates do not replay them, and the three universal harnesses share one indivisible exposure path.
6. **Existing-install adoption.** Create/reconcile links itself or reinstall from the recorded non-overlapping source; `skills@1.5.23` cannot link an already-canonical folder in place.
7. **Provenance gaps.** Keep provenance for local paths and direct downloads, and retain any stronger immutable version/integrity policy. The CLI's best-effort global lock can be absent after a successful install and is not a complete exposure receipt.
8. **Lifecycle semantics not provided by the CLI.** Provide a true check-only/update-available operation if required, decide how stale upstream-deleted skills are handled, and handle rollback/repair after partial installation or copy fallback. `check` currently updates, and non-interactive update deliberately keeps upstream-deleted skills.
9. **Harness-session refresh UX.** Filesystem exposure does not guarantee an already-running harness has reloaded it. OpenCode scans the documented global path; Codex says it detects changes but may require restart; Copilot CLI provides `/skills reload`; Claude watches existing top-level skill directories but says to restart if the top-level directory was created after session start. [OpenCode discovery](https://opencode.ai/docs/skills/#understand-discovery) [Codex change detection](https://developers.openai.com/codex/build-skills.md#create-a-skill) [Copilot reload](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-skills#adding-a-skill-that-someone-else-has-created) [Claude live detection](https://code.claude.com/docs/en/skills.md#live-change-detection)

This list describes uncovered behavior; it is not a recommendation about which component should implement it.

## Evidence classification and unresolved unknowns

### Documented behavior

- The source forms and add flags in the README, including `-g`, repeatable target agents, skill selection, `-y`, `--copy`, and `--all`.
- The four harnesses' official user skill locations, including direct `~/.agents/skills` discovery by OpenCode, Codex, and GitHub Copilot, and symlink support at Claude Code's `~/.claude/skills` location.
- `list --json`, update/remove command forms, private-source authentication, and telemetry controls.

### Released source and test behavior

- Exact agent IDs, universal/non-universal classification, and the global canonical-path override.
- Absolute Windows `junction` creation, copy fallback, and overlap short-circuiting.
- Additional source syntaxes, lock schema/gaps, update child arguments, `check` aliasing update, remove's detection-dependent canonical retention, and exit-code limitations.
- The released tests explicitly cover parser forms, repeated agent options, universal path deduplication, copy behavior, canonical removal protection, update failure exit codes, and installer-created Claude links. [source parser tests](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/tests/source-parser.test.ts) [add tests](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/add.test.ts) [installer link tests](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/tests/installer-symlink.test.ts) [update tests](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/tests/update.test.ts) [remove tests](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/remove.test.ts)

### Unknown or not guaranteed

- There is no released end-to-end test that executes the exact global four-harness command against four real Windows harness installations. The global-universal test comments that it avoids writing to the real home directory and verifies project-equivalent behavior instead. [test limitation](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/tests/installer-symlink.test.ts#L260-L299)
- Junction success varies with filesystem, policy, and target environment. The implementation catches every junction error and substitutes a copy, so callers cannot assume link topology from exit code alone.
- The source does not define compatibility guarantees for CLI text, exit semantics, undocumented source forms, lock schema migrations, or the meaning of future `latest` releases.
- Exact update preservation of the originally requested four targets is not guaranteed because target agents and install mode are absent from the lock and update reruns auto-selection.
- The released source tests were run in this Windows research environment: 770 of 776 passed. The six failures were one build test unable to find a bare `pnpm` executable when the suite was launched through Corepack and five test setup calls that requested ordinary Windows symlinks without available privilege. A focused run of the two tests that exercise installer-created Claude links passed; those paths use the implementation's Windows junction type. This validates the inspected code path in this environment but does not replace the missing real-home/four-harness end-to-end test.
