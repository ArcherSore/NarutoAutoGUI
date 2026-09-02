# Third-Party Notices

NarutoAutoGUI is licensed under the GNU General Public License v3.0 only
(`GPL-3.0-only`). See [LICENSE](LICENSE) for the full license text.

This document records third-party source code that is included in, adapted
into, or modified for this repository, as required by the respective upstream
licenses.

## BetterGI / Better Genshin Impact

- **Project**: BetterGI / Better Genshin Impact
- **Upstream repository**: <https://github.com/babalae/better-genshin-impact>
- **Version referenced**: 0.63.0
- **Upstream license**: GNU General Public License v3.0 (`GPL-3.0`)
- **License text**: <https://www.gnu.org/licenses/gpl-3.0.txt>

The following source files in `src/NarutoAutoGUI/ChildSession/` are adapted,
trimmed, or otherwise modified from BetterGI 0.63.0:

| File | Upstream file | Nature of use |
|------|---------------|---------------|
| `ChildSessionNativeMethods.cs` | `ChildSessionNativeMethods.cs` | Adapted (trimmed) |
| `ChildSessionProcessLauncher.cs` | `ChildSessionProcessLauncher.cs` | Adapted |
| `ChildSessionService.cs` | `ChildSessionService.cs` | Adapted (heavily trimmed) |
| `RdpActiveXHost.cs` | `RdpActiveXHost.cs` | Adapted (trimmed) |

These files have been modified from their original form. The modifications
include trimming of features not used by NarutoAutoGUI, changes to namespace,
removal of dependencies, and integration with the NarutoAutoGUI architecture.

The original copyright holders of the BetterGI source code retain their
copyright. These adapted files are distributed as part of NarutoAutoGUI under
the terms of GPL-3.0-only, consistent with the upstream GPL-3.0 license.

## Other dependencies

NarutoAutoGUI depends on the following NuGet packages. These are ordinary
runtime/development dependencies resolved via NuGet and are not vendored into
this repository. Their licenses are available through the NuGet ecosystem:

| Package | Version | Used by |
|---------|---------|---------|
| `Maa.Framework` | 5.10.0 | NarutoAutoWorker |
| `Maa.Framework.Runtimes` | 5.12.3 | NarutoAutoWorker |
| `System.Management` | 8.0.0 | NarutoAutoGUI |
| `WPF-UI` | 4.3.0 | NarutoAutoGUI |
| `nulastudio.NetBeauty` | 2.1.5 | NarutoAutoGUI |

These packages are compatible with GPL-3.0-only and do not impose obligations
beyond what is already satisfied by the GPL-3.0-only license of this project.

## Assets

The screenshot images in `docs/images/` are captures of the NarutoAutoGUI
application interface and do not incorporate third-party copyrighted material.
