# Skill Management

This context covers globally installed agent skills and the provenance needed to keep them current across supported agent products.

## Language

**Skill Library**:
A distributable source containing one or more Skills.
_Avoid_: Catalog, registry

**Skill**:
One installable agent capability represented by a skill folder and its files.
_Avoid_: Plugin, extension

**Skill Installation**:
A globally managed copy of a Skill, exposed to supported Harnesses through their standard discovery paths.
_Avoid_: Project skill, per-project installation

**Harness**:
An agent product that discovers and loads Skill Installations, initially OpenCode, Codex, Claude Code, or GitHub Copilot.
_Avoid_: Consumer, host, client

**Unmanaged Installation**:
A discovered Skill folder whose source and update mechanism are not yet known to Skilly.
_Avoid_: Imported skill

**Source Provider**:
A supported mechanism that supplies Skills and defines how Skilly checks, installs, updates, and removes them, such as `skills`, `apm`, or GitHub.
_Avoid_: Package manager, registry

**Provenance**:
The recorded Source Provider, source reference, and revision from which a Skill Installation came.
_Avoid_: Origin metadata
