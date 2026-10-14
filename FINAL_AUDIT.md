# HexFill Final Audit

## Required deliverables

- Full build: 13 commands, including `HBR` and `HAD`.
- Core build: 11 commands, excluding only `HBR` and `HAD`.
- Common behavior is compiled from the same `HexFill.cs` source.

## Final artifacts

- `C:\Users\allen\Desktop\HexFill_20260710_234737_Full.dll` (71,680 bytes)
  - SHA256: `0DB6174F2A4F449F3E8C5FE8CEEB7A1CD64B547684B5F008A942971D46FACE35`
- `C:\Users\allen\Desktop\HexFill_20260710_234737_Core.dll` (63,488 bytes)
  - SHA256: `D0F33F2862B53DB06A291797F595B23801C75996212640C24C436CAE910E2C01`

## HNM and HCLN requirements

- Repeated `HNM` preserves the first parent number until `HCLN` is executed.
- `HCLN` marks cleared entities with `H_Clr=1`; the marker is consumed only after a successful subsequent HNM transaction.
- Existing numbered roots remain roots when the selection is expanded, preventing new outer frames from overwriting established parent numbers.
- Descendants are flattened to one suffix level: `root_01`, `root_02`, ...; no `root_01_01` output is generated.
- Independent regions receive visible root numbers.
- Selecting only an outer frame in `HCLN` also clears all descendants and all ancestors found in the drawing.

## Geometry and performance fixes

- Strict containment requires every sampled child-boundary point to be inside or on the parent boundary.
- The former center-only rescue path was removed, preventing L/I false grouping.
- Polygon point tests no longer call `Ray.IntersectWith`; straight polylines use direct segment math and curved boundaries use ordered sampling.
- Parent search tracks the smallest containing parent directly instead of allocating a candidate list per entity.
- Cycle protection uses depth and iteration limits and breaks cycles at the largest-area node.
- HAD processes leaf regions only and caches leaf numbers instead of opening one transaction per leaf.

## Runtime verification

- Nested test: first HNM produced `100_02`.
- Direct XData inspection of a three-level nesting produced exactly `100`, `100_01`, and `100_02`; no second-level suffix was present.
- Repeated HNM with a different input still produced `100_02`.
- After HCLN, HNM produced `300_02`.
- Independent region produced `500`.
- L-shaped region and a disjoint region inside its bounding box produced separate roots `700` and `701`.
- HCLN selecting only the outer frame reset all three nested regions; next HNM produced `20_02`.
- HBR exported a valid brightness CSV with root `10`.
- HAD accepted the brightness CSV and completed its command flow.
- Reflection-only inspection of the final DLLs found exactly 13 Full commands and 11 Core commands; Core differs only by `HAD` and `HBR`.

## Remaining boundary

The automated tests cover command loading and the highest-risk numbering workflows. Visual inspection of large production drawings in full AutoCAD remains advisable because Autodesk geometry behavior can vary with damaged, self-intersecting, non-planar, or proxy entities.
