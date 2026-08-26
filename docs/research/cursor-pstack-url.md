# Why can't `skills@latest` reliably install Cursor pstack skills?

Research date: 2026-08-26

Acceptance URL: <https://github.com/cursor/plugins/tree/main/pstack/skills>

## Result

The acceptance URL is valid input for the released `skills` CLI. On the research date, npm's `latest` dist-tag resolved to `skills@1.5.23`; that immutable package identifies source commit `435076e78988e1e6ec40d00b0b1d76bdbbc5419a` and requires Node `>=22.20.0` ([npm version metadata](https://registry.npmjs.org/skills/1.5.23), [dist-tags observed on 2026-08-26](https://registry.npmjs.org/-/package/skills/dist-tags), [package source](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/package.json#L1-L5), [engine declaration](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/package.json#L142-L145)). Its documentation explicitly supports a GitHub `/tree/<ref>/<path>` URL ([source formats](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/README.md#L28-L48)).

The reported general failure was not reproduced on Windows with `1.5.23`. Five isolated clone/discovery attempts all found 44 valid skills. Listing, one ordinary selected install, the specially named selected install, and an all-skills install exited 0. The all-skills command installed 44 folders containing 44 `SKILL.md` files.

One deterministic selection failure was reproduced: `--skill poteto-mode` exits 1 even though `pstack/skills/poteto-mode/SKILL.md` exists. That file declares `name: Poteto Mode` ([pinned file](https://github.com/cursor/plugins/blob/bdf7aa355337897f167153e05069aca505dae17c/pstack/skills/poteto-mode/SKILL.md#L1-L4)); the clone-backed selection code compares the requested text with the frontmatter/display name case-insensitively but does not normalize spaces to hyphens ([`filterSkills`](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/skills.ts#L323-L339), [selection flow](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/add.ts#L1296-L1317)). `--skill "Poteto Mode"` succeeds and installs it as `poteto-mode`.

The remaining reliability concern is acquisition topology, not parsing or discovery. `1.5.23` converts the URL to repository `https://github.com/cursor/plugins.git`, ref `main`, and subpath `pstack/skills`, then shallow-clones and checks out the whole repository before looking below that subpath. It has no sparse/path-only clone for this source. The optimized tree/snapshot downloader is limited to three owner names plus explicitly configured repositories, does not include `cursor`, and is bypassed whenever an explicit ref is present ([add path](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/add.ts#L1182-L1230), [explicit-ref bypass](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/blob.ts#L497-L531), [clone implementation](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/git.ts#L235-L299)). Therefore npm resolution, the package's Node floor, network access, Git availability, a mutable branch, checkout, timeout, credentials, and the entire repository checkout all precede selection. Those are real intermittent boundaries, but no one of them can be identified as the user's historical failure without its original command, versions, and stderr.

## Pinned source tree

`main` resolved during research to commit [`bdf7aa355337897f167153e05069aca505dae17c`](https://github.com/cursor/plugins/commit/bdf7aa355337897f167153e05069aca505dae17c). The accepted directory at that revision is [pinned here](https://github.com/cursor/plugins/tree/bdf7aa355337897f167153e05069aca505dae17c/pstack/skills). Its Git tree is [`d0a80048834b1a7dcea9bea4a69320ddd130ae5c`](https://api.github.com/repos/cursor/plugins/git/trees/d0a80048834b1a7dcea9bea4a69320ddd130ae5c?recursive=1): 181 recursive entries, `truncated: false`, including 121 blobs and 44 `SKILL.md` blobs. The parent plugin manifest declares `"skills": "./skills/"` ([manifest](https://github.com/cursor/plugins/blob/bdf7aa355337897f167153e05069aca505dae17c/pstack/.cursor-plugin/plugin.json#L1-L29)).

These are the exact valid `SKILL.md` entries. Every entry has string `name` and `description` frontmatter, as demonstrated by `skills@1.5.23 --list`; all folder names equal the declared name except `poteto-mode`, whose declared name is `Poteto Mode`.

```text
pstack/skills/architect/SKILL.md
pstack/skills/arena/SKILL.md
pstack/skills/automate-me/SKILL.md
pstack/skills/blast-radius/SKILL.md
pstack/skills/bro/SKILL.md
pstack/skills/create-verification-skill/SKILL.md
pstack/skills/figure-it-out/SKILL.md
pstack/skills/how/SKILL.md
pstack/skills/interrogate/SKILL.md
pstack/skills/maintain-verification-skill/SKILL.md
pstack/skills/no-comments/SKILL.md
pstack/skills/poteto-mode/SKILL.md                 name: Poteto Mode
pstack/skills/principle-boundary-discipline/SKILL.md
pstack/skills/principle-build-the-lever/SKILL.md
pstack/skills/principle-encode-lessons-in-structure/SKILL.md
pstack/skills/principle-exhaust-the-design-space/SKILL.md
pstack/skills/principle-experience-first/SKILL.md
pstack/skills/principle-fix-root-causes/SKILL.md
pstack/skills/principle-foundational-thinking/SKILL.md
pstack/skills/principle-guard-the-context-window/SKILL.md
pstack/skills/principle-laziness-protocol/SKILL.md
pstack/skills/principle-make-operations-idempotent/SKILL.md
pstack/skills/principle-migrate-callers-then-delete-legacy-apis/SKILL.md
pstack/skills/principle-minimize-reader-load/SKILL.md
pstack/skills/principle-model-the-domain/SKILL.md
pstack/skills/principle-never-block-on-the-human/SKILL.md
pstack/skills/principle-outcome-oriented-execution/SKILL.md
pstack/skills/principle-prove-it-works/SKILL.md
pstack/skills/principle-redesign-from-first-principles/SKILL.md
pstack/skills/principle-separate-before-serializing-shared-state/SKILL.md
pstack/skills/principle-sequence-verifiable-units/SKILL.md
pstack/skills/principle-subtract-before-you-add/SKILL.md
pstack/skills/principle-type-system-discipline/SKILL.md
pstack/skills/recall/SKILL.md
pstack/skills/reflect/SKILL.md
pstack/skills/setup-pstack/SKILL.md
pstack/skills/show-me-your-work/SKILL.md
pstack/skills/swarm/SKILL.md
pstack/skills/tdd/SKILL.md
pstack/skills/teach/SKILL.md
pstack/skills/technical-writing/SKILL.md
pstack/skills/typescript-best-practices/SKILL.md
pstack/skills/unslop/SKILL.md
pstack/skills/why/SKILL.md
```

The folder payload is not just 44 entrypoint files. File counts by skill are: `poteto-mode` 45; `why` 13; `how`, `interrogate`, and `reflect` 5 each; `architect` and `create-verification-skill` 4 each; `show-me-your-work` 3; `typescript-best-practices` 2; every other skill 1. Five scripts have Git mode `100755`, so a downloader must retain executable intent even though Windows does not enforce the Unix execute bit. The pinned recursive tree above is the exact machine-readable inventory.

## What `skills@1.5.23` does

### Parse and ref/path resolution

The parser's GitHub tree-with-path rule captures the first path segment after `/tree/` as the ref and the rest as subpath ([parser](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/source-parser.ts#L351-L383)). For this URL, that is unambiguous:

```text
type      github
url       https://github.com/cursor/plugins.git
ref       main
subpath   pstack/skills
```

Branch names containing `/` are ambiguous in a `/tree/...` URL and are intentionally parsed as first-segment ref plus remaining subpath; the released tests document that limitation ([tests](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/tests/source-parser.test.ts#L45-L70)). It does not affect ref `main`. The supported shorthand `cursor/plugins/pstack/skills#main` separates path from ref and avoids that general ambiguity ([parser tests](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/tests/source-parser.test.ts#L205-L219)).

### Acquisition and authentication

Because `cursor` is not eligible for the snapshot optimization and `main` is explicit, the CLI invokes:

```text
git clone --depth 1 --branch main https://github.com/cursor/plugins.git <temporary-directory>
```

There is no sparse-checkout option in `cloneRepo`; subpath is applied only after clone ([clone source](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/git.ts#L235-L247), [post-clone discovery](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/add.ts#L1205-L1230)). At the pinned revision the full repository had 478 tracked files; the research partial clone that checked out all files held a 3.96 MiB object pack. This is not currently a huge repository, but it is still an avoidable whole-repository/network/Git dependency.

For GitHub HTTPS, normal Git credentials are first; only an authentication-classified clone error triggers authenticated `gh repo clone`, then SSH ([implementation](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/git.ts#L177-L203), [fallback sequence](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/git.ts#L244-L299), [documented behavior](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/README.md#L50-L70)). This public repository needed no authentication in the isolated runs.

GitHub tree lookups separately try anonymous REST, then explicit `GITHUB_TOKEN`/`GH_TOKEN`, then `gh api` when a 403 rate limit or anonymous 401/404 makes authentication useful ([source](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/blob.ts#L110-L275), [auth tests](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/tests/blob-fetch-tree-auth.test.ts#L93-L175)). Those lookups are not the source acquisition path for this URL in `1.5.23`.

### Discovery

After clone, discovery sets `searchPath` to `<clone>/pstack/skills`, examines each direct child for `SKILL.md`, parses YAML, and requires string `name` and `description` ([validation](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/skills.ts#L78-L130), [walk](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/skills.ts#L176-L320)). This flat topology is within the default search; `--full-depth` is unnecessary. The CLI's documented bounded discovery and `--full-depth` behavior are at [README lines 400-408](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/README.md#L400-L408), with corresponding nested-layout tests at [`nested-container-discovery.test.ts`](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/tests/nested-container-discovery.test.ts#L37-L159).

No parser, ref, subpath, discovery, YAML, topology-shadowing, or authentication failure occurred for the exact URL.

### Selection and install

`--skill '*'` selects every discovered skill; `--skill <name>` otherwise uses the declared/display name ([selection](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/add.ts#L1296-L1326)). That explains the only reproduced failure:

```text
--skill poteto-mode       -> exit 1, No matching skills found for: poteto-mode
--skill "Poteto Mode"     -> exit 0, installs .agents/skills/poteto-mode
```

Once selected, clone-backed install copies the whole skill folder, including references, playbooks, and scripts ([install path](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/add.ts#L1750-L1773)). The project lock records the mutable `main` ref, exact `skillPath`, and a deterministic recursive content hash; it does not pin the installed source commit ([lock write](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/add.ts#L1900-L1933), [hash algorithm](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/src/local-lock.ts#L140-L180)).

## Isolated Windows reproductions

All commands ran under `C:\Users\THOMAS~1\AppData\Local\Temp\opencode\skills-repro-1.5.23-5d2f7c8e`. Before executing `npx`, the command asserted that every user/config/cache/temp path started with that root. It also disabled global/system Git configuration, credential prompts, GitHub CLI prompts, and telemetry. No real `~/.agents`, `~/.claude`, `~/.cursor`, or other user skill path was read or written.

The reusable PowerShell 5.1 isolation prelude was:

```powershell
$root = 'C:\Users\THOMAS~1\AppData\Local\Temp\opencode\skills-repro-1.5.23-5d2f7c8e'
if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw 'Isolation root missing' }

$env:USERPROFILE = "$root\home"
$env:HOME = $env:USERPROFILE
$env:APPDATA = "$root\appdata"
$env:LOCALAPPDATA = "$root\localappdata"
$env:XDG_CONFIG_HOME = "$root\home\.config"
$env:XDG_DATA_HOME = "$root\home\.local\share"
$env:GH_CONFIG_DIR = "$root\gh-config"
$env:npm_config_cache = "$root\npm-cache"
$env:npm_config_userconfig = "$root\home\.npmrc"
$env:TEMP = "$root\tmp"
$env:TMP = $env:TEMP
$env:GIT_CONFIG_GLOBAL = 'NUL'
$env:GIT_CONFIG_NOSYSTEM = '1'
$env:GIT_TERMINAL_PROMPT = '0'
$env:GCM_INTERACTIVE = 'Never'
$env:GH_PROMPT_DISABLED = '1'
$env:DISABLE_TELEMETRY = '1'
$env:NO_UPDATE_NOTIFIER = '1'

foreach ($path in @(
  $env:USERPROFILE, $env:HOME, $env:APPDATA, $env:LOCALAPPDATA,
  $env:XDG_CONFIG_HOME, $env:XDG_DATA_HOME, $env:GH_CONFIG_DIR,
  $env:npm_config_cache, $env:TEMP, $env:TMP
)) {
  if (-not $path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unisolated path: $path"
  }
}
'ISOLATION_VERIFIED=True'
```

Runtime and list:

```powershell
node --version
npm --version
npx --yes skills@1.5.23 add "https://github.com/cursor/plugins/tree/main/pstack/skills" --list
```

Observed:

```text
ISOLATION_VERIFIED=True
v24.17.0
11.13.0
Source: https://github.com/cursor/plugins.git @ main (pstack/skills)
Repository cloned
Found 44 skills
EXIT_CODE=0
```

Ordinary selected install:

```powershell
npx --yes skills@1.5.23 add "https://github.com/cursor/plugins/tree/main/pstack/skills" `
  --skill architect --agent cursor --copy -y
```

Observed: `Selected 1 skill: architect`, `Installed 1 skill`, exit 0, with `SKILL.md` and all three `references/*.md` files under `.agents/skills/architect`.

Selection mismatch and working spelling:

```powershell
npx --yes skills@1.5.23 add "https://github.com/cursor/plugins/tree/main/pstack/skills" `
  --skill poteto-mode --agent cursor --copy -y
# exit 1: No matching skills found for: poteto-mode

npx --yes skills@1.5.23 add "https://github.com/cursor/plugins/tree/main/pstack/skills" `
  --skill "Poteto Mode" --agent cursor --copy -y
# exit 0: .agents/skills/poteto-mode contains SKILL.md, playbooks/, references/, scripts/
```

All skills, using the documented wildcard option ([options and examples](https://github.com/vercel-labs/skills/blob/435076e78988e1e6ec40d00b0b1d76bdbbc5419a/README.md#L72-L109)):

```powershell
npx --yes skills@1.5.23 add "https://github.com/cursor/plugins/tree/main/pstack/skills" `
  --skill '*' --agent cursor --copy -y
```

Observed:

```text
Found 44 skills
Installing all 44 skills
Installed 44 skills
EXIT_CODE=0
INSTALLED_DIRECTORY_COUNT=44
INSTALLED_SKILL_MD_COUNT=44
```

This is a currently supported CLI command that reliably handled the exact URL in the isolated environment. Pinning `@1.5.23` makes the CLI version reproducible; replacing it with `@latest` deliberately does not.

## Capability boundaries

| Mechanism | What it can do for this URL | What it does not do |
|---|---|---|
| `skills@1.5.23` | Parse the exact URL; shallow-clone ref `main`; scope discovery to `pstack/skills`; validate 44 skills; select by declared name; install complete selected folders; record path/ref/content hash; check for updates. | It does not sparse-clone `pstack/skills`, pin a project install to the resolved commit, or select `Poteto Mode` by folder slug `poteto-mode`. For this owner/ref it does not download only selected folders. |
| Authenticated `gh` / GitHub REST | Resolve `main` to a commit; traverse trees; detect truncation; fetch blobs by immutable SHA; access private repositories according to token permissions. `gh api` uses its own stored credential ([`gh api`](https://cli.github.com/manual/gh_api)); `gh repo clone` delegates acquisition to Git ([`gh repo clone`](https://cli.github.com/manual/gh_repo_clone)). | It provides repository objects, not skill semantics, frontmatter validation, selection policy, safe materialization, installation paths, or provenance management. REST recursive trees must be checked for `truncated`; GitHub documents 100,000-entry/7-MB limits and subtree traversal when truncated ([Get a tree](https://docs.github.com/en/rest/git/trees#get-a-tree)). |
| Git | Resolve refs, shallow/partial clone, sparse-checkout a path, detach at an immutable commit, enumerate recursively, preserve modes, and compare `refs/heads/main` with `git ls-remote`. | It does not know that `SKILL.md` plus valid frontmatter defines a Skill, normalize selections, or install/expose skills. |
| Custom Skilly logic | Could combine deterministic parsing, path-bounded recursive discovery, frontmatter/folder-name selection, immutable provenance, selected-folder materialization, and an API-to-sparse-Git fallback. | No such implementation exists on the researched Skilly `origin/main`: commit [`f1177e08ffa384aa3f2343cfe8544759c4c63473`](https://github.com/berghtho/skilly/tree/f1177e08ffa384aa3f2343cfe8544759c4c63473) contains only repository guidance/domain documents. The domain calls this responsibility a Source Provider and provenance ([`CONTEXT.md`](https://github.com/berghtho/skilly/blob/f1177e08ffa384aa3f2343cfe8544759c4c63473/CONTEXT.md#L31-L37)). This research does not choose a product design. |

## Smallest path-aware acquisition established

The following is the smallest technically sufficient path-aware flow demonstrated for this exact URL. It is a research result, not a product decision.

1. Parse only the recognized GitHub shape into owner `cursor`, repo `plugins`, requested ref `main`, and bounded path `pstack/skills`. Reject traversal and ambiguous/malformed forms. The shorthand `cursor/plugins/pstack/skills#main` is an equivalent supported CLI representation when path/ref separation is needed.
2. Resolve `main` once to immutable commit `bdf7aa355337897f167153e05069aca505dae17c` using `GET /repos/cursor/plugins/commits/main`, authenticated through an explicit token or `gh api` when needed. All subsequent reads use that commit or object SHAs, never moving `main`.
3. Obtain the `skills` tree SHA without recursively listing the whole repository: `GET /repos/cursor/plugins/contents/pstack?ref=<commit>` returns `d0a80048834b1a7dcea9bea4a69320ddd130ae5c` for its `skills` entry. GitHub's Contents API accepts a `ref` and identifies each directory by SHA ([Get repository content](https://docs.github.com/en/rest/repos/contents#get-repository-content)).
4. Recursively read only `GET /repos/cursor/plugins/git/trees/d0a80048834b1a7dcea9bea4a69320ddd130ae5c?recursive=1`. Require `truncated == false`; this exact response had 181 entries and 44 `*/SKILL.md` blobs. If truncated, recursively traverse non-recursive subtrees as GitHub documents rather than accepting an incomplete catalog.
5. Fetch and validate each discovered entrypoint, retaining both relative folder path and declared frontmatter name. Selection can then match either an exact declared name or exact folder-relative identifier, making `Poteto Mode` and `poteto-mode` unambiguous aliases without fuzzy matching.
6. For each chosen folder tree SHA, request only that tree recursively, validate it is not truncated and every returned path stays below the chosen folder, then fetch each blob by SHA. Recompute Git's `sha1("blob <length>\0" + bytes)` before materializing it and preserve tree mode. Store requested ref, resolved commit, container tree SHA, selected relative folder, selected folder tree SHA, and a deterministic payload hash as provenance.
7. For update checks, resolve `main` again. If its commit differs, traverse to the latest selected folder tree SHA; unchanged folder SHA means no download despite unrelated repository commits. A changed folder SHA triggers the same immutable selected-folder fetch.

The authenticated REST approach is path-minimal but consumes one API request per downloaded blob. Anonymous GitHub REST is rate-limited and can be insufficient for validating 44 entrypoints plus downloading the 45-file `poteto-mode`; authenticated REST/`gh api`, pinned raw public URLs, or Git fallback is required for predictable capacity. Download URLs from the Contents API are temporary; GitHub explicitly says to obtain a fresh URL for each download ([Contents API notes](https://docs.github.com/en/rest/repos/contents#get-repository-content)).

### Direct selected-folder reproduction

The REST flow was exercised for `architect` without writing user files. It traversed the pinned container tree, selected folder tree `0eb5abdc7af97bfd9b9d15b8d9197206e4b88fcd`, fetched all four Git blobs, decoded them, and recomputed every Git blob SHA.

The exact PowerShell 5.1 resolution and discovery commands were:

```powershell
$headers = @{
  Accept = 'application/vnd.github+json'
  'X-GitHub-Api-Version' = '2022-11-28'
  'User-Agent' = 'skilly-research'
}
$commit = Invoke-RestMethod `
  -Uri 'https://api.github.com/repos/cursor/plugins/commits/main' -Headers $headers
$pstack = Invoke-RestMethod `
  -Uri ("https://api.github.com/repos/cursor/plugins/contents/pstack?ref=" + $commit.sha) `
  -Headers $headers
$skillsEntry = @($pstack) | Where-Object { $_.name -eq 'skills' -and $_.type -eq 'dir' }
$tree = Invoke-RestMethod `
  -Uri ("https://api.github.com/repos/cursor/plugins/git/trees/" +
    $skillsEntry.sha + '?recursive=1') -Headers $headers
$skillEntries = @($tree.tree) | Where-Object {
  $_.type -eq 'blob' -and $_.path -match '(^|/)SKILL\.md$'
}

"RESOLVED_COMMIT=$($commit.sha)"
"PSTACK_SKILLS_TREE=$($skillsEntry.sha)"
"SUBTREE_TRUNCATED=$($tree.truncated)"
"SUBTREE_ENTRY_COUNT=$(@($tree.tree).Count)"
"SUBTREE_SKILL_MD_COUNT=$($skillEntries.Count)"
```

Observed:

```text
RESOLVED_COMMIT=bdf7aa355337897f167153e05069aca505dae17c
PSTACK_SKILLS_TREE=d0a80048834b1a7dcea9bea4a69320ddd130ae5c
SUBTREE_TRUNCATED=False
SUBTREE_ENTRY_COUNT=181
SUBTREE_SKILL_MD_COUNT=44
```

The selected-folder command then read the container tree non-recursively, found its `architect` tree entry, read that tree recursively, and fetched each returned blob from `https://api.github.com/repos/cursor/plugins/git/blobs/<blob-sha>`. For each response it removed Base64 whitespace, decoded `content`, and verified the response against the entry SHA using this PowerShell 5.1 calculation:

```powershell
$prefix = [Text.Encoding]::ASCII.GetBytes("blob $($bytes.Length)`0")
$joined = New-Object byte[] ($prefix.Length + $bytes.Length)
[Array]::Copy($prefix, 0, $joined, 0, $prefix.Length)
[Array]::Copy($bytes, 0, $joined, $prefix.Length, $bytes.Length)
$hasher = [Security.Cryptography.SHA1]::Create()
try { $digest = $hasher.ComputeHash($joined) } finally { $hasher.Dispose() }
$actualSha = ([BitConverter]::ToString($digest)).Replace('-', '').ToLowerInvariant()
if ($actualSha -ne $entry.sha) { throw "Blob hash mismatch: $($entry.path)" }
```

```text
DOWNLOADED 100644 9c5dd1b5ab5d068fbacc60ed48d6603af0c237c3 6061 architect/SKILL.md
DOWNLOADED 100644 32cb24083f308fd67ab4c431b44e1d7d3563435a 1944 architect/references/design-red-flags.md
DOWNLOADED 100644 1ddd50545c977264ac387ab805afbef257df03b9 3050 architect/references/rationale-template.md
DOWNLOADED 100644 d2daeee48c50f31825fdb0645e0df05c08e37bb6 3091 architect/references/runner-prompt.md
PINNED_COMMIT=bdf7aa355337897f167153e05069aca505dae17c
SELECTED_FOLDER_TREE=0eb5abdc7af97bfd9b9d15b8d9197206e4b88fcd
SELECTED_TREE_TRUNCATED=False
VERIFIED_BLOBS=4
TOTAL_BYTES=14146
```

### Git fallback reproduction

Git partial clone plus sparse checkout avoids REST request/rate-limit dependence while acquiring only the accepted path. Git documents sparse-checkout as restricting the working tree to tracked files of interest ([`git sparse-checkout`](https://git-scm.com/docs/git-sparse-checkout)) and partial clone as deferring unneeded objects ([partial clone](https://git-scm.com/docs/partial-clone)).

```powershell
git clone --filter=blob:none --no-checkout --depth 1 --branch main `
  https://github.com/cursor/plugins.git .\sparse-pstack
git -C .\sparse-pstack sparse-checkout set pstack/skills
git -C .\sparse-pstack checkout --detach
git -C .\sparse-pstack rev-parse HEAD
git -C .\sparse-pstack rev-parse HEAD:pstack/skills
git -C .\sparse-pstack ls-tree -r --name-only HEAD -- pstack/skills
git ls-remote https://github.com/cursor/plugins.git refs/heads/main
```

Observed in the isolated environment:

```text
PINNED_COMMIT=bdf7aa355337897f167153e05069aca505dae17c
PSTACK_SKILLS_TREE=d0a80048834b1a7dcea9bea4a69320ddd130ae5c
DISCOVERED_SKILL_MD_COUNT=44
CHECKED_OUT_FILE_COUNT=121
PACK_SIZE=292.88 KiB
LS_REMOTE_MAIN=bdf7aa355337897f167153e05069aca505dae17c
```

This is the smallest reliable fallback established when selected-blob API acquisition is unavailable: partial shallow clone, sparse-checkout only `pstack/skills`, detach and record the resolved commit, recursively discover locally, and copy only selected validated folders. It downloaded a 292.88-KiB pack in this run versus 3.96 MiB for the research checkout of the full repository.

## Failure classification

| Stage | Finding for the acceptance URL |
|---|---|
| npm/package | `@latest` was `1.5.23` on 2026-08-26, but `latest` is mutable and therefore not reproducible. `1.5.23` requires Node `>=22.20.0`. |
| Parser | Pass. Exact owner/repo/ref/subpath extracted. General slash-containing branch ambiguity exists but does not affect `main`. |
| Ref/path | Pass. `main` existed and `pstack/skills` existed. The branch is mutable unless separately resolved and recorded. |
| Acquisition/topology | Pass in five attempts, but unnecessarily clones/checks out the whole repository. This is the principal intermittent-risk surface. |
| Discovery | Pass. 44/44 valid direct-child skills found; `--full-depth` is not needed. |
| Selection | One exact defect reproduced: folder slug `poteto-mode` does not select declared name `Poteto Mode`; quoted declared name or wildcard succeeds. |
| Install | Pass. Selected folder support files and all 44 wildcard-selected folders copied correctly to an isolated project target. |
| Authentication | Not needed for this public source. Isolated runs intentionally exposed no real Git/gh credentials. CLI fallback behavior is documented and tested, but private/SSO behavior was not exercised against this public repository. |
| Update/provenance | CLI project lock retains `main`, path, and content hash, not resolved commit. GitHub folder tree SHA or payload hash can suppress unrelated updates, but immutable commit provenance must be added outside the current project lock. |

## Unresolved caveats

- There are no original failing command lines, stderr, Node/npm/Git versions, network conditions, or selected skill names. The historical intermittent report cannot be attributed more narrowly than the demonstrated boundaries.
- Results pin the Cursor repository as it existed at `bdf7aa355337897f167153e05069aca505dae17c`. The acceptance URL points to mutable `main`; its tree and valid-entry count can change.
- Reproduction used Node `24.17.0`, npm `11.13.0`, Git for Windows, and public anonymous Git access. The declared Node minimum was not separately exercised.
- GitHub REST anonymous rate limits, private-repository permissions, SAML SSO, GitHub Enterprise differences, recursive-tree truncation, transient API/Git failures, proxies, and Windows path-length policy remain environment-dependent.
- A custom downloader must additionally define safe behavior for symlinks, submodules, case-colliding paths, reserved Windows names, executable modes, size/file-count limits, and interrupted atomic installs. This pstack revision has regular blobs only, including five `100755` files, so those broader cases were not all present to reproduce.
- `skills@1.5.23` update behavior was inspected in released source/tests and its project lock was observed, but an upstream pstack mutation was not manufactured; no live update was performed against the real repository.
