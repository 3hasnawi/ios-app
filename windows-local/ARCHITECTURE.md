# Local Windows rebuild: implementation contract

New projects only under `windows-local`. Existing iPhone, server, and Windows application remain untouched.
.NET 10 WPF (Windows) + SQLite. Source-link `../windows/SalaryDesktop.Core` in the independent `SalaryLocal.Engine` project for parsing and approved report formats, excluding its BankStatement.cs. The local BankStatement.cs matches iPhone nearest-ten 12% rounding (midpoint upward) before pension addition. Legacy files, persistence and servers remain untouched. No network operations are invoked by the new application. Synthetic fixtures only.

## Namespace SalaryLocal.Core

`LocalPayrollStore(string directory)` is disposable and owns `<directory>/payroll.db`.
- string DatabasePath
- StoreStatus GetStatus()
- IReadOnlyList<StoredMonth> GetMonths()
- BackupSnapshot GetSnapshot(params string[] months) // empty = all, otherwise selected months only
- ImportResult Import(PayrollImportPreview preview, string sourceName, string sourceSha256, string expectedRevision, bool replaceExisting)
- IReadOnlyList<HistoryEntry> GetHistory()
- void CreateDatabaseSnapshot(string destination) // SQLite consistent backup, CreateNew semantics
- static StoreStatus ValidateDatabase(string path) // read only, full integrity + schema + semantic checks
Records:
StoreStatus(string DatasetId, string RevisionId, DateTimeOffset CreatedAt, long RevisionCount, long EmployeeRowCount, long HeaderSchemaCount, long DatabaseBytes)
StoredMonth(string Name, int EmployeeCount, int DeductionCount, decimal NetTotal, string RevisionId)
ImportResult(bool Changed, int Employees, int Deductions, string RevisionId)
HistoryEntry(string RevisionId, string ParentRevisionId, string Month, string SourceName, DateTimeOffset CreatedAt, int Employees, int Deductions)

SQLite stores shared header arrays once by SHA256, raw values per row; do not include headers in each row. Content-addressed immutable row objects and version row links avoid duplicating unchanged rows. Keep all historical versions; no auto deletion. Exact repeated import is a no-op. Import transaction checks expected current revision. Reject malformed data. Preserve raw order, duplicates, blanks, unknown fields and decimals. Original Excel read only, only source hash/name metadata persisted. Support payroll/deduction import independently without dropping the other section. Dataset and revision lineage support two-computer transfer.

BackupService: `.Create(store, destination)` validates a compact SQLite copy, packages `manifest.json` and `payroll.db` once in ZIP, checks SHA256 and reopens it. `.Inspect(path, currentStatus)` returns manifest/status/conflict information. `.RestoreAsNewWorkspace(path, workspacesDirectory)` creates a fresh directory, verifies there and returns directory; never overwrites current workspace. Native format .salarybackup, not an iPhone ZIP; no false compatibility claims.

The WPF interface lives in SalaryLocal.App; persistence and portable backups live in SalaryLocal.Core. SalaryLocal.Tests verifies storage, backup integrity, conflict relationships, and workspace recovery. The HTML/PNG file is a labeled design preview, not a Windows runtime screenshot.

## Compression and portable snapshots

Local immutable payloads use bounded Brotli compression. Content identifiers hash the original uncompressed bytes. Consistent SQLite backup copies transcode payloads to raw bytes and VACUUM before ZIP compression, improving compression across similar rows. Restore validates the archive and database first, then recompresses only the new staging database before activation. Both paths preserve revision identity and require temporary disk space.

## Version 0.2 interchange and auxiliary data

SQLite schema v2 migrates v1 without rewriting its revision history. Auxiliary revisions preserve settings, accounts, staff and attachments in the same global lineage. ZIP interchange is separate from .salarybackup: versions 1–6 and 8–10 are inspected and imported into a new workspace only, with reviewed digest checks. Unknown fields and opaque entries are retained; exports reject unsafe precision or unsupported changed structures rather than silently drop data. Actual iPhone restore is not verified. LocalReconciliation patches only changed modeled fields in original JSON and preserves untouched dates/unknown values.


## Version 0.3 archive and trust editing

LocalSalaryTrust patches original plist JSON losslessly: untouched unknown fields, date representations, null/absent distinctions and numeric lexemes survive. Individual entry edits protect original identity, base amount and legacy-paid state. Review decisions are separate from payment edits. Opening XLSX import is append-only, one-time, validated through September 2026, and preserves prior entries. Its local reader requires unique critical headers including state/case status, rejects unrecognized nonempty sheets and ambiguous month/year values, and verifies source SHA-256 before/after reading. New writes require the reviewed global revision. Book links are validated against the same snapshot. No financial transaction is performed. Monthly cash-to-trust intake was added in version 0.4 below.

Archive writes are atomic SQLite revisions containing a source-addressed 1–3-page PDF and modeled book metadata. Replacements keep book identity, creation date and trust links. Deletion is blocked for any book linked by the trust ledger. Previous snapshots retain deleted/replaced content. Unknown imported book fields are rekeyed to the changed modeled record in the same transaction. Source PDF files are read-only.

WPF management pages provide explicit confirmations, stale revision rejection and a new synthetic HTML/PNG design preview. These design images are not screenshots or evidence of Windows runtime behavior.


## Version 0.4 reviewed monthly cash intake

LocalMonthlyCashTrust.Prepare owns the selected payroll bytes and produces a read-only additions preview tied to the global revision and a SHA-256 source fingerprint. It requires the opening ledger and an unambiguous stored month after September 2026. It rejects duplicate persons, invalid or ambiguous payment classification, missing adjacent case-status data, changed existing amounts/status and vanished existing monthly cash entries. Identical existing entries are no-ops.

SaveMonthlyCashTrust opens an immediate SQLite write transaction, reloads payroll and the ledger under that lock, recomputes the preview, verifies its source fingerprint and totals, then applies the reviewed new IDs through the lossless trust patcher. Existing entries, payments and book links remain unchanged. This is an explicit preview/confirmation step on the trust page, separate from payroll import; the interface states that importing payroll alone does not add trust entries. No external payment occurs.

The account import preview now shows the preserved previous branch when the source branch is blank, matching the already-tested merge behavior.


## Version 0.5 staff, Access, transfer letters and image archive

LocalAccessReader is a bounded, read-only port of the existing Swift reader. It accepts Jet4/ACE header versions 1–3, rejects encryption/password protection and unsupported types, does not execute queries/macros or follow linked tables, and reports incomplete reads instead of dropping rows. Limits cover file/page/table/cell/text/memo/forwarded-record work. Synthetic byte fixtures test the parser; these fixtures are not a Microsoft Access interoperability certification. Reader-to-preview-to-SQLite, native backup and iPhone-format round trips are also tested without real personnel data.

LocalStaffAccessImport maps recognized fields and lookup tables, preserves every raw column including unknown/hidden/null/zero values inside details, and requires explicit complete replacement. Equal lookup candidates must agree on all used values including zero. Local staff editing uses position, row fingerprint and global revision together; it preserves source snapshots and unknown interchange fields, rejects identity conflicts, updates summary settings atomically and retains old revisions. Mother is the normalized iPhone search key; its original spelling remains in Details.

LocalTransferLetters uses the original eleven templates (two groups, nine individual letters). Immutable previews bind source, amount, references, payer and beneficiary accounts. Revalidation under a SQLite write lock protects package export and the separate local group-issuance declaration. ZIP output is create-new, contains original-format PDFs plus a separate names PDF or CSV as applicable, and performs no financial transmission. Header/body/committee edits preserve unknown settings; reference and font imports are local. Required fonts are validated, not silently substituted. Actual Windows letter rendering remains unverified. No bulk generation, merged names pages, individual issuance UI or advanced beneficiary override editor is implemented.

LocalArchiveImages snapshots 1–3 JPEG/PNG sources, bounds encoded bytes and decoded pixels, applies all EXIF orientations plus user rotation, and fits full pixel dimensions on A4 without cropping or new JPEG encoding. The PDF stream records overflow without throwing through a native callback, then rejects after closing. It stores through the existing atomic archive path, preserving identity and trust links on replacement. WIA is an optional Windows-only acquisition adapter; cancel/failure stays explicit. Actual image-only PDF output was rendered and visually checked on Linux. WPF and real scanner hardware were not executed.


## Version 0.6 letters, output and isolation

LocalTransferLetterBatch plans at most eleven distinct templates from one immutable revision and rejects duplicate entity sources. All selected letters must prepare successfully; ZIP output uses CreateNew and verifies the current source under a SQLite write transaction. LocalLetterPdfAssembly appends complete checked name-list pages without rasterizing and compares page geometry, text positions and embedded image digests after PDF merge. Entity name lists use unambiguous payroll fallback for rank/workplace without merging deduction rows.

LocalTransferRecipients patches only recipient fields and preserves future JSON. LocalTransferIssuance uses the existing iPhone manual ledger format and preserves future fields; adjusted contracts are deliberately blocked because that schema cannot represent the adjustment. No issuance action transfers money or files.

LocalPdfPreviewWindow owns its PDF bytes, renders one preview page at a time with Docnet.Core 2.6.0, and submits pages at 600 dpi only after Windows PrintDialog. A printable-area fit requires confirmation when scaling. LocalSessionLock uses PBKDF2-SHA256 and a separate per-computer config, not encryption. WPF confirmation dialogs are covered; native picker/print returns are invalidated if the lock generation changed. In-flight authorized work and already submitted print pages are not rolled back by a session lock. Windows UI and printer verification remain outstanding.

LocalLegacyMigration inventories an explicitly selected source and imports only the reviewed exported ZIP into a fresh workspace. It never composes old application stores or accesses them automatically. LocalPathSafety rejects existing reparse-point components for workspace and migration paths; it does not claim protection against malicious concurrent OS path replacement. Installer AppId and install/data paths are separate from the legacy app; only unsigned installer source/build scripts are delivered, not an executed installer.
