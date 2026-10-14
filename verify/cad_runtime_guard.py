"""Enforce the user's no-CAD/no-Core/no-COM validation preference."""
from pathlib import Path

LOCK_FILE = Path(__file__).resolve().parent.parent / 'CAD_RUNTIME_VALIDATION_DISABLED'

def require_cad_runtime():
    if LOCK_FILE.exists():
        raise RuntimeError('CAD runtime validation is disabled by the user. '
                           'Use compile/static/offline checks. Do not launch CAD, Core Console or COM.')
