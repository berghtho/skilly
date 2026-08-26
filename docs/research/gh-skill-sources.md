# GitHub CLI as a source for GitHub-hosted skills

Research date: 2026-08-26

## Executive summary

An authenticated GitHub CLI is sufficient for almost all repository-side work in a skill source flow, but it does not turn an arbitrary GitHub browser URL into a structured repository, ref, and subdirectory. Use `gh repo read-dir`, `gh repo read-file`, and `gh skill` when their preview status is acceptable; use `gh api` when stable JSON and explicit REST semantics matter; and use Git sparse checkout for an arbitrary selected folder. Resolve every movable ref to a commit SHA before inspecting or downloading content, and retain the skill directory's tree SHA for cheap, content-specific update detection.

The strongest implementation split is:

| Need | Recommended mechanism | Boundary |
| --- | --- | --- |
| Authenticate to public and private repositories | Existing `gh` authentication | Direct `gh` support |
| Parse `https://github.com/OWNER/REPO` | Repository selector accepted by `gh` | Direct `gh` support |
| Parse `/tree/REF/PATH` or `/blob/REF/PATH` URLs | Validate and split the URL, then resolve the ambiguous ref/path boundary | Custom logic plus API calls |
| Read repository/default-branch metadata | `gh api repos/OWNER/REPO` or `gh repo view --json` | Direct `gh` support |
| Enumerate branches and resolve refs | REST endpoints through `gh api` | API composition |
| List a known directory or read one file | `gh repo read-dir` / `gh repo read-file` | Direct preview commands |
| Discover skills for a person | `gh skill install OWNER/REPO` without a selection | Direct preview command, table/TSV output |
| Discover skills for software | Recursive Git tree plus `SKILL.md` validation | API composition and custom parsing |
| Download one conforming skill | `gh skill install OWNER/REPO PATH --pin COMMIT_SHA --dir TARGET` | Direct preview command |
| Download an arbitrary folder | `gh repo clone` plus Git sparse checkout at a commit SHA | Direct `gh` authentication plus Git |
| Download the entire repository snapshot | REST zipball/tarball endpoint | API composition plus archive extraction |
| Detect a skill update | Compare the remote directory tree SHA with the stored tree SHA | Direct `gh skill update` or API composition |
| Display a last revision date | First commit returned by `commits?sha=...&path=...` | API composition; advisory, not identity |

## Inspected state

The commands and implementation details in this note were checked with:

- GitHub CLI `2.98.0`, released 2026-08-20 ([release](https://github.com/cli/cli/releases/tag/v2.98.0)), at source commit [`a255baf71d13fe5947a4eb7ad521ffd412d64cee`](https://github.com/cli/cli/commit/a255baf71d13fe5947a4eb7ad521ffd412d64cee).
- Git `2.55.0.windows.5` on Windows.
- GitHub REST API documentation as of 2026-08-26. The examples explicitly request API version `2026-03-10`; requests without a version still default to `2022-11-28`, which is supported until 2028-03-10 ([API versions](https://docs.github.com/en/rest/about-the-rest-api/api-versions)).
- Agent Skills specification as of 2026-08-26 ([specification](https://agentskills.io/specification)).

`gh skill`, `gh repo read-dir`, and `gh repo read-file` are marked preview and may change without notice ([skill install manual](https://cli.github.com/manual/gh_skill_install), [read-dir manual](https://cli.github.com/manual/gh_repo_read-dir), [read-file manual](https://cli.github.com/manual/gh_repo_read-file)). Pinning an automation contract to the REST API and treating these commands as replaceable adapters limits that risk.

## Authentication and host support

`gh api`, `gh repo clone`, the repository read commands, and `gh skill` reuse the active `gh` login. Verify it before doing work:

```powershell
gh auth status
if ($LASTEXITCODE -ne 0) { throw 'GitHub CLI authentication is unavailable' }
```

Interactive login and credential storage are covered by [`gh auth login`](https://cli.github.com/manual/gh_auth_login). If raw `git` commands must share the same credential setup, run `gh auth setup-git` once ([manual](https://cli.github.com/manual/gh_auth_setup-git)). Do not print or persist `gh auth token` unless another HTTP client truly needs it.

GitHub's skill commands support `github.com` and GitHub Enterprise Cloud data-residency hosts, but reject GitHub Enterprise Server in `2.98.0` ([host validation source](https://github.com/cli/cli/blob/v2.98.0/internal/skills/source/source.go#L53-L67)). General `gh api` and repository commands can target a configured host with `--hostname`; that broader host support should not be assumed for `gh skill`.

## Repository URLs and subdirectory URLs

### Canonical repository URLs

Repository selectors accept `OWNER/REPO`, `HOST/OWNER/REPO`, or a full repository URL. In `2.98.0`, the URL parser requires exactly two path segments after the host and strips a trailing `.git` ([repository parser](https://github.com/cli/cli/blob/v2.98.0/internal/ghrepo/repo.go#L44-L71), [skill install parser](https://github.com/cli/cli/blob/v2.98.0/pkg/cmd/skills/install/install.go#L580-L598)). These are valid selectors:

```text
github/awesome-copilot
github.com/github/awesome-copilot
https://github.com/github/awesome-copilot
```

### Browser tree/blob URLs require custom parsing

Neither `gh skill install` nor `gh repo view` accepts a browser URL with extra path segments such as:

```text
https://github.com/github/awesome-copilot/tree/main/skills/git-commit
```

Both commands rejected that form in an authenticated `2.98.0` smoke test, consistently with the two-segment parser above. Parse user-supplied browser URLs at the application boundary, not inside command strings:

1. Parse with a URI library, not a regular expression.
2. Require HTTPS, an expected GitHub host, no user information, and at least `OWNER/REPO` path segments.
3. Percent-decode each path segment once; reject empty, `.` and `..` segments and NUL/control characters.
4. Strip `.git` only from the repository segment.
5. Accept no suffix, or a suffix beginning with `tree` or `blob`; reject unrelated route types.
6. Treat the segments after `tree`/`blob` as ambiguous until checked against repository refs.

The ambiguity matters because refs can contain `/`: in `/tree/feature/windows/skills/foo`, the branch might be `feature`, `feature/windows`, or even `feature/windows/skills`. Evaluate candidate prefixes from longest to shortest; for each candidate, try an exact branch, then an exact tag, and finally a commit SHA. Select the first valid ref and use the remaining segments as the path. If no split resolves, fail closed. If the producer controls the input format, avoid this process entirely by storing separate `repository`, `ref`, and `path` fields; GitHub itself advises consumers not to infer structured fields from API-returned URLs ([REST best practices](https://docs.github.com/en/rest/using-the-rest-api/best-practices-for-using-the-rest-api#do-not-manually-parse-urls)).

After parsing, resolve the ref and use the resulting commit SHA for every subsequent request. That prevents a branch or tag moving between discovery and download.

## Repository metadata and refs

The following PowerShell examples preserve JSON on stdout and avoid shell-dependent quoting tricks:

```powershell
$repo = 'github/awesome-copilot'
$apiVersion = '2026-03-10'
$apiHeader = "X-GitHub-Api-Version: $apiVersion"

# Repository metadata, including default_branch, visibility, pushed_at and updated_at.
$repository = gh api "repos/$repo" -H $apiHeader | ConvertFrom-Json
$defaultBranch = $repository.default_branch

# Every branch. --slurp returns one JSON array containing the page arrays.
$branchPagesJson = gh api --paginate --slurp "repos/$repo/branches?per_page=100" -H $apiHeader

# Resolve a branch, tag or commit expression to an immutable commit SHA.
$ref = $defaultBranch
$encodedRef = [Uri]::EscapeDataString($ref)
$commit = gh api "repos/$repo/commits/$encodedRef" -H $apiHeader | ConvertFrom-Json
$commitSha = $commit.sha
```

The repository endpoint defines `default_branch`, `pushed_at`, and `updated_at` ([repository endpoint](https://docs.github.com/en/rest/repos/repos#get-a-repository)). Branch enumeration is paginated and returns each branch's head commit SHA ([branches endpoint](https://docs.github.com/en/rest/branches/branches#list-branches)). The commit endpoint accepts a SHA, `heads/BRANCH_NAME`, or `tags/TAG_NAME` and returns the resolved commit ([get a commit](https://docs.github.com/en/rest/commits/commits#get-a-commit)). If branch/tag precedence matters, query exact Git refs separately ([get a reference](https://docs.github.com/en/rest/git/refs#get-a-reference)); annotated tags require dereferencing the tag object to its commit ([get a tag](https://docs.github.com/en/rest/git/tags#get-a-tag)).

`gh skill` has its own version policy. With no explicit version, `2.98.0` uses the latest release tag and falls back to the default branch only when no usable release exists. An explicit short name is tried as a branch, then a tag, then a commit; annotated tags are peeled to their commit ([resolution source](https://github.com/cli/cli/blob/v2.98.0/internal/skills/discovery/discovery.go#L205-L305)). This is not the same as always using the repository's default branch, so callers that require a particular snapshot should pass `--pin COMMIT_SHA`.

## Reading repository content

### Known directory

```powershell
gh repo read-dir 'skills' --repo $repo --ref $commitSha `
  --json name,path,type,gitSHA,size,modeOctal,submodule
```

The JSON envelope includes the selected directory's `gitSHA` and `id`, plus `entries`. Entry fields include path, normalized and Git types, mode, object SHA, size, and submodule metadata ([field list](https://github.com/cli/cli/blob/v2.98.0/pkg/cmd/repo/read-dir/read_dir.go#L17-L30), [JSON envelope](https://github.com/cli/cli/blob/v2.98.0/pkg/cmd/repo/read-dir/http.go#L62-L115)). The command uses GraphQL's `repository.object(expression:)`, avoids the REST Contents API's 1,000-entry directory cap, and distinguishes files, directories, symlinks, and submodules by Git mode ([implementation](https://github.com/cli/cli/blob/v2.98.0/pkg/cmd/repo/read-dir/http.go#L118-L220)). It lists one directory, not a recursive tree.

### Known file

```powershell
gh repo read-file 'skills/git-commit/SKILL.md' --repo $repo --ref $commitSha `
  --json name,path,type,gitSHA,size,encoding,content

gh repo read-file 'skills/git-commit/SKILL.md' --repo $repo --ref $commitSha `
  --output '.\download\SKILL.md'
```

JSON content is base64 and the other selectable fields include API, HTML, Git, and download URLs ([field list](https://github.com/cli/cli/blob/v2.98.0/pkg/cmd/repo/read-file/read_file.go#L20-L33), [encoding](https://github.com/cli/cli/blob/v2.98.0/pkg/cmd/repo/read-file/http.go#L32-L61)). The command uses the REST Contents API, switching to raw content when inline content is unavailable ([implementation](https://github.com/cli/cli/blob/v2.98.0/pkg/cmd/repo/read-file/http.go#L81-L198)). The underlying endpoint caps a directory response at 1,000 entries, provides inline behavior based on file size, and does not support files over 100 MB ([Contents API](https://docs.github.com/en/rest/repos/contents#get-repository-content)).

When displaying untrusted content, `read-file` rejects terminal escape sequences unless explicitly allowed. `--output` writes raw bytes and therefore intentionally bypasses that display protection ([command behavior](https://github.com/cli/cli/blob/v2.98.0/pkg/cmd/repo/read-file/read_file.go#L59-L76), [guard implementation](https://github.com/cli/cli/blob/v2.98.0/pkg/cmd/repo/read-file/read_file.go#L181-L209)).

## Discovering skills

An Agent Skill is a directory containing `SKILL.md`. Its YAML frontmatter requires `name` and `description`; the name must match the parent directory and obey the specification's naming rules ([Agent Skills specification](https://agentskills.io/specification)). Finding a file named `SKILL.md` is therefore candidate discovery, not validation.

### Human-oriented discovery

In a non-interactive pipeline, omitting a skill selection lists discovered skills rather than installing them:

```powershell
gh skill install $repo --pin $commitSha
```

The output is a table on a terminal and tab-separated name/description rows when redirected. There is no `--json` flag in `2.98.0` ([listing implementation](https://github.com/cli/cli/blob/v2.98.0/pkg/cmd/skills/install/install.go#L765-L800)). This is useful for people but should not be treated as a durable machine schema.

The command discovers standard, namespaced, nested-prefix, plugin, root, and optionally hidden-directory conventions. Its full-repository mode requests one recursive Git tree, records both the `SKILL.md` blob SHA and containing directory tree SHA, and rejects truncated results ([discovery implementation](https://github.com/cli/cli/blob/v2.98.0/internal/skills/discovery/discovery.go#L547-L644)). Exact-path installation avoids full discovery and verifies the directory and its `SKILL.md` directly ([path implementation](https://github.com/cli/cli/blob/v2.98.0/internal/skills/discovery/discovery.go#L704-L808)).

### Machine-oriented discovery

For a stable JSON contract, query the recursive tree at the already-resolved commit SHA, check `truncated`, identify candidate `SKILL.md` blobs, fetch them, and validate their frontmatter:

```powershell
$tree = gh api "repos/$repo/git/trees/${commitSha}?recursive=1" -H $apiHeader |
  ConvertFrom-Json

if ($tree.truncated) {
  throw 'Recursive tree was truncated; walk subtrees or require an exact skill path'
}

$candidates = $tree.tree |
  Where-Object { $_.type -eq 'blob' -and $_.path -match '(^|/)SKILL\.md$' } |
  Select-Object path, sha, size

$candidates | ConvertTo-Json -Depth 4
```

The recursive Trees API is limited to 100,000 entries and 7 MB; when `truncated` is true, fetch non-recursive subtrees individually ([Trees API](https://docs.github.com/en/rest/git/trees#get-a-tree)). Prefer an exact path when the source URL already identifies one. Fetching every candidate description consumes one blob/content request per skill, so bound concurrency and cache by blob SHA.

## Revision and update metadata

Use different fields for different questions:

| Question | Field | Meaning |
| --- | --- | --- |
| Which repository snapshot was inspected? | Commit SHA | Immutable commit identity |
| Did any file in this skill directory change? | Directory tree SHA | Content identity for the complete skill subtree |
| Did `SKILL.md` itself change? | Blob SHA | Content identity for that one file |
| When was this path last touched in the selected history? | Latest path-filtered commit's committer date | Display metadata, dependent on history/ref |
| When did any ref in the repository last receive a push? | Repository `pushed_at` | Repository-wide, not skill-specific |
| When did repository metadata or activity change? | Repository `updated_at` | Not a content revision date |

Get a displayable path revision record with:

```powershell
$skillPath = 'skills/git-commit'
$revision = gh api --method GET "repos/$repo/commits" -H $apiHeader `
  -f "sha=$commitSha" -f "path=$skillPath" -f 'per_page=1' |
  ConvertFrom-Json

$revision[0] | Select-Object sha, `
  @{Name='authoredAt'; Expression={$_.commit.author.date}}, `
  @{Name='committedAt'; Expression={$_.commit.committer.date}}, html_url
```

The list-commits endpoint supports both `sha` and `path` filters and exposes distinct author and committer timestamps ([API](https://docs.github.com/en/rest/commits/commits#list-commits)). A renamed path, rewritten history, or a different starting ref can change the answer, so this date must never replace a SHA for update checks.

`gh skill install` injects `github-repo`, `github-ref`, `github-tree-sha`, `github-path`, and optional `github-pinned` values into the spec-defined `metadata` map ([metadata source](https://github.com/cli/cli/blob/v2.98.0/internal/skills/frontmatter/frontmatter.go#L65-L97)). `gh skill update` resolves the current source, matches the stored source path, and compares its directory tree SHA with the installed value ([update source](https://github.com/cli/cli/blob/v2.98.0/pkg/cmd/skills/update/update.go#L247-L313)). The direct read-only check is:

```powershell
gh skill update --dry-run --dir '.\installed-skills'
```

Pinned skills are skipped unless unpinned. `--force` re-downloads matching content and overwrites locally modified tracked files but does not remove extra local files ([update manual](https://cli.github.com/manual/gh_skill_update)). For an independent implementation, store repository, source path, resolved commit SHA, directory tree SHA, and optional display revision date in a lock file; compare tree SHA first.

## Download strategies

### One conforming skill

Use the direct path form and pin the resolved commit:

```powershell
$skillPath = 'skills/git-commit'
$target = '.\downloaded-skills'
gh skill install $repo $skillPath --pin $commitSha --dir $target
```

Exact paths skip repository-wide discovery ([install behavior](https://github.com/cli/cli/blob/v2.98.0/pkg/cmd/skills/install/install.go#L120-L145), [path selection](https://github.com/cli/cli/blob/v2.98.0/pkg/cmd/skills/install/install.go#L300-L315)). The installer enumerates the skill tree, fetches blobs, checks destination paths, writes ordinary files, and injects source metadata into `SKILL.md` ([installer](https://github.com/cli/cli/blob/v2.98.0/internal/skills/installer/installer.go#L251-L308)). This is the shortest path, but it deliberately transforms `SKILL.md`; use sparse checkout or blob downloads when byte-for-byte preservation is required.

### Arbitrary selected folder

GitHub has no repository archive endpoint that emits only one subdirectory. Sparse checkout is the robust general solution:

```powershell
$target = '.\source-snapshot'
$folder = 'skills/git-commit'

gh repo clone $repo $target -- --filter=blob:none --no-checkout
git -C $target sparse-checkout init --cone
git -C $target sparse-checkout set -- $folder
git -C $target checkout --detach $commitSha
```

`gh repo clone` passes additional arguments after `--` to `git clone` and reuses GitHub CLI authentication ([manual](https://cli.github.com/manual/gh_repo_clone)). Sparse checkout limits the working tree to selected paths; cone mode is recommended unless file-level patterns are required ([Git documentation](https://git-scm.com/docs/git-sparse-checkout)). Keep or remove `.git` according to whether provenance/history is needed.

For a tiny folder in an environment where Git is unavailable, compose the Trees and Blobs APIs: locate its tree SHA, recursively enumerate blobs, reject `truncated` or walk subtrees, verify every destination remains below the target directory, then fetch each blob. This is custom downloader code, not a single `gh` feature. The `gh skill` implementation uses this model and limits parallel requests to five to reduce rate-limit pressure ([installer concurrency](https://github.com/cli/cli/blob/v2.98.0/internal/skills/installer/installer.go#L22-L24), [blob fetch](https://github.com/cli/cli/blob/v2.98.0/internal/skills/discovery/discovery.go#L914-L943)).

### Full archive

The REST zipball/tarball endpoints return a redirect to a complete repository archive at a ref; private-repository URLs expire after five minutes ([archive API](https://docs.github.com/en/rest/repos/contents#download-a-repository-archive-zip)). Resolve to a commit SHA first.

On Windows PowerShell 5.1, do not rely on `>` to preserve a native command's binary stdout. One safe composition is to let PowerShell's HTTP client write the response directly while borrowing the `gh` token:

```powershell
$archive = Join-Path $PWD 'repository.zip'
$headers = @{
  Authorization = "Bearer $(gh auth token)"
  Accept = 'application/vnd.github+json'
  'X-GitHub-Api-Version' = $apiVersion
}

try {
  Invoke-WebRequest -UseBasicParsing `
    -Uri "https://api.github.com/repos/$repo/zipball/$commitSha" `
    -Headers $headers -OutFile $archive
} finally {
  $headers.Authorization = $null
}
```

This is API/PowerShell composition, not direct archive output support in `gh`. PowerShell 7.4 changed native stdout redirection to preserve byte streams, which older Windows PowerShell does not ([PowerShell redirection behavior](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_redirection)). If extracting only a folder from the full archive, account for GitHub's generated top-level directory and defend against absolute paths, `..` traversal, symlinks, and overwrite collisions. Sparse checkout avoids those archive-extraction concerns.

## Errors, limits, caching, and retries

`gh`'s documented general exits are `0` success, `1` failure, `2` cancellation, and `4` authentication required; individual commands may define more ([exit codes](https://cli.github.com/manual/gh_help_exit-codes)). Do not infer HTTP status from exit code `1`. For API automation, request headers and inspect the status line:

```powershell
gh api --include "repos/$repo" -H $apiHeader
gh api rate_limit -H $apiHeader
```

Handle these cases explicitly:

| Signal | Interpretation and response |
| --- | --- |
| `401` or `gh` exit `4` | Login/token unavailable or invalid; stop and reauthenticate. |
| `403` with `X-RateLimit-Remaining: 0` | Wait until `X-RateLimit-Reset`. |
| `403`/`429` with `Retry-After` | Secondary limit; wait as instructed, then use bounded exponential backoff. |
| `404` | Missing resource, wrong ref/path, or intentionally concealed private resource; verify authentication and repository access before declaring absence. |
| `409` | Common for an empty/unavailable Git repository state; report it distinctly. |
| `422` | Invalid ref/input or endpoint limit; do not retry unchanged input. |
| `5xx` or network failure | Retry a small bounded number of times with jitter; preserve the original error. |
| Tree `truncated: true` | Never accept a partial discovery result; walk subtrees or require an exact path. |

GitHub documents primary and secondary rate limits separately ([REST rate limits](https://docs.github.com/en/rest/using-the-rest-api/rate-limits-for-the-rest-api)); GraphQL uses points and node/query limits ([GraphQL limits](https://docs.github.com/en/graphql/overview/rate-limits-and-query-limits-for-the-graphql-api)). Avoid unbounded parallel blob fetches. Respect `Retry-After`, `X-RateLimit-Remaining`, and `X-RateLimit-Reset`; GitHub warns that continuing while limited can result in an integration ban ([best practices](https://docs.github.com/en/rest/using-the-rest-api/best-practices-for-using-the-rest-api#handle-rate-limit-errors-appropriately)).

For polling, retain `ETag` and send `If-None-Match` on the same authenticated, otherwise-identical request. A `304 Not Modified` response does not consume the primary REST rate limit when correctly authenticated ([conditional requests](https://docs.github.com/en/rest/using-the-rest-api/best-practices-for-using-the-rest-api#use-conditional-requests)). Prefer caching immutable objects indefinitely by commit/tree/blob SHA and rechecking only the movable ref. A private-resource `404` can deliberately hide an authorization failure, so avoid repeated blind polling ([error guidance](https://docs.github.com/en/rest/using-the-rest-api/best-practices-for-using-the-rest-api#do-not-ignore-errors)).

## Security boundaries

- Treat repository names, refs, paths, file content, frontmatter, and archive entries as untrusted input.
- Resolve a movable ref to a commit SHA before discovery and use only that SHA for subsequent reads/downloads.
- Allowlist GitHub hosts and HTTPS. Do not pass user-provided endpoints to `gh api` before validation.
- Pass arguments as argument-array values, as in the PowerShell examples; never concatenate untrusted values into a shell command line.
- Canonicalize each destination path and require it to remain under the intended root before writing.
- Distinguish regular blobs, symlinks, submodules, and executable modes. Do not automatically initialize submodules or execute downloaded scripts.
- Validate `SKILL.md` against the Agent Skills specification, but do not mistake schema validity for trustworthiness.
- Review content before installation or execution. GitHub CLI itself warns that skills are unverified and may contain prompt injection, hidden instructions, or malicious scripts ([install warning](https://github.com/cli/cli/blob/v2.98.0/pkg/cmd/skills/install/install.go#L375-L379), [manual](https://cli.github.com/manual/gh_skill_install)).
- Prefer least-privilege tokens and avoid logging authorization headers, signed archive redirects, private download URLs, or full API error payloads that may contain sensitive repository data.

## Recommended source record

A source record should avoid reparsing a browser URL after ingestion:

```json
{
  "host": "github.com",
  "repository": "github/awesome-copilot",
  "sourceUrl": "https://github.com/github/awesome-copilot/tree/main/skills/git-commit",
  "requestedRef": "main",
  "resolvedCommitSha": "<40-hex-commit-sha>",
  "path": "skills/git-commit",
  "skillMdBlobSha": "<blob-sha>",
  "skillTreeSha": "<tree-sha>",
  "lastPathCommitSha": "<commit-sha>",
  "lastPathCommittedAt": "<ISO-8601 timestamp>"
}
```

Use `resolvedCommitSha` for reproducibility, `skillTreeSha` for update detection, and `lastPathCommittedAt` only for display. Keeping the original URL is useful for attribution, not as the canonical machine representation.

## Remaining caveats

- The three convenience command families central to this flow are preview features in `gh 2.98.0`; their flags and output may change independently of the versioned REST API.
- Browser `/tree` and `/blob` URLs have an inherently ambiguous ref/path split when refs contain slashes. Reliable ingestion costs ref lookups unless the producer supplies structured fields.
- Full discovery can exceed the recursive Trees API limit; exact-path lookup is both faster and more reliable for large monorepos.
- Path-filtered commit dates are history-dependent and do not carry Git object identity semantics.
- GitHub Enterprise Server is usable through general `gh`/API mechanisms but is not supported by `gh skill` in the inspected release.
- SHA examples assume GitHub's current SHA-1 object IDs. Store returned IDs as opaque strings rather than enforcing a permanent length or algorithm.
