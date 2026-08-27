# Source Providers shell out to external CLIs instead of calling APIs directly

Every Source Provider is a wrapper around an external command-line tool — `gh` for GitHub, `skills` for the skills CLI, `apm` for Microsoft APM — driven through `Infrastructure/ProcessRunner.cs`. The alternative (HTTP clients with embedded auth, or a bundled libgit2) was rejected because it would force Skilly to store or manage credentials, contradicting the local-only promise (no account, no credential storage). Authentication is delegated entirely to the user's existing `gh auth` / tool setup.

## Consequences

- Skilly is only as capable as the installed tool versions; provider checks must degrade to `Source Unavailable` / `Check Failed` when a CLI is missing or unauthenticated, never crash.
- All provider behavior is testable by substituting fake executables (`tests/FakeGh`, `tests/FakeApm`, `tests/FakeSkills`, `tests/FakeGit`) on `PATH`.
