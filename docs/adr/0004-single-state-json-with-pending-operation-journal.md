# All management authority lives in one versioned `state.json` with a pending-operation journal

Skilly's Management Records — Provenance, installed revision, payload hash, intended exposures — are stored in a single schema-versioned JSON file at `%LOCALAPPDATA%\Skilly\state.json` (`State/StateStore.cs`, `SchemaVersion` currently 4). No Management Record means no management authority: an installation without one is Unmanaged regardless of where it came from. Mutations record a `PendingOperation` before touching disk; an interrupted mutation that cannot be reconciled on next start puts the app into the read-only Recovery Required condition instead of guessing.

A database or per-skill lockfiles were rejected: one small JSON file matches the portable no-installer distribution, is trivially inspectable and backupable, and the whole inventory is re-derived from disk on every scan anyway — state.json holds only what cannot be re-derived (provenance and intent).
