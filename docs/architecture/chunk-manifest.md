# Chunk Manifest

## Overview

User-file backups in ReStore use a manifest-first snapshot model:

- A point-in-time snapshot manifest describes files and referenced chunks
- Chunk objects are content-addressed by SHA256
- Unchanged chunks are reused across snapshots
- HEAD points to the latest manifest per watched directory

System component backups (programs, environment, settings) still use archive artifacts.

## Storage Layout

Snapshot artifacts are stored with deterministic paths:

- Manifest: `snapshots/<group-key>/<snapshot-id>.manifest.json`
- Head pointer: `snapshots/<group-key>/HEAD`
- Chunk object: `chunks/<first-two-hash-bytes>/<chunk-id>.chunk`

`<group-key>` is derived from the watched directory path and includes a short hash suffix for uniqueness.

## Manifest Contract

`SnapshotManifest` includes:

- `version`: manifest schema version
- `snapshotId`: unique snapshot identifier
- `group`: normalized watched-directory path
- `createdUtc`: creation timestamp
- `backupMode`: Full, Incremental, or ChunkSnapshot
- `encryptionEnabled`: whether chunk payloads are encrypted
- `encryptionSalt`: salt used for key derivation (if encrypted)
- `keyDerivationIterations`: PBKDF2 iterations used for encryption key derivation
- `profile`: chunking profile (min/target/max chunk sizes and rolling window)
- `files[]`: per-file metadata and chunk references
- `rootHash`: integrity hash over manifest content

Each file entry stores:

- Relative path
- File size and modified timestamp
- File content hash
- Ordered chunk list

Each chunk entry stores:

- Chunk ID (content address)
- Plain content hash
- Plain size and stored size

## Backup Commit Protocol

Backup uses a three-phase commit:

1. Build snapshot manifest and chunk payloads from changed files
2. Upload only missing chunks (`ExistsAsync` check per chunk)
3. Upload manifest, then update `HEAD` as final commit pointer

This ordering prevents `HEAD` from pointing to a manifest whose chunks were not uploaded.

## Integrity Validation

Restore and verify operations perform strict validation:

1. Resolve HEAD to a manifest path when needed
2. Download and validate manifest `rootHash`
3. Download chunk objects and validate chunk hash/size
4. Reconstruct file content and validate final file hash/size

If any integrity step fails, the operation reports failure and does not silently continue.

## Encryption and Deduplication

When encryption is enabled:

- A master key is derived with PBKDF2-SHA256
- Chunk payload encryption is deterministic per chunk identity
- Deduplication remains effective because identical plaintext chunks map to identical encrypted chunk payloads

## Retention and Chunk GC

Retention is manifest-first:

1. Select manifests to keep by policy (`keepLastPerDirectory`, `maxAgeDays`)
2. Delete dropped manifests
3. Decrement chunk reference counts
4. Delete only chunks that become unreferenced

Invariant: the newest snapshot in each group is always kept.

## Operational Telemetry

Current runtime logs emit:

- Backup chunk reuse telemetry:
  - total chunk references
  - unique chunks in manifest
  - uploaded chunks vs reused chunks
  - reuse ratios
- Restore telemetry:
  - files restored vs expected
  - chunk downloads and cache hits
  - validation failure category when restore fails
- Verify telemetry:
  - unique chunks checked
  - missing/invalid chunk counts
  - invalid file count
  - overall validation failure count
