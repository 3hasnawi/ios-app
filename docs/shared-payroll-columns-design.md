# Shared payroll columns: staged design (not activated)

## Current state

Backup versions 1–6 remain unchanged. `SharedPayrollColumns` in C# and Swift is a tested byte-preserving primitive, not a database migration, backup reader, or writer. Production must not emit a new backup version until both client implementations, server capability checks, and device recovery tests are complete.

## Proposed version 7 representation

* A backup may store each distinct exact UTF-8 byte sequence of the `headers` JSON array once, indexed by uppercase SHA-256. A row whose original `rawColumns` JSON is supported may refer to that schema and store the exact bytes before and after the array. Restoring concatenates those three byte sequences in order; the original bytes, not merely the interpreted values, must be identical.
* Property order, duplicate column names, empty cells, unknown properties, encoding escapes, whitespace, and per-sheet or per-month schema differences are significant. A record that cannot be split losslessly must retain its original `rawColumns` bytes in full. Never infer schema identity from a month, a display label, or a count.
* SHA-256 is over the exact schema bytes, not a normalized title or a locale-specific string. On import, recompute it and reject a missing, duplicate-conflicting, corrupt, or unknown schema reference before any changes are committed. A schema hash does not protect a row's other bytes; the complete backup, each source and output record, and archive entries must have independent integrity checks.
* Layout and file names are intentionally not frozen here. Define limits, final record keys and test vectors jointly with both clients before implementation. Version 7 must be explicitly rejected by existing readers; do not repurpose version 6 or a `rawColumns` payload that old clients could silently misread.

## Migration and activation gates

1. Preserve an independently verified full backup outside the current single-version server. Never mutate its original. The local source and its pending imports must be snapshotted under their existing concurrency guards.
2. Generate the new candidate in a separate file with streaming IO and checksums. Validate all rows and unknown fields, month totals, employee/deduction counts, source file identities, archive PDFs, reconciliation, trust, account and settings state. Decode and compare every original `rawColumns` byte sequence; compare all other fields and file hashes. Abort safely on any mismatch.
3. Before enabling writing, deploy and test readers on iOS and Windows, including a staged, transactional restore that does not require the whole file or all models in memory. The current iOS restore still retains SwiftData changes in one transaction and is not proven suitable for the reported workload.
4. Verify the actual installed server reports an explicit capability for the exact backup-format protection, protects a newer stored version against every older upload endpoint, preserves a restorable independent older checkpoint, and rejects races. `backup_format_guard` alone does not demonstrate deployment identity or multi-version retention. Do not upload the candidate to a single-version server and overwrite the only known restorable backup.
5. Exercise old-to-new, new-to-new, and rejection by old clients, along with interrupted disk writes, full disks, corrupted schemas/tails, duplicate names, Unicode, unknown fields, file tampering between validation and commit, and an iPhone physical-device restore of a workload representative of the actual data. Check RAM, disk, runtime and correctness. User data must remain local and never be uploaded for testing.
6. Only after successful validation, expose a clearly reversible migration and retain prior versions per the user's retention policy. If any gate fails, keep the previous state and do not claim a completed backup.
