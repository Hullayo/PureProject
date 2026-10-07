# Project exchange contracts and verification

The production API is a new ProjectExchangeService instance in PureProject.Infrastructure.

    IReadOnlyList<Project> ImportFile(string path, CancellationToken cancellationToken = default);
    void ExportFile(string path, IEnumerable<Project> projects, CancellationToken cancellationToken = default);
    IReadOnlyList<Project> Import(Stream source, ProjectExchangeFormat format, CancellationToken cancellationToken = default);
    void Export(Stream destination, IEnumerable<Project> projects, ProjectExchangeFormat format, CancellationToken cancellationToken = default);

File methods infer .pureproject, .mm, or .xlsx. The enum is PureProject, FreeMind, or Excel. Call these synchronous methods on a background worker using a stable project snapshot. Imports validate every project before returning, without touching the live repository. File export stages beside the destination, flushes, and atomically replaces it; cancellation or validation failure retains the previous file. Stream methods leave caller streams open. ZIP imports require seekable streams.

## Data model and limits

Every format retains the exact annotated canonical project JSON, including project/group/status/task IDs, ordering, dependency offsets, recurrence, subtasks, comments, tags, milestones, changelog, templates, file tree/flowchart, sync_enabled, all unknown extension values and the distinct .pm envelope extension namespace. Schema parsing uses PmSerializer.ParseCanonicalProject; it neither forces sync on nor normalizes the model. Unchanged files round trip all fields. SHA-256 protects each payload against accidental corruption; it is not an authentication signature.

Maximums: 2,000 projects, 20,000 tasks per project (Core contract), 64 MiB JSON per project, 256 MiB input/output file, 512 MiB total ZIP expansion and 4,096 ZIP entries. XML forbids DTD/external entities, nesting beyond 64 and individual values beyond 65,536 characters. ZIPs are never extracted; absolute/traversal/duplicate paths and excessive expansion are rejected. These bounds admit the 10 × 10,000 task benchmark.

## Format contracts

.pureproject is a ZIP containing manifest.json with Format: PureProject, Version: 1 and ordered Projects: [{ Id, Entry, Sha256 }]. Payloads are projects/0000.json, etc.; user IDs are never paths. Extra or missing archive entries, future versions and mismatched IDs/hashes are rejected. Both export and import include the exact serialized manifest bytes in the 512 MiB expansion budget. A failed file export retains the prior destination and removes its temporary file.

.mm uses FreeMind 1.0.1 XML, readable by the open FreeMind/Freeplane ecosystem. The hierarchy is root → project → task group → status → task → subtask. Plain node text edits rename the corresponding model. Task attributes support description, priority and due date/time; subtask attributes support completion. Tasks may move between existing statuses/groups within one project. On a status change, entering done records the import time if needed, leaving done clears completed_at, and status_before_closed_id is reset. No recurrence or task-generation side effects run during import. Comments, history and all other properties remain in verified project attributes (pp:data:000000, etc.).

The root starts expanded and every other node starts folded, so large maps initially show only the project list. Readers can expand one branch at a time. Fold/unfold state is presentation only and does not change imported project data. Freeplane's `attribute_registry SHOW_ATTRIBUTES="hide"` hides metadata by default while retaining every attribute. Every node uses `FORMAT="NO_FORMAT"` so a title such as `=1+1` remains literal text.

The .mm importer accepts application-exported maps, not arbitrary unrelated mind maps. All project/group/status/task/subtask identity nodes and metadata must remain. Removing, duplicating or adding data nodes, moving statuses between groups, moving tasks between projects, changing IDs, introducing unsupported data attributes or rich-text node content is rejected. Purely visual styling can change. New maps have root `pp:format=PureProject/2` and project `pp:text_encoding=unicode-scalars/1`. Supplementary Unicode scalars and XML-invalid controls use reversible `\u{1F331}` / `\u{0001}` display escapes; a literal backslash is doubled. The codec applies to titles and attributes, including structural IDs, and is decoded exactly once. Canonical payloads remain unchanged. Invalid escapes reject with a diagnostic. This avoids the observed Freeplane native-save conversion of supplementary scalars into their low 16 bits. Unedited v1 maps remain supported, including their old control markers; an edited v1 map showing the known low-16 Unicode corruption signature is rejected instead of overwriting intact metadata. Structural IDs must still be legal XML text. The chosen open-source application's native open/save behavior needs validation for that application/version; a parser check alone does not establish it.

.xlsx has one worksheet per project, merged project/group headings and a separate task row per task. Empty projects and groups/statuses with no tasks remain represented. Technical row-type column A is hidden; B preserves IDs. C is the project/group/status name or task title. Task columns D/E are editable group/status IDs, F is priority (high/medium/low), G/H due date/time and I description. Row 3 C:I is the editable project description. Column headers and identifying columns remain frozen. Dates are numeric Excel dates with yyyy-mm-dd; both 1900 and 1904 date systems are supported on import. Text beginning with =, +, - or @ is stored as literal text, never executed. Formula cells are rejected on import.

Full JSON is split into 24,000-character Base64 chunks in the hidden __PureProject worksheet, with hashes and project-sheet mapping. This uses ordinary worksheet cells so spreadsheet applications can retain the metadata when saving. The importer handles inline strings, shared strings and rich string runs. Keep all records, sheets and IDs. Names/title/description/date/priority and task group/status ID changes are applied. Changes to status category/group definitions, deletion/addition of records, extra data columns, renamed project sheets or missing metadata fail with an explicit message. Workbook appearance is a presentation surface, not model data.

Visible descriptions longer than 200 characters show an explicit short preview; an unchanged preview restores the complete original description. Editing the preview replaces the whole description. This rule is written into the workbook and applies to mind-map description attributes and long subtask titles too. Other complex fields are retained in metadata rather than shown as misleading partial tables.

XLSX XML preserves CR, LF and CRLF. When an older export's unchanged display matches the known CR/CRLF-to-LF normalization of its canonical value or preview, import preserves the canonical original. A change consisting only of that normalization cannot be distinguished from the older exporter and is therefore treated as unchanged. Excel's 32,767-unit cell limit is checked after one OOXML escape decode; shared strings are not decoded again when referenced. XML value and aggregate resource limits continue to apply to encoded input.

## Commands

The tests need a .NET 10 SDK and have no external NuGet package dependencies. A
normal clone can run the basic suite and portable regression without stress-test
artifacts, Python, Excel, Freeplane, or the original machine's paths:

    dotnet run --project tests/PureProject.Exchange.Tests/PureProject.Exchange.Tests.csproj -c Release -- --out exchange-basic-results
    dotnet run --project tests/PureProject.Exchange.Tests/PureProject.Exchange.Tests.csproj -c Release -- --regression-fixes --out exchange-regression-results

The four checked-in synthetic legacy inputs in `Fixtures/` total 14,457 bytes.
Their checksums are pinned by `Fixtures/manifest.json`; `Fixtures/generate.py` is
an optional independent, deterministic generator using Python's standard library.
The default basic suite generates its own inputs, and the portable regression
does not read any audit directory. Only explicit `--evidence-root` adds the original
large/local failure samples. Capacity testing is a separate opt-in mode below.

If using the repository's local SDK, first run `build.ps1 -Task Restore` to
prepare it, then configure its package environment as in that script. A normal
clone does not include `.tools/nuget-feed`; use the normal configured package
sources unless you separately prepared an offline feed:

    .tools/dotnet/dotnet.exe restore tests/PureProject.Exchange.Tests/PureProject.Exchange.Tests.csproj
    .tools/dotnet/dotnet.exe run --project tests/PureProject.Exchange.Tests/PureProject.Exchange.Tests.csproj --no-restore -- --out artifacts/exchange-audit

Run each performance case in a fresh process to avoid baseline memory polluted by a preceding format and equality checks:

    .tools/dotnet/dotnet.exe run --project tests/PureProject.Exchange.Tests/PureProject.Exchange.Tests.csproj --no-build --no-restore -- --benchmark --tasks 100000 --format pureproject --out artifacts/exchange-audit/scale-100k-isolated
    .tools/dotnet/dotnet.exe run --project tests/PureProject.Exchange.Tests/PureProject.Exchange.Tests.csproj --no-build --no-restore -- --benchmark --tasks 100000 --format mm --out artifacts/exchange-audit/scale-100k-isolated
    .tools/dotnet/dotnet.exe run --project tests/PureProject.Exchange.Tests/PureProject.Exchange.Tests.csproj --no-build --no-restore -- --benchmark --tasks 100000 --format xlsx --out artifacts/exchange-audit/scale-100k-isolated

The benchmark samples managed and process private memory every 20 ms during export/import, excluding deep-equality verification. Peak samples are observations, not hard allocation guarantees. Output includes file bytes, timings, source baseline and exact per-project deep comparison.

Add --input artifacts/scale-100k/data/projects.json to benchmark the application's 10 projects × 10 groups × 10 statuses × 100 tasks fixture (scale-fixture.json is its descriptive manifest, not the database). The CLI reads it without opening the repository, records its SHA256 and verifies the source is unchanged after each format. Without --input it creates its own 10 × 10,000 task dependency-chain fixture.

For a file edited and saved by Excel/Freeplane:

    .tools/dotnet/dotnet.exe run --project tests/PureProject.Exchange.Tests/PureProject.Exchange.Tests.csproj --no-build --no-restore -- --verify-file path/to/edited.xlsx

independent_check.py accepts the artifact directory and uses read-only openpyxl, ElementTree, zipfile and JSON parsing to check readable typed dates, literal text, layout features, payload hashes and equivalent canonical payloads across all three formats. This optional check requires your own Python 3 environment with `openpyxl`; no Python runtime or library bundle is included in this repository. The original local audit also used artifact-tool to independently import and render representative visible worksheets; that tool is not a CI or ordinary test dependency. Image rendering is not a substitute for native Excel/Freeplane save/reimport tests.

Targeted regression tests, optionally including the immutable audit failure samples:

    .tools/dotnet/dotnet.exe run --project tests/PureProject.Exchange.Tests/PureProject.Exchange.Tests.csproj -c Release --no-restore -- --regression-fixes --evidence-root artifacts --out artifacts/stress-fixes-20261006/exchange/after

Compare a native application's saved file with the generated all-field expected JSON:

    .tools/dotnet/dotnet.exe run --project tests/PureProject.Exchange.Tests/PureProject.Exchange.Tests.csproj -c Release --no-build --no-restore -- --compare-file path/to/native-saved.mm --expected-json artifacts/stress-fixes-20261006/exchange/after/freeplane-v2-expected.json --out path/to/new-result-directory

The package capacity regression is opt-in and must run separately from UI/load tests. It actually exports packages whose payload plus manifest is 512 MiB minus one byte, exactly 512 MiB, and one byte over. Accepted cases are reimported and every complete canonical project's SHA-256 is compared. The rejected case checks existing-destination preservation and temporary-file cleanup. This may need substantial memory and is intentionally excluded from the ordinary suite:

    .tools/dotnet/dotnet.exe run --project tests/PureProject.Exchange.Tests/PureProject.Exchange.Tests.csproj -c Release --no-restore -- --package-capacity-fix --out artifacts/stress-fixes-20261006/exchange/capacity
