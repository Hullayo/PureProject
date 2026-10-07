# v1.2 icon assets

These assets were extracted directly from `v1.2/src/lib/components/shared/Icon.svelte` and the four-rectangle dashboard SVG in `v1.2/src/lib/components/layout/Sidebar.svelte` in the legacy ProjectManager source. Those legacy source directories are not included in this standalone Jianxiang repository. All original path data, shapes, view boxes, stroke widths, line caps and line joins are retained. No icon geometry has been redrawn or substituted with a platform font.

There are 31 source icons and four color variants per icon:

| File suffix | Normal stroke/fill | Logo accent |
| --- | --- | --- |
| `-light.svg` | `#62594e` | `#9f352f` |
| `-dark.svg` | `#c9c0b3` | `#cf6b60` |
| `-light-accent.svg` | `#9f352f` | `#9f352f` |
| `-dark-accent.svg` | `#cf6b60` | `#cf6b60` |

The logo keeps its original warm white fills and alpha values. Its CSS `rgba(...)` colors are expressed as equivalent SVG color and opacity attributes for Windows SVG rendering. Generic `currentColor` and logo `var(--accent)` values are resolved at generation time.

`MainWindow.SourceIcon(name, size, accent)` selects the correct file using the active application theme. `chevron-left` is the original `chevron-right` rendered with a horizontal mirror; it adds no new path geometry. The SVG is decorative, while its containing button supplies an accessible name.

Normal builds use the committed SVG files and do not run the generator. To regenerate them, provide both original component files explicitly and run PowerShell from the repository root:

```powershell
& ./src/PureProject.WinUI/Assets/Icons/Generate.ps1 `
  -Source 'C:\legacy\ProjectManager\v1.2\src\lib\components\shared\Icon.svelte' `
  -SidebarSource 'C:\legacy\ProjectManager\v1.2\src\lib\components\layout\Sidebar.svelte'
```

Replace the example paths with the locations of the original component files. The script does not search adjacent projects or assume a legacy checkout exists. It verifies the source structure and parses every generated SVG as XML before writing it. The existing project asset glob includes the SVG files in both build and publish outputs.
