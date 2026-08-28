![Skilly banner](assets/skilly-banner.png)

# Skilly

**One home for every agent Skill.**

Skilly is a portable Windows app for managing global agent Skills across OpenCode, Codex, Claude Code, and GitHub Copilot.

> **Status:** Pre-release. [View the v1 spec](https://github.com/berghtho/skilly/issues/19).

![Skilly workbench](assets/skilly-workbench.png)

The workbench follows the Industry design system: a light technical ground with a steel-blue accent, condensed headings, and square blueprint panels. Filter the inventory, search it live, and inspect any installation's Provenance, health, update status, and Harness Exposures in the details pane. Skilly ships its typefaces (Barlow and Barlow Condensed, OFL) inside the executable.

## v1

- Install, inspect, update, adopt, and remove Skills.
- Keep one canonical installation exposed to all four Harnesses.
- Track Provenance, health, and updates without unsafe overwrites.
- Use `skills@1.5.23`, Microsoft APM, or authenticated GitHub via `gh`.
- Stay local: no account, cloud backend, daemon, or credential storage.

Windows 11 x64 only. Portable, self-contained `Skilly.exe`.

[Implementation issues](https://github.com/berghtho/skilly/issues?q=is%3Aissue+is%3Aopen+label%3Aready-for-agent) · [Domain vocabulary](CONTEXT.md)
