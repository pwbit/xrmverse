# RealExportShapes

Synthetic overlay on the `XrmverseFixture` solution that reproduces the XML shapes and archive layout of a real, recent Dataverse solution export (observed 2026-11-10 on an export of version 9.2.26102). All names, ids and contents are invented; nothing is copied from the real export, which is customer content and stays out of the repository (legal guardrails §5, §8).

- `manifest.xml`: the `RootComponents` entry of the fixture role in the `parentId` form, and `MissingDependencies` in the real shapes (sides without `schemaName`, dotted keys such as `id.uniquename`, non-numeric `type`, the `package` child).
- `entries/`: archive entries outside `customizations.xml` that xrmverse does not read (agents, formulas, workflow files), one custom API (`customapis/`), which xrmverse reads since plan 15, and one environment variable (`environmentvariabledefinitions/`), which it reads since #167.

`SolutionFixtureZip.BuildWithRealShapes()` applies the overlay.
