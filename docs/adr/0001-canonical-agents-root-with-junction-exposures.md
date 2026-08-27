# One canonical Skill Installation in `~/.agents/skills`, exposed via NTFS junctions

Four Harnesses discover global Skills from different roots (`.agents\skills`, `.claude\skills`, `.copilot\skills`, `.config\opencode\skills`, legacy `.codex\skills`). Rather than copying a Skill into each root — which multiplies drift, update work, and Collision risk — Skilly keeps exactly one canonical Skill Installation under `~/.agents/skills` and creates a Harness Exposure as an NTFS directory junction for any Harness whose root differs (today: Claude Code). A junction was chosen over a symlink because symlink creation on Windows requires administrator rights or Developer Mode, while junctions work for any user; this is why `Infrastructure/Junction.cs` writes the mount-point reparse buffer via `DeviceIoControl` directly instead of using `File.CreateSymbolicLink`.

## Consequences

- Health checks must verify that each junction still resolves to the canonical path (`ExposureState.VerifiedJunction`); a plain folder at an exposure path is a Collision, not a copy to reconcile.
- Payload hashing must refuse reparse points to avoid hashing through an exposure into the canonical folder twice.
