"""These tests never import or execute a CAD runtime."""
import pytest
import cad_runtime_guard as guard
import commands_core_smoke as core
from acad_verify import AcadSession

def test_project_keeps_user_runtime_lock():
    assert guard.LOCK_FILE.exists()
    with pytest.raises(RuntimeError, match='disabled by the user'):
        guard.require_cad_runtime()

def test_core_launch_is_blocked_before_process_creation(monkeypatch, tmp_path):
    def unexpected(*args, **kwargs):
        raise AssertionError('A process must not be created')
    monkeypatch.setattr(core.subprocess, 'run', unexpected)
    with pytest.raises(RuntimeError, match='disabled by the user'):
        core.run_core(tmp_path, 'blocked', [])
    assert not list(tmp_path.iterdir())

def test_com_session_is_blocked_before_initialization(monkeypatch):
    import sys
    from types import SimpleNamespace
    def unexpected(*args, **kwargs):
        raise AssertionError('COM must not be initialized')
    monkeypatch.setitem(sys.modules, 'pythoncom', SimpleNamespace(CoInitialize=unexpected))
    with pytest.raises(RuntimeError, match='disabled by the user'):
        AcadSession('HexFill.dll').__enter__()
