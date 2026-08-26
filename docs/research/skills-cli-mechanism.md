# How `skills@latest` installs and maintains skills across harnesses

Research date: 2026-08-26

## Scope and inspected version

This note describes observed behavior only. It does not recommend a Skilly product design.

At the time of inspection, npm's `latest` dist-tag for package `skills` was `1.5.23`. The immutable version metadata identifies `https://github.com/vercel-labs/skills.git` as the owning repository, maps both the `skills` and `add-skill` binaries to `bin/cli.mjs`, requires Node `>=22.20.0`, and records git head `435076e78988e1e6ec40d00b0b1d76bdbbc5419a`.[^npm-dist-tags] [^npm-version] All implementation links below are pinned to that commit, whose `package.json` also identifies version 1.5.23 and the same binary entry points.[^package]

## Answer gist

`npx skills@latest` is a multi-source installer and a lightweight stateful updater, not a package manager with a central content store. In its recommended multi-harness mode it materializes one canonical skill directory at `<project>/.agents/skills/<name>` or `~/.agents/skills/<name>`, lets OpenCode, Codex, and GitHub Copilot consume that directory directly, and exposes the same directory to Claude Code through `.claude/skills/<name>` or `~/.claude/skills/<name>`. On Windows that Claude exposure is an absolute directory junction, not a Unix-style relative symlink. `--copy` instead writes into each effective harness directory, although the three universal harnesses still resolve to the same `.agents/skills` directory. Project provenance is stored in `skills-lock.json`; global provenance and UI state are stored in `.skill-lock.json`. Global updates compare recorded upstream hashes before reinstalling changed skills, while project updates currently refresh every remotely tracked skill rather than comparing the recorded local hash. Removal deletes selected harness entries and removes canonical content and provenance only when the implementation decides no remaining detected harness uses it.[^installer-core] [^agents-universal] [^local-lock] [^global-lock] [^update-project] [^update-global-check] [^remove]

## Source parsing and skill discovery

### Accepted sources

`add` accepts local paths, GitHub shorthand and URLs, GitLab URLs, generic Git URLs including SSH, arbitrary HTTP(S) well-known sources, and direct `SKILL.md` or archive downloads. On Windows, drive-qualified paths such as `C:\work\skills` and `D:/work/skills` are explicitly recognized as local paths; relative `./`, `../`, `.`, and `..` forms are resolved with Node's platform path resolver. Backslashes in repository subpaths are normalized for traversal checks, and any `..` path segment is rejected.[^source-parser-local] [^source-parser-remote]

Remote Git sources are shallow-cloned into an `os.tmpdir()` directory named `skills-*`. The clone allows `https`, `http`, `ssh`, `git`, and `file` transports, disables interactive credential prompts and Git LFS smudging, and defaults to a five-minute timeout. For a GitHub HTTPS authentication failure it tries existing Git credentials first, then `gh repo clone` if GitHub CLI is authenticated, then batch-mode SSH. Temporary clone cleanup is guarded so it cannot delete outside the operating-system temp directory.[^git-clone]

GitHub sources owned by a small allowlist can use a source-specific blob/snapshot fast path; otherwise the CLI clones. Local sources are read in place, and generic HTTP(S) sources try `/.well-known/agent-skills/index.json` or the legacy endpoint before falling back to a bounded direct file/archive download.[^add-acquisition] [^readme-download]

### Finding skills inside a source

A valid skill must have a readable `SKILL.md` whose YAML frontmatter contains string-valued `name` and `description`. Internal skills (`metadata.internal: true`) are omitted unless explicitly named or `INSTALL_INTERNAL_SKILLS=1|true` is set.[^skills-parse]

Discovery first checks a directly addressed root skill. It then checks `skills/`, curated/experimental/system subcontainers, known harness skill containers, and paths declared by Claude plugin manifests. Known containers are walked to three directory levels; discovery stops below a shallower `SKILL.md`, so a shallower skill shadows nested candidates. If no skill is found, the CLI recursively searches to its fallback depth; `--full-depth` also forces recursive discovery outside known containers. Duplicate frontmatter names are suppressed in first-found order.[^skills-discovery] The official CLI README describes the same bounded-container and plugin-manifest behavior.[^readme-discovery]

## Windows harness detection

Harness auto-detection is directory-based, not executable/PATH-based. For the four harnesses in scope, version 1.5.23 checks these locations:[^agents-four]

| Harness | Windows detection probe |
| --- | --- |
| OpenCode | `<XDG_CONFIG_HOME>/opencode`; if `XDG_CONFIG_HOME` is unset, `<home>/.config/opencode` |
| Codex | `CODEX_HOME` when non-empty, otherwise `<home>/.codex` (also `/etc/codex`, which is normally irrelevant on native Windows) |
| Claude Code | `CLAUDE_CONFIG_DIR` when non-empty, otherwise `<home>/.claude` |
| GitHub Copilot | `<home>/.copilot` |

The OpenCode base comes from `xdg-basedir` 5.1.0. That package defines `xdgConfig` as `XDG_CONFIG_HOME` or `<home>/.config` on every platform; it does not substitute `%APPDATA%` on Windows.[^xdg-source] Consequently, a harness can be installed but missed when its expected config directory has not been created, and a leftover config directory can count as installed after the executable is gone. Explicit `--agent` bypasses detection.[^agents-detect] [^add-targets]

When no `--agent` is supplied, interactive installs prompt if needed. With `-y`, detected harnesses are selected and all harnesses whose project directory is `.agents/skills` are added; if no harness is detected, `-y` selects every supported harness. When the CLI detects that it is running inside an AI agent, it also enables non-interactive mode and maps that agent to a target unless targets were explicit.[^add-targets] [^add-agent-mode]

## Installation topology

### Canonical location

The canonical base is always `<cwd>/.agents/skills` for project scope and `<home>/.agents/skills` for global scope. Scope is based on the process current working directory, not a search for the Git repository root.[^installer-canonical] A sanitized, lowercased skill name forms the final directory component.[^installer-name]

The implementation classifies a harness as "universal" when its project `skillsDir` is exactly `.agents/skills`. OpenCode, Codex, and GitHub Copilot are universal; Claude Code is not.[^agents-four] [^agents-universal]

### Exposure for the four harnesses

In multi-directory symlink mode, the effective locations are:[^installer-routing]

| Scope | Canonical content | OpenCode | Codex | GitHub Copilot | Claude Code |
| --- | --- | --- | --- | --- | --- |
| Project | `<cwd>/.agents/skills/<name>` | canonical directory | canonical directory | canonical directory | `<cwd>/.claude/skills/<name>` linked to canonical |
| Global | `<home>/.agents/skills/<name>` | canonical directory | canonical directory | canonical directory | `<CLAUDE_CONFIG_DIR or home/.claude>/skills/<name>` linked to canonical |

These universal locations are recognized by each harness's official documentation: OpenCode scans both project and global `.agents/skills`; Codex scans `.agents/skills` from the current directory to repository root and `$HOME/.agents/skills`; and GitHub Copilot supports project `.agents/skills` plus personal `~/.agents/skills`.[^opencode-docs] [^codex-docs] [^copilot-docs] Claude Code officially uses project `.claude/skills` and personal `~/.claude/skills`, follows directory symlinks, and deduplicates the same target reached through multiple locations.[^claude-docs]

The CLI's generated README table lists native global directories (`~/.config/opencode/skills`, `~/.codex/skills`, and `~/.copilot/skills`), but installation routing checks universal status before those configured native paths. Therefore universal global installs actually remain in `~/.agents/skills`; this still matches all three harnesses' official discovery rules.[^readme-agent-table] [^installer-routing]

For a project install, the CLI normally avoids creating a non-universal harness directory when that harness's project config root is absent. Claude Code is explicitly exempt: selecting Claude Code creates `.claude/skills/<name>` even when `.claude` did not previously exist.[^installer-project-skip]

### Copy versus link/junction

The interactive default for more than one distinct harness destination is "Symlink (Recommended)"; `-y` also leaves symlink mode selected. `--copy` forces copy mode. If every selected target has one effective project `skillsDir`, the CLI silently chooses copy mode because linking would add no separate destination.[^add-mode]

In symlink mode the source skill is first copied into the canonical directory. Universal harnesses use that directory directly. Each non-universal harness receives a directory link to it. On non-Windows platforms the target is a relative symlink; on Windows `platform() === 'win32'` selects Node's `junction` type and passes the absolute resolved canonical path.[^installer-link] Node documents `junction` as the Windows directory-junction type and requires an absolute target.[^node-symlink]

If junction/symlink creation throws, the installer catches the failure and places a full copy at the harness location while retaining the canonical copy; it reports `symlinkFailed` but treats the install as successful. The user-facing warning recommends Windows Developer Mode, although the actual Windows code path requested a junction.[^installer-link] [^installer-fallback]

In copy mode, no extra link is created and content is copied directly to `getAgentBaseDir()`. Because that function routes universal harnesses to `.agents/skills` before considering native global directories, OpenCode, Codex, and Copilot are not independent copies from one another; they still share the same canonical path. Claude Code receives a separate copy in `.claude/skills`.[^installer-routing] [^installer-copy]

Each install replaces the destination directory before writing, which removes files deleted or renamed upstream. Copies exclude `metadata.json`, `.git`, `__pycache__`, and `__pypackages__`; source symlinks are dereferenced, and broken source symlinks are skipped with a warning.[^installer-copy-details]

## Provenance and state

### Project state

Project installs write `<cwd>/skills-lock.json`, schema version 1, intended for version control. Entries are sorted by skill name and contain `source`, optional original `sourceUrl`, optional `ref`, `sourceType`, optional repository-relative `skillPath`, a recursive SHA-256 `computedHash`, optional Eve placement, and an optional well-known digest. Local paths are made relative with forward slashes when possible; cross-drive Windows paths remain absolute because they cannot be represented relative to the project.[^local-lock]

### Global state

Global installs write `$XDG_STATE_HOME/skills/.skill-lock.json` when `XDG_STATE_HOME` is set, otherwise `<home>/.agents/.skill-lock.json`. Schema version 3 entries contain normalized source and type, original source URL, optional ref and skill path, `skillFolderHash`, installed/updated timestamps, and optional plugin/well-known fields. The same file stores dismissed prompts and the most recently selected agents.[^global-lock]

Neither lock entry records the selected harnesses nor installation mode. `lastSelectedAgents` is one UI preference for the latest selection, not per-skill provenance.[^global-lock-shape] Lock reads return an empty structure for missing, malformed, or older-schema files. More importantly, all add flows intentionally swallow lock-write errors, so files can be installed successfully with no update/removal provenance.[^global-lock-read] [^local-lock-read] [^add-lock-write]

Direct-download installs are not written to the project lock, and global Git/local/download installs without a normalizable remote owner/repository are not written to the global lock. Project entries whose `sourceType` is `local` or `node_modules` are excluded from ordinary `update`; node-module restoration belongs to `experimental_sync`.[^add-lock-conditions] [^update-project-filter]

## Update detection and application

`update`/`upgrade`/`check` accepts skill names and `--global`, `--project`, and `--yes`. Explicit names with no scope check both scopes. Otherwise non-interactive mode chooses project when either `skills-lock.json` exists or `.agents/skills` contains a skill, and global when neither condition is true.[^update-scope]

### Global

For tracked GitHub skills, the lock normally stores the skill folder's Git tree SHA. The updater fetches the recursive repository tree anonymously, then tries `GITHUB_TOKEN`/`GH_TOKEN`, then `gh api` only for rate-limit or private-repository responses; if the API path is unavailable, update falls back to an authenticated shallow clone. Generic Git sources are cloned and compared with either a Git tree hash or a recursive SHA-256 folder hash, depending on the recorded hash format.[^global-hash-write] [^github-tree-auth] [^update-global-check]

Only a recorded skill whose latest hash differs is scheduled for reinstall. Checks are grouped by source and ref. Application spawns the same pinned CLI entry point as `add <source> --skill <name> -g -y`, with `shell: false`; this replaces the canonical directory and refreshes whichever links/copies the new add invocation selects.[^update-global-apply]

Well-known sources compare their recorded digest and reinstall changed skills. New well-known skills are announced but not installed automatically. Upstream deletions prompt for local removal only in interactive mode; `-y` and non-TTY operation explicitly skip deletion.[^update-well-known] [^update-deletion]

### Project

Project update is a refresh operation, not change detection: every remotely tracked entry with `skillPath` is re-added with `-y`; the recorded `computedHash` is never consulted by `updateProjectSkills`. The source is cloned first to identify upstream deletions, and the update preserves recorded Eve subagent placement, but it does not preserve ordinary harness targets or copy/link mode because those fields are absent from the lock and are not passed to the child `add` command.[^update-project] [^local-lock-shape]

Old project entries without `skillPath` cannot be updated in place and are only given a reinstall hint. Local and node-module entries are skipped. For both scopes, updates target already locked names; discovery of unrelated new Git-hosted skills does not add them.[^update-project-filter] [^update-legacy]

## Removal

`remove`/`rm`/`r` defaults to project scope and supports names, `--global`, `--agent`, `--skill`, `--yes`, and `--all`. Without names it presents an interactive skill selector. Without `--agent` it targets every known harness, including harnesses not currently detected, to clean stale links. It scans the canonical and harness directories and includes stale lock keys when resolving names.[^remove-scan] [^remove-options]

For each selected skill, removal deletes target harness entries first. It then preserves the canonical directory and lock entry when a remaining detected harness still has the skill; otherwise it deletes canonical content and removes the matching project/global lock entry. This allows a Claude-only link to be removed while universal consumers retain the canonical skill.[^remove-canonical]

OpenCode, Codex, and Copilot cannot have independent membership in project or global `.agents/skills`: all three resolve to the same path. An agent-specific removal among those harnesses therefore cannot physically hide the skill from only one of them. Whether the shared canonical directory survives depends on detection of another remaining harness, not a persisted per-skill target list.[^installer-routing] [^remove-canonical]

The remover does not delete now-empty parent skill directories. Upstream deletions discovered by update are not automatically removed in non-interactive mode.[^remove-canonical] [^update-deletion]

## CLI and machine-readable behavior

The 1.5.23 dispatcher exposes these commands and aliases:[^cli-dispatch] [^cli-help]

| Command | Purpose and notable options |
| --- | --- |
| `add` (`a`, `install`, `i`) | Discover/install; `-g`, `-a`, `-s`, `-l`, `-y`, `--copy`, `--all`, `--full-depth`, `--metadata`, Eve `--subagent` |
| `use` | Resolve one skill into a temporary directory and print a generated prompt, or launch one supported agent |
| `list` (`ls`) | List installed project skills by default; `-g`, `-a`, `--json` |
| `find` (`search`, `f`, `s`) | Search skills, optionally by owner |
| `remove` (`rm`, `r`) | Remove names/targets/scope or all |
| `update` (`upgrade`, `check`) | Refresh/check tracked project/global skills |
| `init` | Create a `SKILL.md` template |
| `experimental_install` | Restore project lock entries into canonical `.agents/skills` only |
| `experimental_sync` | Sync skills discovered in `node_modules` |
| `--help`, `--version` | Help/version output |

Only `list --json` provides structured JSON. Its stdout is an array of `{name, path, scope, agents, source, sourceUrl, sourceType}` objects with no ANSI formatting; `-g` is required for global JSON because implementation defaults `list` to project scope.[^list-json] This differs from the README sentence claiming plain `list` lists both project and global skills, although the README examples separately use `-g` for global.[^readme-list]

`use` is pipe-friendly rather than JSON: without `--agent`, official documentation says it prints only the generated prompt to stdout.[^readme-use] There is no JSON mode for source discovery (`add --list`), add, update, or remove.[^cli-help]

Exit status is only partially automation-safe. Parse/clone/fatal add errors exit nonzero, and update sets exit code 1 when an attempted reinstall reports failure. However, per-target add failures and per-skill remove failures are printed without setting a failure exit code, and a caught global update-check failure is printed but does not increment `failCount`. Scripts needing authoritative reconciliation cannot rely on exit code alone; `list --json` can inspect resulting on-disk state.[^add-exit] [^remove-exit] [^update-check-exit] [^update-final-exit]

## Limitations established by the implementation

1. Installed-harness detection is config-directory existence, so it can be stale or incomplete, especially for a fresh Windows install whose config directory has not been created.[^agents-four]
2. Per-skill harness targets and copy/link mode are not persisted. A later `update` can choose a different topology from the original install based on the current process, detection results, and prompt/non-interactive state.[^global-lock-shape] [^local-lock-shape] [^update-global-apply] [^update-project]
3. Project `computedHash` records provenance but is not used to avoid unnecessary refreshes or detect local edits before overwrite.[^local-lock-shape] [^update-project]
4. Lock writes are best-effort and can silently fail after content installation, leaving skills untracked.[^add-lock-write]
5. Local paths, direct downloads, some non-normalizable global sources, old locks without `skillPath`, and node-module skills are not maintained by ordinary `update`.[^add-lock-conditions] [^update-project-filter] [^update-legacy]
6. Universal harnesses share one physical directory, so installation/removal cannot independently expose a skill to only OpenCode, only Codex, or only Copilot once it is in `.agents/skills`.[^installer-routing]
7. Source and installed-skill name handling are not identical to every harness's validation. The CLI accepts any string-valued frontmatter name and sanitizes its directory name, while OpenCode's official rules require a lowercase hyphenated name that matches the directory. A skill the CLI installs can therefore still be rejected by a harness.[^skills-parse] [^installer-name] [^opencode-docs]
8. The package requires Node 22.20 or newer.[^npm-version]
9. Machine-readable output is limited to `list --json`; mutating commands do not emit structured results and some partial failures retain exit code zero.[^list-json] [^add-exit] [^remove-exit]

## Local Windows verification

The pinned source was checked out and targeted tests were run under Node 24.17.0 on native Windows. The cross-platform path, XDG path, update, removal, and most installer/list tests passed. Five test cases failed during fixture setup with `EPERM` because those tests directly call Node's default `symlink()` without the CLI's Windows `junction` type; the CLI-specific fresh Claude install tests passed and created junctions. The relevant test fixtures are pinned here.[^installer-tests] [^path-tests] This verification supports reading the product code as junction-based, but it does not cover network authentication variants, GitHub Enterprise, WSL, UNC paths, cross-volume junction targets, or every interactive prompt.

## Primary sources

All CLI implementation claims use the owning repository at commit `435076e78988e1e6ec40d00b0b1d76bdbbc5419a`. npm registry metadata and official OpenCode, Codex, Claude Code, GitHub Copilot, Node.js, and `xdg-basedir` documentation/source were the only external sources used.

[^npm-dist-tags]: [npm registry dist-tags for `skills`](https://registry.npmjs.org/-/package/skills/dist-tags)
[^npm-version]: [npm registry metadata for `skills@1.5.23`](https://registry.npmjs.org/skills/1.5.23)
[^package]: [`package.json` at the inspected git head](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/package.json#L1-L15)
[^source-parser-local]: [`src/source-parser.ts`: Windows/local path recognition and traversal normalization](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/source-parser.ts#L101-L137)
[^source-parser-remote]: [`src/source-parser.ts`: source formats and parsing](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/source-parser.ts#L272-L480)
[^git-clone]: [`src/git.ts`: clone environment, authentication fallbacks, temp directory, and cleanup](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/git.ts#L9-L17), [clone implementation](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/git.ts#L104-L169), [fallbacks and cleanup](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/git.ts#L172-L299)
[^add-acquisition]: [`src/add.ts`: acquisition paths](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/add.ts#L1126-L1231)
[^readme-download]: [CLI README: direct downloads and bounds](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/README.md#L111-L115)
[^skills-parse]: [`src/skills.ts`: required frontmatter and internal filtering](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/skills.ts#L55-L131)
[^skills-discovery]: [`src/skills.ts`: priority, bounded, plugin, and fallback discovery](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/skills.ts#L176-L320)
[^readme-discovery]: [CLI README: skill and plugin-manifest discovery](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/README.md#L400-L494)
[^agents-four]: [`src/agents.ts`: home/config bases and four harness configurations](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/agents.ts#L7-L17), [Claude Code](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/agents.ts#L152-L160), [Codex](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/agents.ts#L219-L227), [Copilot](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/agents.ts#L350-L358), [OpenCode](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/agents.ts#L531-L539)
[^xdg-source]: [`xdg-basedir` 5.1.0 source at npm git head `8cceade...`](https://github.com/sindresorhus/xdg-basedir/blob/8cceade858e4da18cb971bf1844f086e9e213563/index.js#L1-L14)
[^agents-detect]: [`src/agents.ts`: installed-agent aggregation](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/agents.ts#L794-L802)
[^add-targets]: [`src/add.ts`: explicit, detected, and non-interactive target selection](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/add.ts#L1391-L1493)
[^add-agent-mode]: [`src/add.ts`: AI-agent non-interactive behavior](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/add.ts#L1064-L1097)
[^installer-canonical]: [`src/installer.ts`: canonical base and agent routing](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/installer.ts#L98-L149)
[^installer-name]: [`src/installer.ts`: installed directory-name sanitization](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/installer.ts#L44-L65)
[^agents-universal]: [`src/agents.ts`: universal classification](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/agents.ts#L838-L880)
[^installer-core]: [`src/installer.ts`: local skill installation](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/installer.ts#L265-L421)
[^installer-routing]: [`src/installer.ts`: universal-first effective destination routing](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/installer.ts#L113-L149), [canonical/link install path](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/installer.ts#L285-L300)
[^opencode-docs]: [OpenCode official Agent Skills documentation](https://opencode.ai/docs/skills/#place-files)
[^codex-docs]: [OpenAI official Codex skill locations](https://developers.openai.com/codex/skills/#where-codex-loads-local-skills)
[^copilot-docs]: [GitHub official Copilot agent skills documentation](https://docs.github.com/en/copilot/concepts/agents/about-agent-skills)
[^claude-docs]: [Anthropic official Claude Code skills documentation: locations and symlinks](https://code.claude.com/docs/en/skills#where-skills-live)
[^readme-agent-table]: [CLI README: advertised harness paths](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/README.md#L265-L341)
[^installer-project-skip]: [`src/installer.ts`: absent project harness roots and Claude exception](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/installer.ts#L374-L389)
[^add-mode]: [`src/add.ts`: install mode selection](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/add.ts#L1572-L1609)
[^installer-link]: [`src/installer.ts`: cross-platform link creation](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/installer.ts#L193-L263)
[^node-symlink]: [Node.js `fsPromises.symlink` documentation](https://nodejs.org/docs/latest-v24.x/api/fs.html#fspromisessymlinktarget-path-type)
[^installer-fallback]: [`src/installer.ts`: failed-link copy fallback](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/installer.ts#L391-L412), [user-facing Windows warning](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/add.ts#L2025-L2032)
[^installer-copy]: [`src/installer.ts`: direct copy mode](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/installer.ts#L336-L346)
[^installer-copy-details]: [`src/installer.ts`: replacement and copy exclusions/dereferencing](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/installer.ts#L155-L170), [copy details](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/installer.ts#L423-L514)
[^local-lock]: [`src/local-lock.ts`: project schema, path, sorting, Windows portability, and hashing](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/local-lock.ts#L5-L67), [read/write and paths](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/local-lock.ts#L69-L159)
[^local-lock-shape]: [`src/local-lock.ts`: project lock entry fields](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/local-lock.ts#L8-L60)
[^global-lock]: [`src/skill-lock.ts`: global schema and state path](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/skill-lock.ts#L6-L73), [write and timestamps](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/skill-lock.ts#L105-L118), [selection state](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/skill-lock.ts#L258-L293)
[^global-lock-shape]: [`src/skill-lock.ts`: global per-skill and file fields](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/skill-lock.ts#L10-L60)
[^global-lock-read]: [`src/skill-lock.ts`: invalid/old lock behavior](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/skill-lock.ts#L75-L103)
[^local-lock-read]: [`src/local-lock.ts`: invalid/old project lock behavior](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/local-lock.ts#L69-L100)
[^add-lock-write]: [`src/add.ts`: best-effort global and project lock writes](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/add.ts#L1854-L1937)
[^add-lock-conditions]: [`src/add.ts`: lock eligibility conditions](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/add.ts#L1812-L1819), [global/project conditions](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/add.ts#L1854-L1902)
[^update-project-filter]: [`src/update.ts`: project update filtering](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/update.ts#L239-L253)
[^update-scope]: [`src/update.ts`: parsing and scope resolution](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/update.ts#L38-L77), [project detection and scope](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/update.ts#L80-L165)
[^global-hash-write]: [`src/add.ts`: recording GitHub tree or folder hashes](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/add.ts#L1854-L1895)
[^github-tree-auth]: [`src/blob.ts`: anonymous, token, and GitHub CLI tree lookup](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/blob.ts#L209-L275)
[^update-global-check]: [`src/update.ts`: global hash and clone checks](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/update.ts#L478-L640)
[^update-global-apply]: [`src/update.ts`: changed global skill application](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/update.ts#L667-L724)
[^update-well-known]: [`src/update.ts`: well-known digest checks and application](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/update.ts#L309-L475)
[^update-deletion]: [`src/update.ts`: interactive-only upstream deletion](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/update.ts#L256-L307)
[^update-project]: [`src/update.ts`: project refresh and child add invocation](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/update.ts#L727-L935)
[^update-legacy]: [`src/update.ts`: legacy project entries](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/update.ts#L938-L957)
[^remove]: [`src/remove.ts`: removal implementation](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/remove.ts#L61-L380)
[^remove-scan]: [`src/remove.ts`: canonical/harness scan and stale lock keys](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/remove.ts#L92-L151)
[^remove-options]: [`src/remove.ts`: option parser](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/remove.ts#L382-L429)
[^remove-canonical]: [`src/remove.ts`: target cleanup, canonical retention, and lock cleanup](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/remove.ts#L235-L335)
[^cli-dispatch]: [`src/cli.ts`: command dispatcher and aliases](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/cli.ts#L299-L409)
[^cli-help]: [`src/cli.ts`: complete help and options](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/cli.ts#L105-L198)
[^list-json]: [`src/list.ts`: list scope and JSON schema](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/list.ts#L55-L128)
[^readme-list]: [CLI README: list documentation](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/README.md#L144-L157)
[^readme-use]: [CLI README: stdout behavior of `use`](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/README.md#L17-L26)
[^add-exit]: [`src/add.ts`: printed per-target failures without failure status](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/add.ts#L2035-L2067)
[^remove-exit]: [`src/remove.ts`: printed per-skill failures without failure status](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/remove.ts#L338-L380)
[^update-check-exit]: [`src/update.ts`: caught global check failure](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/update.ts#L635-L639)
[^update-final-exit]: [`src/update.ts`: final update failure status](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/update.ts#L997-L1008)
[^installer-tests]: [Pinned Windows-relevant installer tests](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/tests/installer-symlink.test.ts)
[^path-tests]: [Pinned cross-platform path tests](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/tests/cross-platform-paths.test.ts)
