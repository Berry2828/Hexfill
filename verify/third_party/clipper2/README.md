# Test-only Clipper2 reference

Repository: https://github.com/AngusJohnson/Clipper2

Pinned commit: `f9c5eb6e14a59f6f5d65fbfb3564519a561cf4fd` (retrieved 2026-10-14).
License: Boost Software License 1.0; original `LICENSE` and source headers preserved.
`manifest.json` records SHA-256 hashes, checked before every helper compilation.
The C# source files are unmodified. Nothing in this directory is included in the
production project or delivered HexFill DLL. The oracle DLL is built under `obj/`.

The project tests adapt the following upstream ideas:

- `CPP/Tests/TestOffsets.cpp`, TestOffsets9: reversing polygon orientation must
  not change expansion/contraction; TestOffsets11: excessive contraction yields
  no contours. The harness adds disconnected narrow-neck contraction.
- `CPP/Tests/TestPolytreeHoles.cpp`, TestPolytreeHoles10 (#973): five rectangles
  union into one outer contour with two holes. Coordinates are scaled by 0.001
  for the actual HexFill database fixture.
- Polygon tests compare area and contour count; HexFill checks additionally use
  XOR area, proper segment intersections, preservation, database rollback,
  undo, mixed winding, and translations to coordinates of magnitude 1e8.

`upstream-tests/` holds attribution/reference material. Those upstream suites
are **not** claimed as AutoCAD or HexFill test coverage. Triangulation is included
only because the unmodified Clipper convenience module references it; no test
or production behavior uses triangulation.
