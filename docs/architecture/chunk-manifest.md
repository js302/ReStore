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

Encrypted snapshots insert a namespace segment before the prefix —
`chunks/enc_<key-hash>/<first-two-hash-bytes>/<chunk-id>.chunk` — so chunks encrypted under
different passwords never collide in the same store.

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
- `chunkStorageNamespace`: subdirectory chunks live under, derived from the encryption key
  (see Encryption and Deduplication below). Null for unencrypted snapshots
- `profile`: chunking profile (min/target/max chunk sizes, rolling window, and `AlgorithmVersion`:
  1 = pre-1.7 hash, 2 = `RollingWindowHash` from 1.7). The version is not part of the root hash. When absent
  it is inferred from `createdUtc` (before the 1.7.0 release = 1, otherwise 2). If the previous snapshot's
  version differs from the current one, Backup re-chunks every file instead of reusing/carrying previous
  entries (unchanged chunks still dedup by id); unreadable files are then omitted and reported as skipped
- `files[]`: per-file metadata and chunk references
- `rootHash`: integrity hash over manifest content

Chunking uses an incremental gear-hash (Rabin-Karp style, O(1) per byte) to pick
content-defined boundaries. Chunk IDs are content hashes, so boundaries are a
compatibility surface: if the algorithm changes, the same file yields different chunk IDs
and nothing already in storage can be reused. `ChunkingServiceTests` pins the boundaries
with a golden vector so such a change cannot land unnoticed.

Release note: v1.7.0 changed chunk boundaries (the 1.6 to 1.7 upgrade). Users upgrading from 1.6 had a
one-time re-chunk of all files on their first 1.7 backup; the manifest `AlgorithmVersion` now makes this explicit.

Each file entry stores:

- Relative path
- File size and modified timestamp
- File content hash
- Ordered chunk list

Each chunk entry stores:

- Chunk ID (content address)
- Plain content hash
- Plain size and stored size
- `codec` (v3 only; absent = `raw`): `brotli` when the stored payload is Brotli-compressed (quality 2) before
  encryption, so the stored object is `encrypt(compress(plain))` for encrypted repos. The chunk ID and content hash
  are always computed over the plaintext. A chunk is stored raw when compression saves less than ~3%, when the
  source file has an already-compressed extension (jpg, png, mp4, zip, docx, ...; PDF is tried), or when
  `chunkDiffing.compression` is `off`. An object already stored under the same ID is reused with whatever codec it
  has (recognised by its stored size), never overwritten with a different codec. v2 manifests never carry a codec.

## Backup Commit Protocol

Backup commits in three phases:

1. Chunk each file and upload any chunk the provider does not already have (`ExistsAsync`
   per chunk). Chunks are uploaded as they are produced rather than collected first, so peak
   memory stays at roughly one chunk instead of scaling with the size of the change set.
2. Upload the snapshot manifest, which by then references only chunks that are already stored
3. Update `HEAD` as the final commit pointer

`HEAD` therefore never points at a manifest whose chunks are missing, and cancelling at any
earlier point leaves `HEAD` on the previous snapshot — an interrupted backup is inert rather
than half-applied.

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
