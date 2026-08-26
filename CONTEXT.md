# Skill Management

This context covers globally installed agent skills and the provenance needed to keep them current across supported agent products.

## Language

**Skill Library**:
A distributable source containing one or more Skills.
_Avoid_: Catalog, registry

**Source Skill**:
A Skill offered by a Skill Library, identified within that source by its relative folder path while retaining its declared name as display metadata.
_Avoid_: Package, fuzzy name

**Skill**:
One installable agent capability represented by a skill folder and its files.
_Avoid_: Plugin, extension

**Skill Installation**:
A single canonical, globally managed copy of a Skill, exposed to supported Harnesses through their standard discovery paths.
_Avoid_: Project skill, per-project installation

**Harness Exposure**:
A managed reference that makes one canonical Skill Installation visible to a Harness with a different discovery root.
_Avoid_: Replica, copy

**Harness**:
An agent product that discovers and loads Skill Installations, initially OpenCode, Codex, Claude Code, or GitHub Copilot.
_Avoid_: Consumer, host, client

**Unmanaged Installation**:
A discovered Skill folder whose source and update mechanism are not yet known to Skilly.
_Avoid_: Imported skill

**Management Status**:
Whether Skilly has authority to manage an installation: Managed, Verified Adoption Available, or Unmanaged.
_Avoid_: Ownership state

**Installation Health**:
The result of reconciling an installation with its recorded payload, metadata, and Harness Exposures: Healthy, Locally Modified, Missing, Exposure Problem, Invalid Metadata, or Collision.
_Avoid_: Update Status

**Adoption**:
The explicit act of recording verified Provenance for an existing canonical Unmanaged Installation without replacing its content.
_Avoid_: Import, claim

**Source Provider**:
A supported mechanism that supplies Skills and defines how Skilly checks, installs, updates, and removes them, such as `skills`, Microsoft APM, or GitHub.
_Avoid_: Package manager, registry

**Provenance**:
The recorded Source Provider, normalized source identity, Source Skill path, requested tracking rule, and resolved revision or content hash from which a Skill Installation came.
_Avoid_: Origin metadata

**Tracking Rule**:
The moving or immutable source reference against which a Skill Installation is checked, such as a branch, version constraint, tag, or commit.
_Avoid_: Update channel

**Update Status**:
The read-only result of comparing a managed Skill Installation with its Tracking Rule: Current, Update Available, Pinned, Source Unavailable, or Check Failed.
_Avoid_: Version status
