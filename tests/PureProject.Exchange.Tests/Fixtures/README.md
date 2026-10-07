# Portable legacy regression inputs

These four small synthetic fixtures are committed test inputs. Together they use
14,457 bytes. They contain invented project/task data and no personal data,
absolute filesystem paths, private audit directory references, or native software.
They are copied beside the test executable by the test project.

- `legacy-newlines.xlsx`: v1 canonical descriptions exceed the 200-character
  preview threshold and contain CR, CRLF and LF. Visible names and previews use
  the old exporter's CR/CRLF-to-LF normalization. Import must restore every field
  from the canonical payload when the display is unchanged.
- `legacy-escaped-28000.xlsx`: the same canonical project, with the visible task
  description edited to 4,000 literal `_x0041_` strings. OOXML escaping grows the
  serialized text to 52,000 characters; its logical length remains 28,000.
- `legacy-v1.mm`: an unchanged v1 map containing an emoji, old XML-control display
  markers, a formula-like literal title, and unknown extension fields.
- `legacy-v1-damaged.mm`: the synthetic v1 map with visible U+1F331 replaced by
  U+F331, reproducing the character corruption observed during the separate
  Freeplane audit. Canonical Base64 and its checksum stay intact. This is a
  simulated failure fixture, not a claim of a native application save.

`manifest.json` fixes their SHA-256 and byte lengths. Tests check the manifest and
verify that imports leave these fixture files unchanged. `generate.py` recreates
them deterministically using only Python's standard library and an independent
minimal implementation of the legacy file contracts; the current production
exporter is not involved. Regeneration is optional and not part of running tests.

Run the portable regression from any clone using an installed .NET 10 SDK:

```sh
dotnet run --project tests/PureProject.Exchange.Tests/PureProject.Exchange.Tests.csproj -c Release -- --regression-fixes --out exchange-regression-results
```

Omit `--evidence-root` for ordinary clones. That optional switch adds checks against
large local stress-audit evidence which is deliberately not part of the repository.
