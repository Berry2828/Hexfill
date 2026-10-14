import math

from acad_verify import htc_dot_errors


def dots(spacing=0.06, radius=0.015):
    return [{"center": ((x + 0.5 * (y % 2)) * spacing,
                        y * spacing * math.sqrt(3) / 2), "radius": radius}
            for x in range(6) for y in range(6)]


def test_fixed_geometry_is_accepted():
    assert htc_dot_errors(dots()) == []


def test_old_diameter_is_rejected():
    assert any("radii" in error for error in htc_dot_errors(dots(radius=0.05)))


def test_changed_spacing_is_rejected():
    for spacing in (0.03, 0.1):
        assert any("spacing" in error for error in htc_dot_errors(dots(spacing)))


def test_empty_output_is_rejected():
    assert htc_dot_errors([])
