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

The source inspector searches paths, names, and descriptions. Select a row to preview it; check its box to install it. **Select visible** selects only search results, while **Select none** clears the entire selection. Selections outside the current search remain counted. Existing local destinations stay visible but cannot be selected for installation.

GitHub and Microsoft APM inspections include a read-only SKILL.md preview. The `skills` provider supplies descriptions only. Installed Skills have shortcuts to open their folder or supported HTTPS source, copy their path, and read SKILL.md without launching an editor. Health details explain the next step for collisions, missing files, metadata errors, and broken Harness Exposures.

Updates show a file comparison before applying, including scripts and every affected Skill in an APM package. Installed and available content are checked again when applying. APM changes that add or remove Skills are shown but cannot yet be applied by the managed updater. **Review updates** controls whether the confirmation opens; content checks always run.

Update progress counts affected Skills. **Stop after current** finishes the active provider operation and leaves remaining updates untouched. **History** keeps up to 1,000 per-Skill results locally within 4 MB; interrupted runs are recorded without automatic retries. The workbench wraps its toolbar on smaller screens, supports resizable columns, and lets you collapse or resize details.

[Implementation issues](https://github.com/berghtho/skilly/issues?q=is%3Aissue+is%3Aopen+label%3Aready-for-agent) · [Domain vocabulary](CONTEXT.md)
