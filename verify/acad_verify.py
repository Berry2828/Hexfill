import argparse
import csv
import json
import math
import os
import subprocess
import tempfile
import time
from pathlib import Path
from cad_runtime_guard import require_cad_runtime

VERIFY_DIR = Path(__file__).parent.absolute()
PROJECT_DIR = VERIFY_DIR.parent
TEST_VARIABLE_VALUES = (
    ("CMDECHO", 0),
    ("OSMODE", 0),
    ("ORTHOMODE", 0),
    ("SNAPMODE", 0),
)
USER_INTERFACE_VARIABLE_VALUES = (
    ("FILEDIA", 1),
    ("CMDDIA", 1),
    ("DYNMODE", 3),
    ("DYNPROMPT", 1),
    ("SECURELOAD", 1),
)
PRESERVED_VARIABLE_NAMES = (
    *(name for name, _ in USER_INTERFACE_VARIABLE_VALUES),
    "USERR1",
)
SNAPSHOT_VARIABLE_NAMES = tuple(name for name, _ in TEST_VARIABLE_VALUES) + PRESERVED_VARIABLE_NAMES


class AcadSession:
    def __init__(self, dll_path):
        self.dll_path = str(Path(dll_path).expanduser().resolve())
        self.acad = None
        self.doc = None
        self.documents = None
        self.process_id = None
        self.window_prepared = False
        self.original_window_state = None
        self.original_variable_values = None
        self.last_restore_failures = []

    def __enter__(self):
        require_cad_runtime()
        import pythoncom
        import win32com.client

        pythoncom.CoInitialize()
        try:
            self.acad = win32com.client.DispatchEx("AutoCAD.Application")
            self.acad.Visible = True
            try:
                self.original_window_state = self.acad.WindowState
            except Exception:
                self.original_window_state = None
            self.process_id = self._get_process_id()
            self.documents = self._wait_for_documents(timeout=45)
            self.open_document()
            self.prepare_background_window()
            self.netload()
            return self
        except Exception:
            self.close()
            raise

    def __exit__(self, exc_type, exc, tb):
        self.close()

    def _get_process_id(self, timeout=30):
        import ctypes

        deadline = time.time() + timeout
        while time.time() < deadline:
            try:
                hwnd = int(self.acad.HWND)
                process_id = ctypes.c_ulong()
                ctypes.windll.user32.GetWindowThreadProcessId(hwnd, ctypes.byref(process_id))
                if process_id.value:
                    return int(process_id.value)
            except Exception:
                pass
            time.sleep(0.25)
        return None

    def prepare_background_window(self):
        if self.window_prepared:
            return
        self.window_prepared = True
        try:
            self.acad.WindowState = 2
        except Exception:
            pass
        try:
            import ctypes
            ctypes.windll.user32.ShowWindow(int(self.acad.HWND), 7)
        except Exception:
            pass

    def _wait_for_documents(self, timeout=45):
        deadline = time.time() + timeout
        last_error = None
        while time.time() < deadline:
            try:
                documents = self.acad.Documents
                _ = documents.Count
                return documents
            except Exception as exc:
                last_error = exc
                time.sleep(0.5)
        raise TimeoutError("AutoCAD documents collection was not ready: {}".format(last_error))

    @staticmethod
    def _set_document_variable(document, name, value, timeout=5):
        try:
            if document.GetVariable(name) == value:
                return True
        except Exception:
            pass

        deadline = time.time() + timeout
        typed_value = None
        if isinstance(value, int):
            try:
                import pythoncom
                import win32com.client
                typed_value = win32com.client.VARIANT(pythoncom.VT_I2, value)
            except Exception:
                typed_value = None
        while time.time() < deadline:
            for candidate in (value, typed_value):
                if candidate is None:
                    continue
                try:
                    document.SetVariable(name, candidate)
                    return True
                except Exception:
                    pass
            time.sleep(0.2)
        return False

    def _set_variable(self, name, value):
        if self.doc is None:
            return False
        return self._set_document_variable(self.doc, name, value)

    def prepare_document(self, document=None):
        target_document = document if document is not None else self.doc
        if target_document is None:
            return

        if self.original_variable_values is None:
            original_values = {}
            for name in SNAPSHOT_VARIABLE_NAMES:
                try:
                    original_values[name] = target_document.GetVariable(name)
                except Exception:
                    pass
            self.original_variable_values = original_values

        for name, value in TEST_VARIABLE_VALUES:
            self._set_document_variable(target_document, name, value)

    def restore_document_state(self, document=None):
        target_document = document if document is not None else self.doc
        if target_document is None or not self.original_variable_values:
            return True

        failures = []
        for name, value in self.original_variable_values.items():
            if not self._set_document_variable(target_document, name, value):
                failures.append("{}(set failed)".format(name))
                continue
            try:
                actual_value = target_document.GetVariable(name)
                if actual_value != value:
                    failures.append("{}({!r} != {!r})".format(name, actual_value, value))
            except Exception as exc:
                failures.append("{}(read failed: {})".format(name, exc))
        for name, value in USER_INTERFACE_VARIABLE_VALUES:
            if not self._set_document_variable(target_document, name, value):
                failures.append("{}(safe state set failed)".format(name))
                continue
            try:
                actual_value = target_document.GetVariable(name)
                if actual_value != value:
                    failures.append("{}({!r} != safe {!r})".format(name, actual_value, value))
            except Exception as exc:
                failures.append("{}(safe state read failed: {})".format(name, exc))
        self.last_restore_failures = failures
        return not failures

    def restore_application_state(self):
        if self.acad is None:
            return

        if self.original_variable_values:
            restore_document = self.doc
            if restore_document is None and self.documents is not None:
                try:
                    if self.documents.Count > 0:
                        restore_document = self.documents.Item(0)
                except Exception:
                    restore_document = None
            if restore_document is not None:
                self.restore_document_state(restore_document)

    def open_document(self):
        if self.documents is None:
            self.documents = self._wait_for_documents(timeout=45)
        self.doc = self.add_document()
        time.sleep(0.8)
        self.prepare_document()

    def add_document(self, timeout=45):
        deadline = time.time() + timeout
        last_error = None
        while time.time() < deadline:
            try:
                return self.documents.Add("")
            except Exception as exc:
                last_error = exc
                time.sleep(0.5)
        raise TimeoutError("AutoCAD rejected document creation: {}".format(last_error))

    @staticmethod
    def activate_document(document, timeout=30):
        deadline = time.time() + timeout
        last_error = None
        while time.time() < deadline:
            try:
                document.Activate()
                return
            except Exception as exc:
                last_error = exc
                time.sleep(0.25)
        raise TimeoutError("AutoCAD rejected document activation: {}".format(last_error))

    def close_document(self, document=None):
        target_document = document if document is not None else self.doc
        if target_document is None:
            return
        self.doc = target_document
        try:
            try:
                self.activate_document(target_document, timeout=5)
            except Exception:
                pass
            try:
                if int(target_document.GetVariable("CMDACTIVE")) != 0:
                    target_document.SendCommand("\x03\x03")
                    self.wait_idle(timeout=5)
            except Exception:
                pass
            if not self.restore_document_state(target_document):
                print("WARNING: AutoCAD system variables were not fully restored: {}".format(
                    ", ".join(self.last_restore_failures)))
            target_document.Close(False)
            time.sleep(0.4)
        except Exception:
            pass
        finally:
            self.doc = None

    def close(self):
        self.close_document()
        self.restore_application_state()

        if not self.process_id and self.acad is not None:
            self.process_id = self._get_process_id(timeout=3)

        graceful_exit = False
        try:
            if self.acad is not None:
                self.acad.Quit()
                graceful_exit = self._wait_owned_process_exit(timeout=20)
        except Exception:
            pass
        if not graceful_exit:
            self.restore_application_state()
            self._kill_owned_process_if_running()
        self.acad = None

        try:
            import pythoncom
            pythoncom.CoUninitialize()
        except Exception:
            pass

    def _kill_owned_process_if_running(self):
        if not self.process_id:
            return
        try:
            probe = subprocess.run([
                "powershell",
                "-NoProfile",
                "-Command",
                "Get-Process -Id {} -ErrorAction SilentlyContinue | Select-Object -ExpandProperty ProcessName".format(self.process_id),
            ], capture_output=True, text=True, timeout=5)
            if "acad" in probe.stdout.lower():
                subprocess.run(["taskkill", "/F", "/PID", str(self.process_id)], capture_output=True, timeout=8)
                subprocess.run([
                    "powershell",
                    "-NoProfile",
                    "-Command",
                    "Wait-Process -Id {} -Timeout 10 -ErrorAction SilentlyContinue".format(self.process_id),
                ], capture_output=True, text=True, timeout=12)
        except Exception:
            pass

    def _wait_owned_process_exit(self, timeout=20):
        if not self.process_id:
            return True
        try:
            subprocess.run([
                "powershell",
                "-NoProfile",
                "-Command",
                "Wait-Process -Id {} -Timeout {} -ErrorAction SilentlyContinue".format(
                    self.process_id, int(timeout)),
            ], capture_output=True, text=True, timeout=timeout + 3)
            probe = subprocess.run([
                "powershell",
                "-NoProfile",
                "-Command",
                "Get-Process -Id {} -ErrorAction SilentlyContinue | Select-Object -ExpandProperty ProcessName".format(
                    self.process_id),
            ], capture_output=True, text=True, timeout=5)
            return "acad" not in probe.stdout.lower()
        except Exception:
            return False

    def wait_idle(self, timeout=60):
        deadline = time.time() + timeout
        last_active = None
        stable_since = None
        while time.time() < deadline:
            try:
                active = int(self.doc.GetVariable("CMDACTIVE"))
                if active == 0:
                    if last_active == 0:
                        if stable_since is None:
                            stable_since = time.time()
                        elif time.time() - stable_since > 0.8:
                            return True
                    else:
                        stable_since = time.time()
                else:
                    stable_since = None
                last_active = active
            except Exception:
                stable_since = None
            time.sleep(0.25)
        return False

    def send_command(self, command, timeout=60):
        self.doc.SendCommand(command)
        if not self.wait_idle(timeout):
            raise TimeoutError("AutoCAD command did not become idle")

    def netload(self):
        try:
            if not self._set_document_variable(self.doc, "FILEDIA", 0):
                raise RuntimeError("Could not temporarily disable FILEDIA for NETLOAD")
            if not self._set_document_variable(self.doc, "SECURELOAD", 0):
                raise RuntimeError("Could not temporarily disable SECURELOAD for NETLOAD")
            command = '_.NETLOAD\n"{}"\n'.format(self.dll_path)
            self.send_command(command, timeout=90)
        finally:
            restore_failures = []
            for name, value in (("FILEDIA", 1), ("SECURELOAD", 1)):
                if not self._set_document_variable(self.doc, name, value):
                    restore_failures.append(name)
            if restore_failures:
                raise RuntimeError("NETLOAD did not restore: {}".format(", ".join(restore_failures)))

    def run_script(self, lines, timeout=60):
        command_names = {line.strip().upper() for line in lines}
        requires_file_prompt_suppression = bool(command_names.intersection({"HIX", "HAD"}))
        try:
            if requires_file_prompt_suppression and not self._set_document_variable(self.doc, "FILEDIA", 0):
                raise RuntimeError("Could not temporarily disable FILEDIA for command-line file input")
            self.send_command("\n".join(lines) + "\n", timeout=timeout)
            self.wait_modelspace_stable(timeout=10)
        finally:
            if requires_file_prompt_suppression:
                if not self._set_document_variable(self.doc, "FILEDIA", 1):
                    raise RuntimeError("Command-line file input did not restore FILEDIA")

    def wait_modelspace_stable(self, timeout=10):
        deadline = time.time() + timeout
        last_count = None
        stable_since = None
        while time.time() < deadline:
            try:
                count = int(self.doc.ModelSpace.Count)
                if count == last_count:
                    if stable_since is None:
                        stable_since = time.time()
                    elif time.time() - stable_since > 1.0:
                        return True
                else:
                    stable_since = time.time()
                    last_count = count
            except Exception:
                stable_since = None
            time.sleep(0.25)
        return False

    def get_polylines(self):
        polylines = []
        model_space = self.doc.ModelSpace
        for index in range(model_space.Count):
            entity = model_space.Item(index)
            if getattr(entity, "EntityName", None) != "AcDbPolyline":
                continue
            coords = list(entity.Coordinates)
            vertices = [(float(coords[i]), float(coords[i + 1])) for i in range(0, len(coords), 2)]
            bulges = []
            for vertex_index in range(len(vertices)):
                try:
                    bulges.append(float(entity.GetBulge(vertex_index)))
                except Exception:
                    bulges.append(0.0)
            polylines.append({
                "closed": bool(entity.Closed),
                "vertices": vertices,
                "area": abs(float(getattr(entity, "Area", 0.0))),
                "layer": str(getattr(entity, "Layer", "")),
                "handle": str(getattr(entity, "Handle", "")),
                "bulges": bulges,
            })
        return polylines

    def get_layer_entities(self, layer_name):
        entities = []
        model_space = self.doc.ModelSpace
        for index in range(model_space.Count):
            entity = model_space.Item(index)
            if str(getattr(entity, "Layer", "")).lower() != layer_name.lower():
                continue
            entities.append({
                "name": str(getattr(entity, "EntityName", "")),
                "layer": str(getattr(entity, "Layer", "")),
            })
        return entities

    def get_circles(self):
        circles = []
        model_space = self.doc.ModelSpace
        for index in range(model_space.Count):
            entity = model_space.Item(index)
            if getattr(entity, "EntityName", None) != "AcDbCircle":
                continue
            center = list(entity.Center)
            circles.append({
                "center": (float(center[0]), float(center[1])),
                "radius": float(entity.Radius),
                "layer": str(getattr(entity, "Layer", "")),
                "handle": str(getattr(entity, "Handle", "")),
                "color": int(getattr(entity, "Color", -1)),
            })
        return circles

    def get_circle_count_fast(self):
        selection = self._select_circles(5)
        try:
            return int(selection.Count)
        finally:
            selection.Delete()

    def get_circles_in_window(self, minimum, maximum):
        selection = self._select_circles(0, minimum, maximum)
        circles = []
        try:
            for index in range(selection.Count):
                entity = selection.Item(index)
                center = list(entity.Center)
                circles.append({
                    "center": (float(center[0]), float(center[1])),
                    "radius": float(entity.Radius),
                    "layer": str(getattr(entity, "Layer", "")),
                    "handle": str(getattr(entity, "Handle", "")),
                    "color": int(getattr(entity, "Color", -1)),
                })
            return circles
        finally:
            selection.Delete()

    def _select_circles(self, mode, minimum=None, maximum=None):
        import pythoncom
        import win32com.client

        name = "HEXVERIFY_{}".format(int(time.time() * 1000000))
        selection = self.doc.SelectionSets.Add(name)
        filter_type = win32com.client.VARIANT(pythoncom.VT_ARRAY | pythoncom.VT_I2, [0])
        filter_data = win32com.client.VARIANT(pythoncom.VT_ARRAY | pythoncom.VT_VARIANT, ["CIRCLE"])
        if mode == 5:
            selection.Select(mode, None, None, filter_type, filter_data)
        else:
            first = win32com.client.VARIANT(
                pythoncom.VT_ARRAY | pythoncom.VT_R8,
                [float(minimum[0]), float(minimum[1]), 0.0])
            second = win32com.client.VARIANT(
                pythoncom.VT_ARRAY | pythoncom.VT_R8,
                [float(maximum[0]), float(maximum[1]), 0.0])
            selection.Select(mode, first, second, filter_type, filter_data)
        return selection

    def get_texts_by_layer(self, layer_name):
        texts = []
        model_space = self.doc.ModelSpace
        for index in range(model_space.Count):
            entity = model_space.Item(index)
            if str(getattr(entity, "Layer", "")).lower() != layer_name.lower():
                continue
            if str(getattr(entity, "EntityName", "")) not in ("AcDbText", "AcDbMText"):
                continue
            texts.append({
                "name": str(getattr(entity, "EntityName", "")),
                "layer": str(getattr(entity, "Layer", "")),
                "text": str(getattr(entity, "TextString", "")),
            })
        return texts

    def get_entities(self):
        entities = []
        model_space = self.doc.ModelSpace
        for index in range(model_space.Count):
            entity = model_space.Item(index)
            entities.append({
                "name": str(getattr(entity, "EntityName", "")),
                "layer": str(getattr(entity, "Layer", "")),
            })
        return entities


def latest_dll():
    build = PROJECT_DIR / "bin" / "Release" / "HexFill.dll"
    if build.exists():
        return build
    desktop = Path.home() / "Desktop"
    dlls = sorted(desktop.glob("HexFill_*.dll"), key=lambda path: path.stat().st_mtime, reverse=True)
    return dlls[0] if dlls else None


def default_hplit_setup():
    return [
        "_.PLINE",
        "0,0",
        "100,0",
        "100,100",
        "0,100",
        "_C",
    ]


def arc_notched_hplit_setup():
    return [
        "(entmakex (list '(0 . \"LWPOLYLINE\") '(100 . \"AcDbEntity\") '(100 . \"AcDbPolyline\") '(90 . 8) '(70 . 1) (cons 10 (list 0 0)) (cons 10 (list 160 0)) (cons 10 (list 160 140)) (cons 10 (list 110 140)) (cons 10 (list 110 70)) (cons 42 -1.0) (cons 10 (list 50 70)) (cons 10 (list 50 140)) (cons 10 (list 0 140))))",
    ]


def multi_rect_hplit_setup():
    return [
        "_.PLINE",
        "0,0",
        "100,0",
        "100,100",
        "0,100",
        "_C",
        "_.PLINE",
        "150,0",
        "250,0",
        "250,100",
        "150,100",
        "_C",
        "_.PLINE",
        "300,0",
        "400,0",
        "400,100",
        "300,100",
        "_C",
    ]


def two_rect_hplit_setup():
    return [
        "_.PLINE",
        "0,0",
        "100,0",
        "100,100",
        "0,100",
        "_C",
        "_.PLINE",
        "150,0",
        "250,0",
        "250,100",
        "150,100",
        "_C",
    ]


def hgap_open_gap_markers_script():
    return [
        "_.ERASE",
        "_ALL",
        "",
        "_.PLINE",
        "0,0",
        "100,0",
        "100,100",
        "0,100",
        "0,0.2",
        "",
        "_.PLINE",
        "150,0",
        "250,0",
        "250,100",
        "150,100",
        "150,10",
        "",
        "HGAP",
        "_W",
        "-20,-20",
        "270,130",
        "",
    ]


def hgap_overlap_lines_script():
    return [
        "_.ERASE",
        "_ALL",
        "",
        "(entmakex (list '(0 . \"LINE\") (cons 10 (list 0 0 0)) (cons 11 (list 50 0 0))))",
        "(entmakex (list '(0 . \"LINE\") (cons 10 (list 50 0 0)) (cons 11 (list 100 0 0))))",
        "(entmakex (list '(0 . \"LINE\") (cons 10 (list 20 0 0)) (cons 11 (list 80 0 0))))",
        "(entmakex (list '(0 . \"LINE\") (cons 10 (list 100 0 0)) (cons 11 (list 100 100 0))))",
        "(entmakex (list '(0 . \"LINE\") (cons 10 (list 100 100 0)) (cons 11 (list 0 100 0))))",
        "(entmakex (list '(0 . \"LINE\") (cons 10 (list 0 100 0)) (cons 11 (list 0 0.2 0))))",
        "HGAP",
        "_W",
        "-10,-10",
        "110,110",
        "",
    ]


def hgap_overlap_polyline_script():
    return [
        "_.ERASE",
        "_ALL",
        "",
        "_.PLINE",
        "0,0",
        "100,0",
        "100,100",
        "0,100",
        "0,0",
        "100,0",
        "",
        "HGAP",
        "_W",
        "-10,-10",
        "110,110",
        "",
    ]


def hgap_open_polyline_vertex_cleanup_script():
    # 开放 LWPOLYLINE（closed=0），带共线冗余 (25,0) 与重复顶点 (50,0)。
    # HGAP 应清理顶点：0,0 -> 50,0 -> 50,50（3 顶点），并与缺口标记。
    return [
        "_.ERASE",
        "_ALL",
        "",
        '(entmakex (list \'(0 . "LWPOLYLINE") \'(100 . "AcDbEntity") \'(100 . "AcDbPolyline") (cons 90 5) (cons 70 0)'
        ' (cons 10 (list 0 0)) (cons 10 (list 25 0)) (cons 10 (list 50 0))'
        ' (cons 10 (list 50 0)) (cons 10 (list 50 50))))',
        "HGAP",
        "_W",
        "-10,-10",
        "110,110",
        "",
    ]


def hgap_closed_polyline_vertex_cleanup_script():
    # 闭合矩形（LWPOLYLINE, closed=1），带：
    #   - 共线冗余顶点 (25,0)（位于 0,0 -> 50,0 边上）
    #   - 重复顶点 (50,0)（连续出现两次）
    # HGAP 应清理这两处顶点问题但不拆段/不重建：结果仍为 1 条闭合聚合线、4 顶点。
    return [
        "_.ERASE",
        "_ALL",
        "",
        '(entmakex (list \'(0 . "LWPOLYLINE") \'(100 . "AcDbEntity") \'(100 . "AcDbPolyline") (cons 90 6) (cons 70 1)'
        ' (cons 10 (list 0 0)) (cons 10 (list 25 0)) (cons 10 (list 50 0))'
        ' (cons 10 (list 50 0)) (cons 10 (list 50 100)) (cons 10 (list 0 100))))',
        "HGAP",
        "_W",
        "-10,-10",
        "110,110",
        "",
    ]


def hgap_duplicate_closed_polylines_untouched_script():
    # 两条完全相同的闭合矩形：HGAP 对"重复/重叠聚合线维持不处理"——
    # 不得合并去重、不得拆段重建，两条各 4 顶点原样保留。
    return [
        "_.ERASE",
        "_ALL",
        "",
        '(entmakex (list \'(0 . "LWPOLYLINE") \'(100 . "AcDbEntity") \'(100 . "AcDbPolyline") (cons 90 4) (cons 70 1)'
        ' (cons 10 (list 0 0)) (cons 10 (list 100 0)) (cons 10 (list 100 100)) (cons 10 (list 0 100))))',
        '(entmakex (list \'(0 . "LWPOLYLINE") \'(100 . "AcDbEntity") \'(100 . "AcDbPolyline") (cons 90 4) (cons 70 1)'
        ' (cons 10 (list 0 0)) (cons 10 (list 100 0)) (cons 10 (list 100 100)) (cons 10 (list 0 100))))',
        "HGAP",
        "_W",
        "-10,-10",
        "110,110",
        "",
    ]


def hgap_near_endpoint_line_join_script():
    return [
        "_.ERASE", "_ALL", "",
        "_.LINE", "0,0", "100,0", "",
        "_.LINE", "100.2,0", "100.2,100", "",
        "_.LINE", "100.2,100.2", "0,100.2", "",
        "_.LINE", "0,100.2", "0,0.2", "",
        "HGAP", "_W", "-10,-10", "110,110", "",
    ]


def hjoin_gap_rectangles_script():
    return [
        "_.ERASE", "_ALL", "",
        "_.PLINE", "0,0", "49.95,0", "49.95,100", "0,100", "_C",
        "_.PLINE", "50.05,0", "100,0", "100,100", "50.05,100", "_C",
        "HJOIN", "_W", "-10,-10", "110,110", "",
    ]


def hjoin_auto_merge_script():
    return [
        "_.ERASE", "_ALL", "",
        "_.PLINE", "0,0", "50,0", "50,100", "0,100", "_C",
        "_.PLINE", "50,0", "100,0", "100,100", "50,100", "_C",
        "HJOIN", "_W", "-10,-10", "110,110", "",
    ]


def hjoin_pin_shape_script():
    return [
        "_.ERASE", "_ALL", "",
        "_.PLINE", "0,0", "50,0", "50,50", "0,50", "_C",
        "_.PLINE", "50,0", "100,0", "100,50", "50,50", "_C",
        "_.PLINE", "25,50", "75,50", "75,100", "25,100", "_C",
        "HJOIN", "_W", "-10,-10", "110,110", "",
    ]


def hjoin_hplit_pin_restore_script():
    return [
        "_.ERASE", "_ALL", "",
        "_.PLINE", "0,0", "100,0", "100,50", "75,50", "75,100", "25,100", "25,50", "0,50", "_C",
        "HPLIT", "_W", "-10,-10", "110,110", "", "_non", "-10,50", "_non", "110,50", "",
        "HPLIT", "_W", "-10,-10", "110,60", "", "_non", "50,-10", "_non", "50,60", "",
        "HJOIN", "_W", "-10,-10", "110,110", "",
    ]


def hjoin_curved_pin_script():
    curve_points = []
    for step in range(1, 31):
        parameter = step / 30.0
        smooth = parameter * parameter * (3.0 - 2.0 * parameter)
        curve_points.append((90.0 - 25.0 * smooth, 100.0 - 50.0 * parameter))

    lines = [
        "_.ERASE", "_ALL", "",
        "_.PLINE", "0,50", "30,50", "30,100", "0,100", "_C",
        "_.PLINE", "30,50", "50,50", "50,100", "30,100", "_C",
        "_.PLINE", "0,0", "40,0", "40,50", "0,50", "_C",
        "_.PLINE", "50,50", "50,100", "90,100",
    ]
    lines.extend("{:.8f},{:.8f}".format(x, y) for x, y in curve_points)
    lines.extend(["_C", "HJOIN", "_W", "-10,-10", "100,110", ""])
    return lines


def hjoin_bulge_pin_script():
    return [
        "_.ERASE", "_ALL", "",
        "_.PLINE", "0,50", "30,50", "30,100", "0,100", "_C",
        "_.PLINE", "30,50", "50,50", "50,100", "30,100", "_C",
        "_.PLINE", "0,0", "40,0", "40,50", "0,50", "_C",
        "(entmakex (list '(0 . \"LWPOLYLINE\") '(100 . \"AcDbEntity\") '(100 . \"AcDbPolyline\") '(90 . 4) '(70 . 1) (cons 10 (list 50 50)) (cons 10 (list 50 100)) (cons 10 (list 90 100)) '(42 . 0.6) (cons 10 (list 65 50))))",
        "HJOIN", "_W", "-10,-10", "110,120", "",
    ]


def hjoin_mixed_batch_script():
    curve_points = []
    for step in range(1, 31):
        parameter = step / 30.0
        smooth = parameter * parameter * (3.0 - 2.0 * parameter)
        curve_points.append((490.0 - 25.0 * smooth, 100.0 - 50.0 * parameter))

    lines = [
        "_.ERASE", "_ALL", "",
        "_.-LAYER", "_M", "HJOIN_GAP", "",
        "_.PLINE", "0,0", "49.95,0", "49.95,100", "0,100", "_C",
        "_.PLINE", "50.05,0", "100,0", "100,100", "50.05,100", "_C",
        "_.-LAYER", "_M", "HJOIN_PIN", "",
        "_.PLINE", "200,0", "250,0", "250,50", "200,50", "_C",
        "_.PLINE", "250,0", "300,0", "300,50", "250,50", "_C",
        "_.PLINE", "225,50", "275,50", "275,100", "225,100", "_C",
        "_.-LAYER", "_M", "HJOIN_CURVE", "",
        "_.PLINE", "400,50", "430,50", "430,100", "400,100", "_C",
        "_.PLINE", "430,50", "450,50", "450,100", "430,100", "_C",
        "_.PLINE", "400,0", "440,0", "440,50", "400,50", "_C",
        "_.PLINE", "450,50", "450,100", "490,100",
    ]
    lines.extend("{:.8f},{:.8f}".format(x, y) for x, y in curve_points)
    lines.extend([
        "_C",
        "_.-LAYER", "_M", "HJOIN_BULGE", "",
        "_.PLINE", "600,50", "630,50", "630,100", "600,100", "_C",
        "_.PLINE", "630,50", "650,50", "650,100", "630,100", "_C",
        "_.PLINE", "600,0", "640,0", "640,50", "600,50", "_C",
        "(entmakex (list '(0 . \"LWPOLYLINE\") '(100 . \"AcDbEntity\") '(100 . \"AcDbPolyline\") '(90 . 4) '(70 . 1) (cons 10 (list 650 50)) (cons 10 (list 650 100)) (cons 10 (list 690 100)) '(42 . 0.6) (cons 10 (list 665 50))))",
        "_.-LAYER", "_M", "HJOIN_RESTORE", "",
        "_.PLINE", "800,0", "900,0", "900,50", "875,50", "875,100", "825,100", "825,50", "800,50", "_C",
        "HPLIT", "_W", "790,-10", "910,110", "", "_non", "790,50", "_non", "910,50", "",
        "HPLIT", "_W", "790,-10", "910,60", "", "_non", "850,-10", "_non", "850,60", "",
        "HJOIN", "_W", "-10,-10", "910,120", "",
    ])
    return lines


def hset_hplit_spacing_script():
    lines = ["_.ERASE", "_ALL", "", "HSET", "S", "0.2"]
    lines.extend(default_hplit_setup())
    lines.extend([
        "HPLIT", "_W", "-20,-20", "120,120", "",
        "_non", "50,-10", "_non", "50,110", "",
        "HSET", "D",
    ])
    return lines


def hset_hgap_threshold_script():
    return [
        "_.ERASE", "_ALL", "",
        "HSET", "G", "0.1",
        "_.PLINE", "0,0", "100,0", "100,100", "0,100", "0,0.2", "",
        "HGAP", "_W", "-10,-10", "110,110", "",
        "HSET", "D",
    ]


def hset_hjoin_distance_script():
    return [
        "_.ERASE", "_ALL", "",
        "HSET", "J", "0.05",
        "_.PLINE", "0,0", "49.95,0", "49.95,100", "0,100", "_C",
        "_.PLINE", "50.05,0", "100,0", "100,100", "50.05,100", "_C",
        "HJOIN", "_W", "-10,-10", "110,110", "",
        "HSET", "D",
    ]


def hchk_normal_script():
    return [
        "_.ERASE", "_ALL", "",
        "_.PLINE", "0,0", "100,0", "100,100", "0,100", "_C",
        "HCHK", "_W", "-10,-10", "110,110", "",
    ]


def hchk_bulge_curve_script():
    return [
        "_.ERASE", "_ALL", "",
        "(entmakex (list '(0 . \"LWPOLYLINE\") '(100 . \"AcDbEntity\") '(100 . \"AcDbPolyline\") '(90 . 4) '(70 . 1) (cons 10 (list 0 0)) (cons 10 (list 0 50)) (cons 10 (list 40 50)) '(42 . 0.6) (cons 10 (list 15 0))))",
        "HCHK", "_W", "-10,-10", "50,70", "",
    ]


def hchk_problem_script():
    return [
        "_.ERASE", "_ALL", "",
        "_.-LAYER", "_M", "H_NUM", "",
        "_.PLINE", "0,0", "100,0", "100,100", "0,0.2", "",
        "_.PLINE", "150,0", "250,100", "150,100", "250,0", "_C",
        "_.LINE", "300,0", "360,0", "",
        "_.LINE", "320,0", "340,0", "",
        "(entmakex (list '(0 . \"TEXT\") (cons 8 \"H_NUM\") (cons 10 (list 400.0 0.0 0.0)) (cons 40 2.0) (cons 50 0.0) (cons 1 \"99_99\")))",
        "_.-LAYER", "_S", "0", "",
        "HCHK", "_W", "-10,-10", "420,120", "",
    ]


def hnm_split_renumber_script():
    return [
        "_.ERASE", "_ALL", "",
        "_.PLINE", "0,0", "100,0", "100,100", "0,100", "_C",
        "_.PLINE", "20,20", "80,20", "80,80", "20,80", "_C",
        "HNM", "1", "_W", "-10,-10", "110,110", "",
        "HPLIT", "_W", "15,15", "85,85", "", "_non", "50,10", "_non", "50,90", "",
        "HNM", "1", "_W", "-10,-10", "110,110", "",
    ]


def hnm_hplit_no_confirmation_script():
    return [
        "_.ERASE", "_ALL", "",
        "_.PLINE", "0,0", "100,0", "100,100", "0,100", "_C",
        "_.PLINE", "20,20", "80,20", "80,80", "20,80", "_C",
        "HNM", "1", "_W", "-10,-10", "110,110", "",
        "HPLIT", "_W", "15,15", "85,85", "", "_non", "50,10", "_non", "50,90", "",
        "HNM", "1", "_W", "-10,-10", "110,110", "",
    ]


def hcln_orphan_cleanup_script():
    return [
        "_.ERASE", "_ALL", "",
        "_.PLINE", "0,0", "100,0", "100,100", "0,100", "_C",
        "_.PLINE", "20,20", "80,20", "80,80", "20,80", "_C",
        "HNM", "1", "_W", "-10,-10", "110,110", "",
        "HPLIT", "_W", "15,15", "85,85", "", "_non", "50,10", "_non", "50,90", "",
        "HCLN", "_W", "-10,-10", "110,110", "",
    ]


def hnm_single_root_skip_script():
    return [
        "_.ERASE", "_ALL", "",
        "_.PLINE", "0,0", "100,0", "100,100", "0,100", "_C",
        "HNM", "1", "_W", "-10,-10", "110,110", "",
    ]


def hnm_undo_then_renumber_script():
    return [
        "_.ERASE", "_ALL", "",
        "_.PLINE", "0,0", "100,0", "100,100", "0,100", "_C",
        "_.PLINE", "20,20", "80,20", "80,80", "20,80", "_C",
        "HNM", "1", "_W", "-10,-10", "110,110", "",
        "_.U",
        "HNM", "1", "_W", "-10,-10", "110,110", "",
    ]


def grid_regeneration_safety_script():
    return [
        "_.ERASE", "_ALL", "",
        "_.-LAYER", "_M", "SAFE_HGR", "",
        "_.PLINE", "0,0", "0.01,0", "0.01,0.01", "0,0.01", "_C",
        "_.CIRCLE", "0.005,0.005", "0.015",
        "_.CHPROP", "_L", "", "_C", "1", "",
        "_.-LAYER", "_M", "SAFE_HRS", "",
        "_.PLINE", "10,0", "10.01,0", "10.01,0.01", "10,0.01", "_C",
        "_.CIRCLE", "10.002,0.005", "0.015",
        "_.CIRCLE", "10.008,0.005", "0.015",
        "_.-LAYER", "_M", "SAFE_HRD", "",
        "_.PLINE", "20,0", "20.01,0", "20.01,0.01", "20,0.01", "_C",
        "_.CIRCLE", "20.002,0.005", "0.015",
        "_.CIRCLE", "20.008,0.005", "0.015",
        "_.-LAYER", "_S", "0", "",
        "_.PLINE", "30,0", "31,0", "31,1", "30,1", "_C",
        "_.ZOOM", "_E",
        "HDIA", "HDIA",
        "HGR", "_W", "-0.1,-0.1", "0.1,0.1", "", "0.1",
        "HRS", "_W", "9.9,-0.1", "10.1,0.1", "", "10",
        "HRD", "_W", "19.9,-0.1", "20.1,0.1", "",
        "HGR", "_W", "29.9,-0.1", "31.1,1.1", "", "0.2",
        "HRS", "_W", "29.9,-0.1", "31.1,1.1", "", "10",
        "HRD", "_W", "29.9,-0.1", "31.1,1.1", "",
    ]


def hplit_script(case):
    points = case["points"]
    setup_lines = case.get("setup_lines") or default_hplit_setup()
    selection_window = case.get("selection_window", ((-20, -20), (120, 120)))
    (x1, y1), (x2, y2) = selection_window
    def coordinate(x, y):
        return "{:.8f},{:.8f}".format(x, y)
    lines = [
        "_.ERASE",
        "_ALL",
        "",
        "HPLIT",
        "_W",
        coordinate(x1, y1),
        coordinate(x2, y2),
        "",
    ]
    lines[3:3] = setup_lines
    for x, y in points:
        lines.append("_non")
        lines.append(coordinate(x, y))
    lines.append("")
    return lines


def hplit_batch_script(cases):
    lines = ["_.ERASE", "_ALL", ""]
    regions = []
    for index, case in enumerate(cases):
        offset_x = 600.0 * (index + 1)
        setup_lines = case.get("setup_lines") or default_hplit_setup()
        (x1, y1), (x2, y2) = case.get("selection_window", ((-20, -20), (120, 120)))

        lines.extend(setup_lines)
        lines.extend([
            "_.MOVE", "_W",
            "{:.8f},{:.8f}".format(x1, y1),
            "{:.8f},{:.8f}".format(x2, y2),
            "", "0,0", "{:.8f},0".format(offset_x),
        ])
        regions.append((case, offset_x, x1 + offset_x, x2 + offset_x))

    lines.extend(["_.ZOOM", "_E"])
    for case, offset_x, region_min_x, region_max_x in regions:
        (x1, y1), (x2, y2) = case.get("selection_window", ((-20, -20), (120, 120)))
        lines.extend([
            "(sssetfirst nil nil)",
            "HPLIT", "_W",
            "{:.8f},{:.8f}".format(region_min_x, y1),
            "{:.8f},{:.8f}".format(region_max_x, y2),
            "",
        ])
        for x, y in case["points"]:
            lines.extend(["_non", "{:.8f},{:.8f}".format(x + offset_x, y)])
        lines.append("")
    return lines, regions


def distance(point, expected):
    return math.hypot(point[0] - expected[0], point[1] - expected[1])


def average_nearest_spacing(circles):
    points = [circle["center"] for circle in circles]
    nearest = []
    for index, point in enumerate(points):
        distances = [distance(point, other) for other_index, other in enumerate(points) if other_index != index]
        if distances:
            nearest.append(min(distances))
    return sum(nearest) / len(nearest) if nearest else 0.0


def contains_near(vertices, expected, tolerance=0.18):
    return any(distance(vertex, expected) <= tolerance for vertex in vertices)


def assert_hplit_result(polylines, case):
    expected_points = case.get("expected", [])
    expected_count = case.get("expected_count", 2)
    errors = []
    if len(polylines) != expected_count:
        errors.append("expected {} polylines, got {}".format(expected_count, len(polylines)))
        return False, errors

    for index, polyline in enumerate(polylines):
        if not polyline["closed"]:
            errors.append("polyline {} is not closed".format(index))
        if len(polyline["vertices"]) < 3:
            errors.append("polyline {} has fewer than 3 vertices".format(index))
        if polyline["area"] <= 0.01:
            errors.append("polyline {} area is too small: {:.6f}".format(index, polyline["area"]))

    all_vertices = []
    for polyline in polylines:
        all_vertices.extend(polyline["vertices"])

    for expected in expected_points:
        if not contains_near(all_vertices, expected, case.get("point_tolerance", 0.18)):
            errors.append("missing vertex near ({:.2f},{:.2f})".format(expected[0], expected[1]))

    total_area = sum(polyline["area"] for polyline in polylines)
    area_min = case.get("area_min", 9800.0)
    area_max = case.get("area_max", 10005.0)
    if total_area < area_min or total_area > area_max:
        errors.append("unexpected total area {:.3f}".format(total_area))

    return len(errors) == 0, errors


HGAP_MARKER_LAYER = "HGAP_未闭合标识"

HGAP_CASES = [
    {
        "name": "open_gap_markers",
        "script": hgap_open_gap_markers_script,
        "expected_polylines": 2,
        "expected_closed": 1,
        "expected_open": 1,
        "marker_min": 3,
        "expected_vertices": [4, 5],
    },
    {
        "name": "overlap_lines_cleanup",
        "script": hgap_overlap_lines_script,
        "expected_polylines": 1,
        "expected_closed": 1,
        "expected_open": 0,
        "marker_min": 0,
        "expected_non_marker_lines": 0,
        "expected_vertices": [4],
        # 2026-08-16：缺口端点 (0,0.2) 位于 (0,100)->(0,0) 连线上，
        # 8-15 起的共线冗余顶点清理会将其删除（旧期望 5 顶点含 (0,0.2) 已过时）。
        "expected_points": [(100, 0), (100, 100), (0, 100), (0, 0)],
    },
    {
        "name": "overlap_polyline_cleanup",
        "script": hgap_overlap_polyline_script,
        "expected_polylines": 1,
        "expected_closed": 1,
        "expected_open": 0,
        "marker_min": 0,
        "expected_non_marker_lines": 0,
        "expected_vertices": [4],
    },
    {
        "name": "open_polyline_vertex_cleanup",
        "script": hgap_open_polyline_vertex_cleanup_script,
        "expected_polylines": 1,
        "expected_closed": 0,
        "expected_open": 1,
        "marker_min": 3,
        "expected_non_marker_lines": 0,
        "expected_vertices": [3],
        "expected_points": [(0, 0), (50, 0), (50, 50)],
    },
    {
        "name": "closed_polyline_vertex_cleanup",
        "script": hgap_closed_polyline_vertex_cleanup_script,
        "expected_polylines": 1,
        "expected_closed": 1,
        "expected_open": 0,
        "marker_min": 0,
        "expected_non_marker_lines": 0,
        "expected_vertices": [4],
        "expected_points": [(0, 0), (50, 0), (50, 100), (0, 100)],
    },
    {
        "name": "duplicate_closed_polylines_untouched",
        "script": hgap_duplicate_closed_polylines_untouched_script,
        "expected_polylines": 2,
        "expected_closed": 2,
        "expected_open": 0,
        "marker_min": 0,
        "expected_non_marker_lines": 0,
        "expected_vertices": [4, 4],
        "expected_points": [(0, 0), (100, 0), (100, 100), (0, 100)],
    },
    {
        "name": "near_endpoint_lines_join",
        "script": hgap_near_endpoint_line_join_script,
        "expected_polylines": 1,
        "expected_closed": 1,
        "expected_open": 0,
        "marker_min": 0,
        "expected_non_marker_lines": 0,
        "expected_vertices": [4],
        # 2026-08-16：缺口端点 (100.2,100) 与 (0,0.2) 位于首尾边上，
        # 共线冗余顶点清理会将其删除（旧期望 6 顶点已过时）。
        "expected_points": [(100.2, 0), (100.2, 100.2), (0, 100.2), (0, 0)],
    },
]


HJOIN_CASES = [
    {
        "name": "mixed_regions_single_command",
        "script": hjoin_mixed_batch_script,
        "expected_polylines": 5,
        "expected_closed": 5,
        "area_min": 36050.0,
        "area_max": 36110.0,
        "expected_regions": [
            {"name": "gap", "min_x": -10, "max_x": 110, "count": 1, "area_min": 9990.0, "area_max": 10010.0, "vertex_min": 4, "layer": "HJOIN_GAP"},
            {"name": "pin", "min_x": 190, "max_x": 310, "count": 1, "area_min": 7490.0, "area_max": 7510.0, "vertex_min": 8, "layer": "HJOIN_PIN"},
            {"name": "curve", "min_x": 390, "max_x": 510, "count": 1, "area_min": 5865.0, "area_max": 5885.0, "vertex_min": 30, "vertices": [(486.55185185, 88.33333333), (468.44814815, 61.66666667), (440, 50), (465, 50)], "layer": "HJOIN_CURVE"},
            {"name": "bulge", "min_x": 590, "max_x": 710, "count": 1, "area_min": 5000.0, "area_max": 7500.0, "vertex_min": 12, "layer": "HJOIN_BULGE"},
            {"name": "hplit_restore", "min_x": 790, "max_x": 910, "count": 1, "area_min": 7460.0, "area_max": 7510.0, "vertex_min": 8, "vertices": [(825, 49.95), (875, 49.95), (825, 100), (875, 100)], "layer": "HJOIN_RESTORE"},
        ],
    },
]


HSET_CASES = [
    {
        "name": "hplit_spacing_0_2",
        "script": hset_hplit_spacing_script,
        "expected_polylines": 2,
        "expected_closed": 2,
        "expected_vertices": [(49.9, 0), (50.1, 100)],
        "point_tolerance": 0.08,
    },
    {
        "name": "hgap_threshold_0_1_keeps_0_2_open",
        "script": hset_hgap_threshold_script,
        "expected_polylines": 1,
        "expected_closed": 0,
        "expected_open": 1,
        "marker_min": 3,
    },
    {
        "name": "hjoin_distance_0_05_rejects_0_1_gap",
        "script": hset_hjoin_distance_script,
        "expected_polylines": 2,
        "expected_closed": 2,
        "expected_open": 0,
        "marker_min": 0,
    },
]


HCHK_CASES = [
    {
        "name": "normal_no_markers",
        "script": hchk_normal_script,
        "marker_min": 0,
        "marker_max": 0,
        "expected_polylines": 1,
    },
    {
        "name": "bulge_curve_no_false_markers",
        "script": hchk_bulge_curve_script,
        "expected_polylines": 1,
        "marker_min": 0,
        "marker_max": 0,
    },
    {
        "name": "problem_markers",
        "script": hchk_problem_script,
        "marker_min": 4,
        "expected_polylines": 2,
    },
]


HNM_CASES = [
    {
        "name": "undo_then_renumber",
        "script": hnm_undo_then_renumber_script,
        "expected_texts": 1,
        "expected_polylines": 2,
        "expected_closed": 2,
    },
    {
        "name": "split_renumber_cleans_orphan",
        "script": hnm_split_renumber_script,
        "expected_texts": 2,
        "expected_polylines": 3,
        "expected_closed": 3,
    },
    {
        "name": "hplit_numbered_no_confirmation",
        "script": hnm_hplit_no_confirmation_script,
        "expected_texts": 2,
        "expected_polylines": 3,
        "expected_closed": 3,
    },
    {
        "name": "hcln_cleans_orphan",
        "script": hcln_orphan_cleanup_script,
        "expected_texts": 0,
        "expected_polylines": 3,
        "expected_closed": 3,
    },
    {
        "name": "single_root_skip",
        "script": hnm_single_root_skip_script,
        "expected_texts": 0,
        "expected_polylines": 1,
        "expected_closed": 1,
    },
]


GRID_CASES = [
    {
        "name": "zero_grid_preserves_existing_circles",
        "script": grid_regeneration_safety_script,
        "expected_layers": {"SAFE_HGR": 1, "SAFE_HRS": 2, "SAFE_HRD": 2},
        "expected_colors": {"SAFE_HGR": [6], "SAFE_HRS": [1, 1], "SAFE_HRD": [1, 1]},
        "generated_region": {"min_x": 30.0, "max_x": 31.0, "minimum": 1},
    },
]


HPLIT_CASES = [
    {
        "name": "vertical",
        "points": [(50, -10), (50, 110)],
        "expected": [(49.95, 0), (49.95, 100), (50.05, 0), (50.05, 100)],
    },
    {
        "name": "l_bottom_right",
        "points": [(50, -10), (50, 30), (110, 30)],
        "expected": [(49.95, 30.05), (50.05, 29.95), (49.95, 0), (100, 30.05)],
    },
    {
        "name": "l_bottom_left",
        "points": [(50, -10), (50, 30), (-10, 30)],
        "expected": [(49.95, 29.95), (50.05, 30.05), (50.05, 0), (0, 30.05)],
    },
    {
        "name": "z_shape",
        "points": [(50, -10), (50, 30), (80, 30), (80, 110)],
        "expected": [(49.95, 30.05), (79.95, 30.05), (50.05, 29.95), (80.05, 29.95)],
    },
    {
        "name": "z_boundary_four_parts",
        "points": [(0, 70), (100, 70), (0, 30), (100, 30)],
        "expected": [(0, 70.05), (100, 70.05), (0, 29.95), (100, 29.95)],
        "expected_count": 4,
    },
    {
        "name": "arc_notched_concave",
        "points": [(70, -20), (70, 80)],
        "expected": [(69.95, 0), (70.05, 0), (69.95, 41.65), (70.05, 41.65)],
        "expected_count": 2,
        "setup_lines": arc_notched_hplit_setup(),
        "selection_window": ((-20, -20), (180, 160)),
        "area_min": 16600.0,
        "area_max": 16900.0,
        "point_tolerance": 0.3,
    },
    {
        "name": "multi_rect_two_split_one_skip",
        "points": [(50, -10), (50, 110), (200, 110), (200, -10)],
        "expected": [(49.95, 0), (50.05, 100), (199.95, 0), (200.05, 100)],
        "expected_count": 5,
        "setup_lines": multi_rect_hplit_setup(),
        "selection_window": ((-20, -20), (420, 130)),
        "area_min": 29700.0,
        "area_max": 30010.0,
        "point_tolerance": 0.3,
    },
    {
        "name": "no_extend_partial_last",
        "points": [(50, -10), (50, 110), (200, 110), (200, 50)],
        "expected": [(49.95, 0), (50.05, 100)],
        "expected_count": 3,
        "setup_lines": two_rect_hplit_setup(),
        "selection_window": ((-20, -20), (270, 130)),
        "area_min": 19700.0,
        "area_max": 20010.0,
        "point_tolerance": 0.3,
    },
]


def selected_hgap_cases(case_name):
    if not case_name:
        return HGAP_CASES
    cases = [case for case in HGAP_CASES if case["name"].lower() == case_name.lower()]
    if not cases:
        raise ValueError("Unknown HGAP case: {}".format(case_name))
    return cases


def selected_hjoin_cases(case_name):
    if not case_name:
        return HJOIN_CASES
    cases = [case for case in HJOIN_CASES if case["name"].lower() == case_name.lower()]
    if not cases:
        raise ValueError("Unknown HJOIN case: {}".format(case_name))
    return cases


def selected_hset_cases(case_name):
    if not case_name:
        return HSET_CASES
    cases = [case for case in HSET_CASES if case["name"].lower() == case_name.lower()]
    if not cases:
        raise ValueError("Unknown HSET case: {}".format(case_name))
    return cases


def selected_hchk_cases(case_name):
    if not case_name:
        return HCHK_CASES
    cases = [case for case in HCHK_CASES if case["name"].lower() == case_name.lower()]
    if not cases:
        raise ValueError("Unknown HCHK case: {}".format(case_name))
    return cases


def selected_hnm_cases(case_name):
    if not case_name:
        return HNM_CASES
    cases = [case for case in HNM_CASES if case["name"].lower() == case_name.lower()]
    if not cases:
        raise ValueError("Unknown HNM case: {}".format(case_name))
    return cases


def selected_grid_cases(case_name):
    if not case_name:
        return GRID_CASES
    cases = [case for case in GRID_CASES if case["name"].lower() == case_name.lower()]
    if not cases:
        raise ValueError("Unknown GRID case: {}".format(case_name))
    return cases


def selected_cases(case_name):
    if not case_name:
        return HPLIT_CASES
    cases = [case for case in HPLIT_CASES if case["name"].lower() == case_name.lower()]
    if not cases:
        raise ValueError("Unknown HPLIT case: {}".format(case_name))
    return cases


def run_hplit_case(case, session, timeout):
    print("\n--- HPLIT {} ---".format(case["name"]))
    polylines = []
    session.run_script(hplit_script(case), timeout=timeout)
    polylines = session.get_polylines()

    ok, errors = assert_hplit_result(polylines, case)
    print("polylines: {}".format(len(polylines)))
    for index, polyline in enumerate(polylines):
        print("  {}: closed={} vertices={} area={:.3f}".format(index, polyline["closed"], len(polyline["vertices"]), polyline["area"]))
        print("     {}".format(json.dumps(polyline["vertices"], ensure_ascii=False)))
    if errors:
        for error in errors:
            print("  ERROR: {}".format(error))
    print("  [{}] {}".format("PASS" if ok else "FAIL", case["name"]))
    return ok


def run_hplit_batch(cases, session, timeout):
    print("\n--- HPLIT spatial batch ({} cases) ---".format(len(cases)))
    script, regions = hplit_batch_script(cases)
    session.run_script(script, timeout=timeout)
    all_polylines = session.get_polylines()
    all_ok = True

    for case, offset_x, region_min_x, region_max_x in regions:
        polylines = []
        for polyline in all_polylines:
            vertices = polyline["vertices"]
            if not vertices:
                continue
            mean_x = sum(vertex[0] for vertex in vertices) / len(vertices)
            if region_min_x <= mean_x <= region_max_x:
                polylines.append(polyline)

        shifted_case = dict(case)
        shifted_case["expected"] = [
            (point[0] + offset_x, point[1]) for point in case.get("expected", [])
        ]
        ok, errors = assert_hplit_result(polylines, shifted_case)
        print("\n--- HPLIT {} ---".format(case["name"]))
        print("polylines: {}".format(len(polylines)))
        for index, polyline in enumerate(polylines):
            print("  {}: closed={} vertices={} area={:.3f}".format(
                index, polyline["closed"], len(polyline["vertices"]), polyline["area"]))
        for error in errors:
            print("  ERROR: {}".format(error))
        print("  [{}] {}".format("PASS" if ok else "FAIL", case["name"]))
        if not ok:
            all_ok = False

    expected_total = sum(case.get("expected_count", 2) for case in cases)
    if len(all_polylines) != expected_total:
        print("  ERROR: spatial batch expected {} total polylines, got {}".format(
            expected_total, len(all_polylines)))
        all_ok = False
    return all_ok


def run_hplit(dll_path, timeout, case_name=None):
    with AcadSession(dll_path) as session:
        return run_hplit_in_session(session, timeout, case_name)


def run_hplit_in_session(session, timeout, case_name=None):
    if not case_name:
        try:
            return run_hplit_batch(HPLIT_CASES, session, timeout)
        except Exception as exc:
            print("\n--- HPLIT spatial batch ---")
            print("  ERROR: {}".format(exc))
            print("  [FAIL] HPLIT spatial batch")
            return False

    all_ok = True
    for case in selected_cases(case_name):
        try:
            if not run_hplit_case(case, session, timeout):
                all_ok = False
        except Exception as exc:
            all_ok = False
            print("\n--- HPLIT {} ---".format(case["name"]))
            print("  ERROR: {}".format(exc))
            print("  [FAIL] {}".format(case["name"]))
    return all_ok


def run_hgap_case(case, session, timeout):
    print("\n--- HGAP {} ---".format(case["name"]))
    polylines = []
    markers = []
    entities = []
    session.run_script(case["script"](), timeout=timeout)
    polylines = session.get_polylines()
    markers = session.get_layer_entities(HGAP_MARKER_LAYER)
    entities = session.get_entities()

    closed_count = sum(1 for polyline in polylines if polyline["closed"])
    open_count = sum(1 for polyline in polylines if not polyline["closed"])
    non_marker_lines = [entity for entity in entities if entity["name"] == "AcDbLine" and entity["layer"].lower() != HGAP_MARKER_LAYER.lower()]
    closed_endpoint_gaps = []
    for polyline in polylines:
        if polyline["closed"] and len(polyline["vertices"]) >= 2:
            closed_endpoint_gaps.append(distance(polyline["vertices"][0], polyline["vertices"][-1]))

    errors = []
    if len(polylines) != case["expected_polylines"]:
        errors.append("expected {} polylines, got {}".format(case["expected_polylines"], len(polylines)))
    if closed_count != case["expected_closed"]:
        errors.append("expected {} closed polylines, got {}".format(case["expected_closed"], closed_count))
    if open_count != case["expected_open"]:
        errors.append("expected {} remaining open polylines, got {}".format(case["expected_open"], open_count))
    if len(markers) < case["marker_min"]:
        errors.append("expected at least {} marker entities on HGAP layer, got {}".format(case["marker_min"], len(markers)))
    expected_non_marker_lines = case.get("expected_non_marker_lines")
    if expected_non_marker_lines is not None and len(non_marker_lines) != expected_non_marker_lines:
        errors.append("expected {} non-marker lines, got {}".format(expected_non_marker_lines, len(non_marker_lines)))
    expected_vertices = case.get("expected_vertices")
    if expected_vertices is not None:
        actual_vertices = sorted(len(polyline["vertices"]) for polyline in polylines)
        if actual_vertices != sorted(expected_vertices):
            errors.append("expected polyline vertex counts {}, got {}".format(sorted(expected_vertices), actual_vertices))
    all_vertices = []
    for polyline in polylines:
        all_vertices.extend(polyline["vertices"])
    for expected in case.get("expected_points", []):
        if not contains_near(all_vertices, expected, case.get("point_tolerance", 0.02)):
            errors.append("missing preserved endpoint near ({:.3f},{:.3f})".format(expected[0], expected[1]))

    print("polylines: {} closed={} open={}".format(len(polylines), closed_count, open_count))
    for index, polyline in enumerate(polylines):
        print("  {}: closed={} vertices={} area={:.3f}".format(index, polyline["closed"], len(polyline["vertices"]), polyline["area"]))
        print("     {}".format(json.dumps(polyline["vertices"], ensure_ascii=False)))
    print("closed endpoint gaps: {}".format(closed_endpoint_gaps))
    print("markers: {}".format(len(markers)))
    print("non-marker lines: {}".format(len(non_marker_lines)))
    for error in errors:
        print("  ERROR: {}".format(error))
    print("  [{}] HGAP {}".format("PASS" if not errors else "FAIL", case["name"]))
    return len(errors) == 0


def run_hgap(dll_path, timeout, case_name=None):
    with AcadSession(dll_path) as session:
        return run_hgap_in_session(session, timeout, case_name)


def run_hgap_in_session(session, timeout, case_name=None):
    all_ok = True
    for case in selected_hgap_cases(case_name):
        try:
            if not run_hgap_case(case, session, timeout):
                all_ok = False
        except Exception as exc:
            all_ok = False
            print("\n--- HGAP {} ---".format(case["name"]))
            print("  ERROR: {}".format(exc))
            print("  [FAIL] HGAP {}".format(case["name"]))
    return all_ok


def run_hjoin_case(case, session, timeout):
    print("\n--- HJOIN {} ---".format(case["name"]))
    session.run_script(case["script"](), timeout=timeout)
    polylines = session.get_polylines()
    closed_count = sum(1 for polyline in polylines if polyline["closed"])
    total_area = sum(polyline["area"] for polyline in polylines)

    errors = []
    if len(polylines) != case["expected_polylines"]:
        errors.append("expected {} polylines, got {}".format(case["expected_polylines"], len(polylines)))
    if closed_count != case["expected_closed"]:
        errors.append("expected {} closed polylines, got {}".format(case["expected_closed"], closed_count))
    if total_area < case.get("area_min", 0.0) or total_area > case.get("area_max", float("inf")):
        errors.append("unexpected total area {:.3f}".format(total_area))
    expected_vertex_counts = case.get("expected_vertex_counts")
    if expected_vertex_counts is not None:
        actual_vertex_counts = sorted(len(polyline["vertices"]) for polyline in polylines)
        if actual_vertex_counts != sorted(expected_vertex_counts):
            errors.append("expected vertex counts {}, got {}".format(sorted(expected_vertex_counts), actual_vertex_counts))
    expected_vertex_min = case.get("expected_vertex_min")
    if expected_vertex_min is not None:
        actual_vertex_max = max((len(polyline["vertices"]) for polyline in polylines), default=0)
        if actual_vertex_max < expected_vertex_min:
            errors.append("expected at least {} vertices on the merged outline, got {}".format(expected_vertex_min, actual_vertex_max))
    all_vertices = []
    for polyline in polylines:
        all_vertices.extend(polyline["vertices"])
    for expected in case.get("expected_vertices", []):
        if not contains_near(all_vertices, expected, case.get("point_tolerance", 0.18)):
            errors.append("missing vertex near ({:.2f},{:.2f})".format(expected[0], expected[1]))
    for region in case.get("expected_regions", []):
        region_polylines = []
        for polyline in polylines:
            center_x = sum(point[0] for point in polyline["vertices"]) / max(len(polyline["vertices"]), 1)
            if region["min_x"] <= center_x <= region["max_x"]:
                region_polylines.append(polyline)
        if len(region_polylines) != region["count"]:
            errors.append("region {} expected {} polylines, got {}".format(region["name"], region["count"], len(region_polylines)))
            continue
        region_area = sum(polyline["area"] for polyline in region_polylines)
        if region_area < region.get("area_min", 0.0) or region_area > region.get("area_max", float("inf")):
            errors.append("region {} unexpected area {:.3f}".format(region["name"], region_area))
        region_vertex_max = max((len(polyline["vertices"]) for polyline in region_polylines), default=0)
        if region_vertex_max < region.get("vertex_min", 0):
            errors.append("region {} expected at least {} vertices, got {}".format(region["name"], region.get("vertex_min", 0), region_vertex_max))
        region_vertices = []
        for polyline in region_polylines:
            region_vertices.extend(polyline["vertices"])
        for expected in region.get("vertices", []):
            if not contains_near(region_vertices, expected, region.get("point_tolerance", 0.2)):
                errors.append("region {} missing vertex near ({:.2f},{:.2f})".format(region["name"], expected[0], expected[1]))
        expected_layer = region.get("layer")
        if expected_layer is not None:
            actual_layers = sorted(set(polyline["layer"] for polyline in region_polylines))
            if actual_layers != [expected_layer]:
                errors.append("region {} expected layer {}, got {}".format(region["name"], expected_layer, actual_layers))

    print("polylines: {} closed={} area={:.3f}".format(len(polylines), closed_count, total_area))
    for index, polyline in enumerate(polylines):
        print("  {}: closed={} vertices={} area={:.3f} layer={}".format(index, polyline["closed"], len(polyline["vertices"]), polyline["area"], polyline["layer"]))
        print("     {}".format(json.dumps(polyline["vertices"], ensure_ascii=False)))
    for error in errors:
        print("  ERROR: {}".format(error))
    print("  [{}] HJOIN {}".format("PASS" if not errors else "FAIL", case["name"]))
    return len(errors) == 0


def run_hjoin(dll_path, timeout, case_name=None):
    with AcadSession(dll_path) as session:
        return run_hjoin_in_session(session, timeout, case_name)


def run_hjoin_in_session(session, timeout, case_name=None):
    all_ok = True
    for case in selected_hjoin_cases(case_name):
        try:
            if not run_hjoin_case(case, session, timeout):
                all_ok = False
        except Exception as exc:
            all_ok = False
            print("\n--- HJOIN {} ---".format(case["name"]))
            print("  ERROR: {}".format(exc))
            print("  [FAIL] HJOIN {}".format(case["name"]))
    return all_ok


def run_hset_case(case, session, timeout):
    print("\n--- HSET {} ---".format(case["name"]))
    session.run_script(case["script"](), timeout=timeout)
    polylines = session.get_polylines()
    markers = session.get_layer_entities(HGAP_MARKER_LAYER)
    all_vertices = []
    for polyline in polylines:
        all_vertices.extend(polyline["vertices"])

    errors = []
    if len(polylines) != case["expected_polylines"]:
        errors.append("expected {} polylines, got {}".format(case["expected_polylines"], len(polylines)))
    closed_count = sum(1 for polyline in polylines if polyline["closed"])
    if closed_count != case["expected_closed"]:
        errors.append("expected {} closed polylines, got {}".format(case["expected_closed"], closed_count))
    expected_open = case.get("expected_open")
    if expected_open is not None:
        open_count = sum(1 for polyline in polylines if not polyline["closed"])
        if open_count != expected_open:
            errors.append("expected {} open polylines, got {}".format(expected_open, open_count))
    if len(markers) < case.get("marker_min", 0):
        errors.append("expected at least {} HGAP markers, got {}".format(case.get("marker_min", 0), len(markers)))
    for expected in case.get("expected_vertices", []):
        if not contains_near(all_vertices, expected, case.get("point_tolerance", 0.1)):
            errors.append("missing configured-spacing vertex near ({:.2f},{:.2f})".format(expected[0], expected[1]))

    print("polylines: {} closed={} markers={}".format(len(polylines), closed_count, len(markers)))
    for index, polyline in enumerate(polylines):
        print("  {}: closed={} vertices={} area={:.3f}".format(index, polyline["closed"], len(polyline["vertices"]), polyline["area"]))
        print("     {}".format(json.dumps(polyline["vertices"], ensure_ascii=False)))
    for error in errors:
        print("  ERROR: {}".format(error))
    print("  [{}] HSET {}".format("PASS" if not errors else "FAIL", case["name"]))
    return len(errors) == 0


def run_hset(dll_path, timeout, case_name=None):
    with AcadSession(dll_path) as session:
        return run_hset_in_session(session, timeout, case_name)


def run_hset_in_session(session, timeout, case_name=None):
    all_ok = True
    for case in selected_hset_cases(case_name):
        try:
            if not run_hset_case(case, session, timeout):
                all_ok = False
        except Exception as exc:
            all_ok = False
            print("\n--- HSET {} ---".format(case["name"]))
            print("  ERROR: {}".format(exc))
            print("  [FAIL] HSET {}".format(case["name"]))
    return all_ok


def run_hchk_case(case, session, timeout):
    print("\n--- HCHK {} ---".format(case["name"]))
    session.run_script(case["script"](), timeout=timeout)
    polylines = session.get_polylines()
    markers = session.get_layer_entities("HCHK_图形健康标识")
    errors = []
    marker_count = len(markers)
    if marker_count < case.get("marker_min", 0):
        errors.append("expected at least {} HCHK markers, got {}".format(case.get("marker_min", 0), marker_count))
    marker_max = case.get("marker_max")
    if marker_max is not None and marker_count > marker_max:
        errors.append("expected at most {} HCHK markers, got {}".format(marker_max, marker_count))
    if len(polylines) != case["expected_polylines"]:
        errors.append("expected original polyline count {}, got {}".format(case["expected_polylines"], len(polylines)))

    print("polylines: {} markers={}".format(len(polylines), marker_count))
    print("marker entities: {}".format(json.dumps(markers, ensure_ascii=False)))
    for error in errors:
        print("  ERROR: {}".format(error))
    print("  [{}] HCHK {}".format("PASS" if not errors else "FAIL", case["name"]))
    return len(errors) == 0


def run_hchk(dll_path, timeout, case_name=None):
    with AcadSession(dll_path) as session:
        return run_hchk_in_session(session, timeout, case_name)


def run_hchk_in_session(session, timeout, case_name=None):
    all_ok = True
    for case in selected_hchk_cases(case_name):
        try:
            if not run_hchk_case(case, session, timeout):
                all_ok = False
        except Exception as exc:
            all_ok = False
            print("\n--- HCHK {} ---".format(case["name"]))
            print("  ERROR: {}".format(exc))
            print("  [FAIL] HCHK {}".format(case["name"]))
    return all_ok


def run_hnm_case(case, session, timeout):
    print("\n--- HNM {} ---".format(case["name"]))
    session.run_script(case["script"](), timeout=timeout)
    polylines = session.get_polylines()
    texts = session.get_texts_by_layer("H_NUM")
    closed_count = sum(1 for polyline in polylines if polyline["closed"])

    errors = []
    if len(texts) != case["expected_texts"]:
        errors.append("expected {} H_NUM texts, got {}".format(case["expected_texts"], len(texts)))
    if len(polylines) != case["expected_polylines"]:
        errors.append("expected {} polylines, got {}".format(case["expected_polylines"], len(polylines)))
    if closed_count != case["expected_closed"]:
        errors.append("expected {} closed polylines, got {}".format(case["expected_closed"], closed_count))

    print("polylines: {} closed={} H_NUM texts={}".format(len(polylines), closed_count, len(texts)))
    print("texts: {}".format(json.dumps([text["text"] for text in texts], ensure_ascii=False)))
    for error in errors:
        print("  ERROR: {}".format(error))
    print("  [{}] HNM {}".format("PASS" if not errors else "FAIL", case["name"]))
    return len(errors) == 0


def run_hnm(dll_path, timeout, case_name=None):
    with AcadSession(dll_path) as session:
        return run_hnm_in_session(session, timeout, case_name)


def run_hnm_in_session(session, timeout, case_name=None):
    all_ok = True
    for case in selected_hnm_cases(case_name):
        try:
            if not run_hnm_case(case, session, timeout):
                all_ok = False
        except Exception as exc:
            all_ok = False
            print("\n--- HNM {} ---".format(case["name"]))
            print("  ERROR: {}".format(exc))
            print("  [FAIL] HNM {}".format(case["name"]))
    return all_ok


def htc_dot_case_script():
    return [
        "_.ERASE", "_ALL", "",
        "(entmakex (list '(0 . \"TEXT\") (cons 8 \"0\") (cons 10 (list 0.0 0.0 0.0)) (cons 40 5.0) (cons 50 0.0) (cons 1 \"AB\")))",
        "HTC", "_W", "-3,-3", "20,13", "",
    ]


def htc_chinese_case_script():
    return [
        "_.ERASE", "_ALL", "",
        "(entmakex (list '(0 . \"TEXT\") (cons 8 \"0\") (cons 10 (list 0.0 0.0 0.0)) (cons 40 5.0) (cons 50 0.0) (cons 1 \"中\")))",
        "HTC", "_W", "-3,-3", "15,15", "",
    ]


def htc_zero_case_script():
    # "0" 有孔洞：网点应填环带、中心空心（奇偶规则）
    return [
        "_.ERASE", "_ALL", "",
        "(entmakex (list '(0 . \"TEXT\") (cons 8 \"0\") (cons 10 (list 0.0 0.0 0.0)) (cons 40 5.0) (cons 50 0.0) (cons 1 \"0\")))",
        "HTC", "_W", "-3,-3", "12,12", "",
    ]


def htc_rotated_case_script():
    # 旋转 30° 文字：网点绕插入点旋转，位置 1:1
    return [
        "_.ERASE", "_ALL", "",
        "(entmakex (list '(0 . \"TEXT\") (cons 8 \"0\") (cons 10 (list 0.0 0.0 0.0)) (cons 40 5.0) (cons 50 0.5236) (cons 1 \"AB\")))",
        "HTC", "_W", "-6,-6", "22,17", "",
    ]


def htc_oblique_case_script():
    # 斜体文字：斜切必须绕文字基线，不能把整段网点水平推离原文字
    return [
        "_.ERASE", "_ALL", "",
        "(entmakex (list '(0 . \"TEXT\") (cons 8 \"0\") (cons 10 (list 0.0 0.0 0.0)) (cons 40 5.0) (cons 50 0.0) (cons 51 0.2617993878) (cons 1 \"AB\")))",
        "HTC", "_W", "-5,-5", "20,13", "",
    ]


def htc_mtext_case_script():
    # 单行 MTEXT：墨迹顶贴盒顶（TopLeft），尺寸 1:1 不拉伸到参考框
    return [
        "_.ERASE", "_ALL", "",
        "(entmakex '((0 . \"MTEXT\") (100 . \"AcDbEntity\") (100 . \"AcDbMText\") (8 . \"0\") (10 0.0 0.0 0.0) (40 . 5.0) (41 . 20.0) (71 . 1) (72 . 1) (73 . 1) (44 . 1.0) (1 . \"AB\")))",
        "HTC", "_W", "-5,-15", "25,5", "",
    ]


def htc_mtext_multiline_case_script():
    # 多行 MTEXT：两行网点按 1.6667×字高行距排布
    return [
        "_.ERASE", "_ALL", "",
        "(entmakex '((0 . \"MTEXT\") (100 . \"AcDbEntity\") (100 . \"AcDbMText\") (8 . \"0\") (10 0.0 0.0 0.0) (40 . 5.0) (41 . 20.0) (71 . 1) (72 . 1) (73 . 1) (44 . 1.0) (1 . \"AB\\\\PAB\")))",
        "HTC", "_W", "-5,-32", "25,5", "",
    ]


def htc_mtext_codes_case_script():
    # MTEXT 带格式代码（{\fSimSun|b0|i0|c134|p2;...}）：必须剥离代码，只按可见文字 "AB" 生成网点，
    # 不得把 {\fSimSun|b0|i0|c134|p2; 等代码符号渲染进网点
    return [
        "_.ERASE", "_ALL", "",
        "(entmakex '((0 . \"MTEXT\") (100 . \"AcDbEntity\") (100 . \"AcDbMText\") (8 . \"0\") (10 0.0 0.0 0.0) (40 . 5.0) (41 . 30.0) (71 . 1) (72 . 1) (73 . 1) (44 . 1.0) (1 . \"{\\\\fSimSun|b0|i0|c134|p2;AB}\")))",
        "HTC", "_W", "-5,-15", "35,5", "",
    ]


HTC_CASES = [
    ("latin", htc_dot_case_script),
    ("chinese", htc_chinese_case_script),
    ("zero_hole", htc_zero_case_script),
    ("rotated", htc_rotated_case_script),
    ("oblique", htc_oblique_case_script),
    ("mtext", htc_mtext_case_script),
    ("mtext_multiline", htc_mtext_multiline_case_script),
    ("mtext_codes", htc_mtext_codes_case_script),
]


def _model_text_count(session):
    count = 0
    model_space = session.doc.ModelSpace
    for index in range(model_space.Count):
        entity = model_space.Item(index)
        if getattr(entity, "EntityName", None) in ("AcDbText", "AcDbMText"):
            count += 1
    return count


def _text_extents(session):
    """Return (min_x, min_y, max_x, max_y) of the first text/mtext in model space, or None."""
    model_space = session.doc.ModelSpace
    for index in range(model_space.Count):
        entity = model_space.Item(index)
        if getattr(entity, "EntityName", None) not in ("AcDbText", "AcDbMText"):
            continue
        try:
            min_point, max_point = entity.GetBoundingBox()
            return (
                float(min_point[0]), float(min_point[1]),
                float(max_point[0]), float(max_point[1]),
            )
        except Exception:
            return None
    return None


def htc_dot_errors(circles):
    """Validate actual dot geometry against the fixed 0.03/0.06 mm contract."""
    errors = []
    if len(circles) < 20:
        errors.append("expected at least 20 dots, got {}".format(len(circles)))
    if any(abs(float(c["radius"]) - 0.015) > 1e-8 for c in circles):
        errors.append("expected all radii 0.015 (diameter 0.03 fixed)")
    # Spread samples over the full glyph without a quadratic all-pairs scan.
    points = [c["center"] for c in circles]
    step = max(1, len(points) // 32)
    nearest = []
    for index in range(0, len(points), step):
        distances = [distance(points[index], other)
                     for j, other in enumerate(points) if j != index]
        if distances:
            nearest.append(min(distances))
    if nearest:
        median = sorted(nearest)[len(nearest) // 2]
        if min(nearest) < 0.06 - 1e-6 or abs(median - 0.06) > 1e-6:
            errors.append("expected nearest spacing 0.06, got min={} median={}".format(
                min(nearest), median))
    return errors


def run_htc_case(session, timeout, case_name, script):
    print("\n--- HTC {} text-to-dot ---".format(case_name))
    session.run_script(script(), timeout=timeout)
    circles = session.get_circles()
    errors = htc_dot_errors(circles)
    radii = {round(float(c["radius"]), 4) for c in circles}

    text_count = _model_text_count(session)
    if text_count != 1:
        errors.append("expected original text kept (count=1), got {}".format(text_count))

    extents = _text_extents(session)
    if extents is not None and circles:
        min_x, min_y, max_x, max_y = extents
        # 容差 2.5mm：允许字形墨迹下伸到显示范围以下（如"中"的下伸 ~1.8mm）与左右侧边距
        margin = 2.5
        bad = [c for c in circles
               if c["center"][0] < min_x - margin or c["center"][0] > max_x + margin or
               c["center"][1] < min_y - margin or c["center"][1] > max_y + margin]
        if bad:
            errors.append("{} dots outside text bounds x[{:.2f},{:.2f}] y[{:.2f},{:.2f}]".format(
                len(bad), min_x, max_x, min_y, max_y))
        in_x = [c["center"][0] for c in circles]
        in_y = [c["center"][1] for c in circles]
        print("circles: {} radius={} x[{:.3f},{:.3f}] y[{:.3f},{:.3f}] text_ext x[{:.2f},{:.2f}] y[{:.2f},{:.2f}]".format(
            len(circles), sorted(radii), min(in_x), max(in_x), min(in_y), max(in_y),
            min_x, max_x, min_y, max_y))

        if case_name == "zero_hole" and circles:
            # "0" 字形中心为孔洞：围绕网点包围盒中心的 0.8mm 范围内不应有点
            cx = (min(in_x) + max(in_x)) / 2.0
            cy = (min(in_y) + max(in_y)) / 2.0
            hole_dots = [c for c in circles
                         if abs(c["center"][0] - cx) < 0.4 and abs(c["center"][1] - cy) < 0.4]
            if len(hole_dots) > 0:
                errors.append("expected hollow hole in '0', got {} dots near center".format(len(hole_dots)))
            print("  '0' hole check: dots within 0.4 of center = {} (expect 0)".format(len(hole_dots)))

        if case_name == "mtext_multiline" and circles:
            # 两行 MTEXT（H=5）：网点纵向跨度应接近 2 行行距（>10mm），且贴盒顶
            y_span = max(in_y) - min(in_y)
            if y_span < 10.0:
                errors.append("expected 2-line mtext dot span >10mm, got {:.2f}mm".format(y_span))
            top_gap = max_y - max(in_y)
            if top_gap > 1.5:
                errors.append("mtext dots should start at box top, top gap {:.2f}mm".format(top_gap))
            print("  mtext multiline: y_span={:.2f}mm top_gap={:.2f}mm (expect span>10, gap<1.5)".format(y_span, top_gap))
    else:
        print("circles: {} radius={}".format(len(circles), sorted(radii)))

    for error in errors:
        print("  ERROR: {}".format(error))
    print("  [{}] HTC {} text-to-dot".format("PASS" if not errors else "FAIL", case_name))
    return len(errors) == 0


def run_htc(dll_path, timeout):
    with AcadSession(dll_path) as session:
        return run_htc_in_session(session, timeout)


def run_htc_in_session(session, timeout):
    all_ok = True
    for case_name, script in HTC_CASES:
        try:
            if not run_htc_case(session, timeout, case_name, script):
                all_ok = False
        except Exception as exc:
            all_ok = False
            print("\n--- HTC {} ---".format(case_name))
            print("  ERROR: {}".format(exc))
            print("  [FAIL] HTC {}".format(case_name))
    return all_ok


def run_grid_case(case, session, timeout):
    print("\n--- GRID {} ---".format(case["name"]))
    session.run_script(case["script"](), timeout=timeout)
    circles = session.get_circles()
    errors = []
    for layer, expected_count in case["expected_layers"].items():
        actual = [circle for circle in circles if circle["layer"].lower() == layer.lower()]
        if len(actual) != expected_count:
            errors.append("layer {} expected {} preserved circles, got {}".format(layer, expected_count, len(actual)))
    for layer, expected_colors in case.get("expected_colors", {}).items():
        actual_colors = sorted(
            circle["color"] for circle in circles if circle["layer"].lower() == layer.lower())
        if actual_colors != sorted(expected_colors):
            errors.append("layer {} expected anomaly colors {}, got {}".format(layer, sorted(expected_colors), actual_colors))
    generated_region = case.get("generated_region")
    if generated_region is not None:
        generated = [
            circle for circle in circles
            if generated_region["min_x"] <= circle["center"][0] <= generated_region["max_x"]
        ]
        if len(generated) < generated_region.get("minimum", 1):
            errors.append("normal HGR/HRS/HRD path generated {} circles".format(len(generated)))
    unexpected_default = [
        circle for circle in circles
        if circle["layer"].lower() == "0" and circle["center"][0] < 29.0
    ]
    if unexpected_default:
        errors.append("zero-grid replacement unexpectedly created {} circles on layer 0".format(len(unexpected_default)))

    print("circles: {}".format(len(circles)))
    print("circle data: {}".format(json.dumps(circles, ensure_ascii=False)))
    for error in errors:
        print("  ERROR: {}".format(error))
    print("  [{}] GRID {}".format("PASS" if not errors else "FAIL", case["name"]))
    return len(errors) == 0


def run_grid(dll_path, timeout, case_name=None):
    with AcadSession(dll_path) as session:
        return run_grid_in_session(session, timeout, case_name)


def run_grid_in_session(session, timeout, case_name=None):
    all_ok = True
    for case in selected_grid_cases(case_name):
        try:
            if not run_grid_case(case, session, timeout):
                all_ok = False
        except Exception as exc:
            all_ok = False
            print("\n--- GRID {} ---".format(case["name"]))
            print("  ERROR: {}".format(exc))
            print("  [FAIL] GRID {}".format(case["name"]))
    return all_ok


def run_hix_atomic_case(session, timeout):
    print("\n--- HEX -> HIX linked workflow ---")
    temp_directory = Path(tempfile.mkdtemp(prefix="hexfill_hex_hix_"))
    drawing_path = temp_directory / "HexHixLink.dwg"
    desktop_directory = Path.home() / "Desktop"
    search_directories = [temp_directory, desktop_directory]
    existing_exports = set()
    for directory in search_directories:
        existing_exports.update(directory.glob("*Spacing_*.csv"))
    exported_csv_paths = []
    try:
        session.doc.SaveAs(str(drawing_path))
        setup = [
            "_.ERASE", "_ALL", "",
            "_.PLINE", "0,0", "30,0", "30,30", "0,30", "_C",
            "_.PLINE", "5,5", "25,5", "25,25", "5,25", "_C",
            "HNM", "1", "_W", "-1,-1", "31,31", "",
            "HGR", "_W", "4,4", "26,26", "", "2",
            "HEX", "_W", "4,4", "26,26", "",
        ]
        session.run_script(setup, timeout=timeout)

        exported_files = []
        for directory in search_directories:
            exported_files.extend(path for path in directory.glob("*Spacing_*.csv") if path not in existing_exports)
        exported_files.sort(key=lambda path: path.stat().st_mtime)
        if not exported_files:
            print("  ERROR: HEX did not export a spacing CSV")
            print("  [FAIL] HEX -> HIX linked workflow")
            return False
        csv_path = exported_files[-1]
        exported_csv_paths.append(csv_path)
        with csv_path.open("r", encoding="utf-8-sig", newline="") as csv_file:
            rows = list(csv.reader(csv_file))
        if len(rows) < 2 or len(rows[1]) < 5:
            print("  ERROR: HEX CSV format is incomplete")
            print("  [FAIL] HEX -> HIX linked workflow")
            return False

        exported_spacing = float(rows[1][1])
        target_spacing = max(0.1, exported_spacing * 0.7)

        def write_target(value):
            rows[1][1] = "{:.8f}".format(value)
            with csv_path.open("w", encoding="utf-8-sig", newline="") as csv_file:
                csv.writer(csv_file, lineterminator="\n").writerows(rows)

        write_target(target_spacing)
        before_handles = sorted(circle["handle"] for circle in session.get_circles())
        session.run_script([
            "HIX", '"{}"'.format(str(csv_path)),
            "_W", "4,4", "26,26", "",
        ], timeout=timeout)
        updated_circles = session.get_circles()
        updated_handles = sorted(circle["handle"] for circle in updated_circles)
        actual_spacing = average_nearest_spacing(updated_circles)

        errors = []
        if not before_handles:
            errors.append("HGR setup generated no circles")
        if before_handles == updated_handles:
            errors.append("HIX did not replace circles after the HEX spacing was changed")
        spacing_tolerance = max(target_spacing * 0.03, 0.01)
        if abs(actual_spacing - target_spacing) > spacing_tolerance:
            errors.append("HIX spacing {:.4f} does not match target {:.4f}".format(actual_spacing, target_spacing))

        known_exports = set(existing_exports)
        known_exports.update(exported_files)
        time.sleep(1.1)
        session.run_script([
            "_.ERASE", "_ALL", "",
            "_.PLINE", "100,0", "101,0", "101,1", "100,1", "_C",
            "_.PLINE", "100.495,0.495", "100.505,0.495", "100.505,0.505", "100.495,0.505", "_C",
            "HNM", "2", "_W", "99.9,-0.1", "101.1,1.1", "",
            "_.CIRCLE", "100.5,0.5", "0.015",
            "HEX", "_W", "100.49,0.49", "100.51,0.51", "",
        ], timeout=timeout)
        tiny_export_files = []
        for directory in search_directories:
            tiny_export_files.extend(path for path in directory.glob("*Spacing_*.csv") if path not in known_exports)
        tiny_export_files.sort(key=lambda path: path.stat().st_mtime)
        if not tiny_export_files:
            errors.append("HEX did not export the tiny-region spacing CSV")
            tiny_csv_path = csv_path
        else:
            tiny_csv_path = tiny_export_files[-1]
            exported_csv_paths.append(tiny_csv_path)
        with tiny_csv_path.open("r", encoding="utf-8-sig", newline="") as csv_file:
            rows = list(csv.reader(csv_file))
        csv_path = tiny_csv_path

        write_target(0.1)
        before_failure = sorted(circle["handle"] for circle in session.get_circles())
        session.run_script([
            "HIX", '"{}"'.format(str(csv_path)),
            "_W", "100.49,0.49", "100.51,0.51", "",
        ], timeout=timeout)
        failed_circles = session.get_circles()
        after_failure = sorted(circle["handle"] for circle in failed_circles)
        if before_failure != after_failure:
            errors.append("generation failure did not preserve the old circle handles")
        if failed_circles and any(circle["color"] not in (1, 6) for circle in failed_circles):
            errors.append("generation failure did not mark all preserved circles with an anomaly color")

        write_target(0.0)
        session.run_script([
            "HIX", '"{}"'.format(str(csv_path)),
            "_W", "100.49,0.49", "100.51,0.51", "",
        ], timeout=timeout)
        zero_target_circles = session.get_circles()
        if zero_target_circles:
            errors.append("explicit zero target preserved {} circles instead of clearing them".format(len(zero_target_circles)))

        print("HEX spacing={:.4f} target={:.4f} HIX spacing={:.4f}".format(
            exported_spacing, target_spacing, actual_spacing))
        print("generation_failure_handles_preserved={} anomaly_colors={} zero_target_count={}".format(
            before_failure == after_failure,
            sorted(set(circle["color"] for circle in failed_circles)),
            len(zero_target_circles)))
        for error in errors:
            print("  ERROR: {}".format(error))
        print("  [{}] HEX -> HIX linked workflow".format("PASS" if not errors else "FAIL"))
        return len(errors) == 0
    finally:
        session.close_document()
        for exported_csv_path in exported_csv_paths:
            try:
                exported_csv_path.unlink()
            except OSError:
                pass
        for generated_path in temp_directory.glob("*"):
            try:
                generated_path.unlink()
            except OSError:
                pass
        try:
            temp_directory.rmdir()
        except OSError:
            pass
        session.open_document()


def run_hix(dll_path, timeout):
    with AcadSession(dll_path) as session:
        return run_hix_atomic_case(session, timeout)


def run_hbr_had_case(session, timeout):
    print("\n--- HBR -> HAD linked workflow ---")
    temp_directory = Path(tempfile.mkdtemp(prefix="hexfill_hbr_had_"))
    drawing_path = temp_directory / "HbrHadLink.dwg"
    desktop_directory = Path.home() / "Desktop"
    search_directories = [temp_directory, desktop_directory]
    existing_exports = set()
    for directory in search_directories:
        existing_exports.update(directory.glob("*Brightness_*.csv"))
    exported_csv_paths = []
    try:
        session.doc.SaveAs(str(drawing_path))
        session.run_script([
            "_.ERASE", "_ALL", "",
            "_.PLINE", "0,0", "40,0", "40,40", "0,40", "_C",
            "_.PLINE", "5,5", "35,5", "35,35", "5,35", "_C",
            "HNM", "1", "_W", "-1,-1", "41,41", "",
            "HGR", "_W", "4,4", "36,36", "", "2",
            "HBR", "_W", "-1,-1", "41,41", "",
        ], timeout=timeout)

        exported_files = []
        for directory in search_directories:
            exported_files.extend(path for path in directory.glob("*Brightness_*.csv") if path not in existing_exports)
        exported_files.sort(key=lambda path: path.stat().st_mtime)
        if not exported_files:
            print("  ERROR: HBR did not export a brightness CSV")
            print("  [FAIL] HBR -> HAD linked workflow")
            return False
        csv_path = exported_files[-1]
        exported_csv_paths.append(csv_path)
        with csv_path.open("r", encoding="utf-8-sig", newline="") as csv_file:
            rows = list(csv.reader(csv_file))
        if len(rows) < 2 or len(rows[1]) < 4:
            print("  ERROR: HBR CSV format is incomplete")
            print("  [FAIL] HBR -> HAD linked workflow")
            return False

        before_circles = session.get_circles()
        old_spacing = average_nearest_spacing(before_circles)
        current_brightness = 1.0
        target_brightness = 0.25
        expected_spacing = old_spacing * math.sqrt(current_brightness / target_brightness)
        # 模拟部分 Excel 版本/区域设置“另存为”后的格式：UTF-16、分号分列、逗号小数。
        # HAD 必须能读回同一份 HBR 亮度数据，而不能把 1,0000 误当成 10000。
        excel_csv_path = csv_path.with_name(csv_path.stem + "_ExcelCompat.csv")
        rows[1][1] = "{:.4f}".format(current_brightness).replace(".", ",")
        rows[1][2] = "{:.4f}".format(target_brightness).replace(".", ",")
        with excel_csv_path.open("w", encoding="utf-16", newline="") as csv_file:
            csv.writer(csv_file, delimiter=";", lineterminator="\r\n").writerows(rows)
        exported_csv_paths.append(excel_csv_path)

        before_handles = sorted(circle["handle"] for circle in before_circles)
        session.run_script([
            "HAD", '"{}"'.format(str(excel_csv_path)),
            "_W", "-1,-1", "41,41", "",
        ], timeout=timeout)
        after_circles = session.get_circles()
        after_handles = sorted(circle["handle"] for circle in after_circles)
        actual_spacing = average_nearest_spacing(after_circles)

        errors = []
        if not before_handles:
            errors.append("HGR setup generated no circles")
        if before_handles == after_handles:
            errors.append("HAD did not replace circles after brightness changed")
        if len(after_circles) >= len(before_circles):
            errors.append("lower target brightness did not reduce circle count")
        spacing_tolerance = max(expected_spacing * 0.04, 0.02)
        if abs(actual_spacing - expected_spacing) > spacing_tolerance:
            errors.append("HAD spacing {:.4f} does not match expected {:.4f}".format(actual_spacing, expected_spacing))

        print("HBR spacing={:.4f} expected HAD spacing={:.4f} actual={:.4f}".format(
            old_spacing, expected_spacing, actual_spacing))
        print("circle_count {} -> {} handles_replaced={}".format(
            len(before_circles), len(after_circles), before_handles != after_handles))
        for error in errors:
            print("  ERROR: {}".format(error))
        print("  [{}] HBR -> HAD linked workflow".format("PASS" if not errors else "FAIL"))
        return len(errors) == 0
    finally:
        session.close_document()
        for exported_csv_path in exported_csv_paths:
            try:
                exported_csv_path.unlink()
            except OSError:
                pass
        for generated_path in temp_directory.glob("*"):
            try:
                generated_path.unlink()
            except OSError:
                pass
        try:
            temp_directory.rmdir()
        except OSError:
            pass
        session.open_document()


def run_hbr_had(dll_path, timeout):
    with AcadSession(dll_path) as session:
        return run_hbr_had_case(session, timeout)


def run_had_performance_case(session, timeout, region_count=30):
    print("\n--- HAD large-data performance ---")
    temp_directory = Path(tempfile.mkdtemp(prefix="hexfill_had_perf_"))
    drawing_path = temp_directory / "HadPerformance.dwg"
    desktop_directory = Path.home() / "Desktop"
    search_directories = [temp_directory, desktop_directory]
    existing_exports = set()
    for directory in search_directories:
        existing_exports.update(directory.glob("*Brightness_*.csv"))
    exported_csv_paths = []
    try:
        session.doc.SaveAs(str(drawing_path))
        max_x = (region_count - 1) * 30.0 + 22.0
        setup = ["_.ERASE", "_ALL", ""]
        for index in range(region_count):
            x = index * 30.0
            setup.extend([
                "_.PLINE", "{:.3f},0".format(x), "{:.3f},0".format(x + 22.0),
                "{:.3f},22".format(x + 22.0), "{:.3f},22".format(x), "_C",
                "_.PLINE", "{:.3f},1".format(x + 1.0), "{:.3f},1".format(x + 21.0),
                "{:.3f},21".format(x + 21.0), "{:.3f},21".format(x + 1.0), "_C",
            ])
        setup.extend(["HNM", "1", "_W", "-1,-1", "{:.3f},23".format(max_x + 1.0), "", "_.REGEN"])
        session.run_script(setup, timeout=max(timeout, 240))
        circle_lisp = (
            "(progn (setq hi 0) (repeat {} (setq hr 0) "
            "(repeat 18 (setq hc 0) (repeat 18 "
            "(entmakex (list '(0 . \"CIRCLE\") "
            "(cons 10 (list (+ 2.0 (* hi 30.0) hc) (+ 2.0 hr) 0.0)) '(40 . 0.015))) "
            "(setq hc (1+ hc))) (setq hr (1+ hr))) (setq hi (1+ hi))))".format(region_count)
        )
        session.run_script([circle_lisp, "_.REGEN"], timeout=max(timeout, 240))
        session.run_script([
            "HBR", "_W", "-1,-1", "{:.3f},23".format(max_x + 1.0), "", "_.REGEN",
        ], timeout=max(timeout, 240))

        exported_files = []
        for directory in search_directories:
            exported_files.extend(path for path in directory.glob("*Brightness_*.csv") if path not in existing_exports)
        exported_files.sort(key=lambda path: path.stat().st_mtime)
        if not exported_files:
            print("  ERROR: HBR did not export the performance CSV")
            print("  [FAIL] HAD large-data performance")
            return False
        csv_path = exported_files[-1]
        exported_csv_paths.append(csv_path)
        with csv_path.open("r", encoding="utf-8-sig", newline="") as csv_file:
            rows = list(csv.reader(csv_file))
        for row in rows[1:]:
            if len(row) >= 3:
                row[1] = "1.0000"
                row[2] = "0.6400"
        with csv_path.open("w", encoding="utf-8-sig", newline="") as csv_file:
            csv.writer(csv_file, lineterminator="\n").writerows(rows)

        before_count = session.get_circle_count_fast()
        started = time.perf_counter()
        session.run_script([
            "HAD", '"{}"'.format(str(csv_path)),
            "_W", "-1,-1", "{:.3f},23".format(max_x + 1.0), "",
        ], timeout=max(timeout, 240))
        elapsed = time.perf_counter() - started
        after_count = session.get_circle_count_fast()
        first_region = session.get_circles_in_window((1.0, 1.0), (21.0, 21.0))
        actual_spacing = average_nearest_spacing(first_region)

        errors = []
        expected_before = region_count * 18 * 18
        if before_count != expected_before:
            errors.append("expected {} setup circles, got {}".format(expected_before, before_count))
        if after_count >= before_count:
            errors.append("HAD did not reduce circle count")
        if abs(actual_spacing - 1.25) > 0.04:
            errors.append("expected spacing 1.25, got {:.4f}".format(actual_spacing))
        if elapsed > 30.0:
            errors.append("HAD exceeded 30 second performance limit: {:.2f}s".format(elapsed))

        print("regions={} circles={} -> {} spacing={:.4f} elapsed={:.2f}s".format(
            region_count, before_count, after_count, actual_spacing, elapsed))
        for error in errors:
            print("  ERROR: {}".format(error))
        print("  [{}] HAD large-data performance".format("PASS" if not errors else "FAIL"))
        return len(errors) == 0
    finally:
        session.close_document()
        for exported_csv_path in exported_csv_paths:
            try:
                exported_csv_path.unlink()
            except OSError:
                pass
        for generated_path in temp_directory.glob("*"):
            try:
                generated_path.unlink()
            except OSError:
                pass
        try:
            temp_directory.rmdir()
        except OSError:
            pass
        session.open_document()


def run_had_performance(dll_path, timeout):
    with AcadSession(dll_path) as session:
        correctness_ok = run_hbr_had_case(session, timeout)
        performance_ok = run_had_performance_case(session, timeout)
        return correctness_ok and performance_ok


def run_hrs_performance_case(session, timeout, region_count=30):
    print("\n--- HRS large-selection performance ---")
    max_x = (region_count - 1) * 30.0 + 22.0
    setup = ["_.ERASE", "_ALL", ""]
    for index in range(region_count):
        x = index * 30.0
        setup.extend([
            "_.PLINE", "{:.3f},0".format(x), "{:.3f},0".format(x + 22.0),
            "{:.3f},22".format(x + 22.0), "{:.3f},22".format(x), "_C",
        ])
    setup.extend([
        "HGR", "_W", "-1,-1", "{:.3f},23".format(max_x + 1.0), "", "1",
        "_.ZOOM", "_E",
    ])
    session.run_script(setup, timeout=max(timeout, 300))

    before_count = session.get_circle_count_fast()
    before_region = session.get_circles_in_window((0.0, 0.0), (22.0, 22.0))
    before_spacing = average_nearest_spacing(before_region)
    started = time.perf_counter()
    session.run_script([
        "(setq hrs_t0 (getvar \"MILLISECS\"))",
        "HRS", "_W", "-1,-1", "{:.3f},23".format(max_x + 1.0), "", "-20",
        "(setvar \"USERR1\" (- (getvar \"MILLISECS\") hrs_t0))",
    ], timeout=max(timeout, 300))
    elapsed = time.perf_counter() - started
    command_elapsed = float(session.doc.GetVariable("USERR1")) / 1000.0
    after_count = session.get_circle_count_fast()
    after_region = session.get_circles_in_window((0.0, 0.0), (22.0, 22.0))
    after_spacing = average_nearest_spacing(after_region)
    expected_spacing = before_spacing * math.sqrt(100.0 / 80.0)

    errors = []
    if before_count <= 0:
        errors.append("HGR did not create setup circles")
    if after_count >= before_count:
        errors.append("HRS -20% did not reduce circle count")
    if abs(after_spacing - expected_spacing) > 0.04:
        errors.append("expected spacing {:.4f}, got {:.4f}".format(expected_spacing, after_spacing))
    if elapsed > 45.0:
        errors.append("HRS exceeded 45 second performance limit: {:.2f}s".format(elapsed))

    print("regions={} circles={} -> {} spacing={:.4f} -> {:.4f} command={:.3f}s total={:.2f}s".format(
        region_count, before_count, after_count, before_spacing, after_spacing, command_elapsed, elapsed))
    for error in errors:
        print("  ERROR: {}".format(error))
    print("  [{}] HRS large-selection performance".format("PASS" if not errors else "FAIL"))
    return len(errors) == 0


def run_hrs_performance(dll_path, timeout):
    with AcadSession(dll_path) as session:
        return run_hrs_performance_case(session, timeout)


def run_hof_dialog_case(session, timeout):
    print("\n--- HOF modal dialog workflow ---")
    errors = []
    dialog_handle = None
    hof_thread = None
    command_errors = []
    try:
        session.run_script([
            "_.ERASE", "_ALL", "",
            "_.PLINE", "0,0", "100,0", "100,100", "0,100", "_C",
        ], timeout=timeout)

        import pythoncom
        import threading
        import win32com.client
        marshaled_document = pythoncom.CoMarshalInterThreadInterfaceInStream(
            pythoncom.IID_IDispatch, session.doc._oleobj_)

        def send_hof_command():
            pythoncom.CoInitialize()
            try:
                document_dispatch = pythoncom.CoGetInterfaceAndReleaseStream(
                    marshaled_document, pythoncom.IID_IDispatch)
                threaded_document = win32com.client.Dispatch(document_dispatch)
                threaded_document.SendCommand("HOF\n_W\n-1,-1\n101,101\n\n")
            except Exception as exc:
                command_errors.append(str(exc))
            finally:
                pythoncom.CoUninitialize()

        hof_thread = threading.Thread(target=send_hof_command, name="HexFillHofCommand")
        hof_thread.daemon = True
        hof_thread.start()

        import win32con
        import win32gui
        import win32process
        deadline = time.time() + min(timeout, 30)
        while time.time() < deadline and dialog_handle is None:
            windows = []

            def collect_window(hwnd, _):
                _, process_id = win32process.GetWindowThreadProcessId(hwnd)
                if int(process_id) == int(session.process_id) and win32gui.GetWindowText(hwnd) == "HOF 偏移命令":
                    windows.append(hwnd)

            win32gui.EnumWindows(collect_window, None)
            if windows:
                dialog_handle = windows[0]
                break
            time.sleep(0.1)
        if dialog_handle is None:
            raise TimeoutError("HOF dialog was not found")

        children = []
        win32gui.EnumChildWindows(dialog_handle, lambda hwnd, _: children.append(hwnd), None)
        distance_edit = None
        confirm_button = None
        for child in children:
            class_name = win32gui.GetClassName(child).upper()
            text_value = win32gui.GetWindowText(child)
            if "EDIT" in class_name and text_value == "0.5":
                distance_edit = child
            if "BUTTON" in class_name and text_value == "确认":
                confirm_button = child
        if distance_edit is None:
            errors.append("HOF distance input was not found")
        else:
            win32gui.SendMessage(distance_edit, win32con.WM_SETTEXT, 0, "5")
        if confirm_button is None:
            errors.append("HOF confirm button was not found")
        else:
            win32gui.SendMessage(confirm_button, win32con.BM_CLICK, 0, 0)
        dialog_handle = None
        hof_thread.join(timeout=timeout)
        if hof_thread.is_alive():
            errors.append("HOF command thread did not finish after confirming the dialog")
        if command_errors:
            errors.append("HOF command dispatch failed: {}".format(command_errors[-1]))
        time.sleep(0.5)
        try:
            session.acad.WindowState = 2
        except Exception:
            pass
        if not session.wait_idle(timeout=timeout):
            errors.append("HOF command did not return to idle")
        session.wait_modelspace_stable(timeout=10)

        polylines = session.get_polylines()
        areas = sorted(polyline["area"] for polyline in polylines)
        if len(polylines) != 2:
            errors.append("expected original and offset polyline, got {}".format(len(polylines)))
        if len(areas) == 2 and (abs(areas[0] - 10000.0) > 1.0 or abs(areas[1] - 12100.0) > 2.0):
            errors.append("unexpected HOF areas {}".format(areas))

        print("polyline_areas={}".format([round(area, 3) for area in areas]))
        for error in errors:
            print("  ERROR: {}".format(error))
        print("  [{}] HOF modal dialog workflow".format("PASS" if not errors else "FAIL"))
        return len(errors) == 0
    except Exception as exc:
        print("  ERROR: {}".format(exc))
        print("  [FAIL] HOF modal dialog workflow")
        return False
    finally:
        if dialog_handle is not None:
            try:
                import win32con
                import win32gui
                children = []
                win32gui.EnumChildWindows(dialog_handle, lambda hwnd, _: children.append(hwnd), None)
                for child in children:
                    if "BUTTON" in win32gui.GetClassName(child).upper() and win32gui.GetWindowText(child) == "取消":
                        win32gui.SendMessage(child, win32con.BM_CLICK, 0, 0)
                        break
            except Exception:
                pass
        try:
            session.doc.SendCommand("\x03\x03")
            session.wait_idle(timeout=5)
        except Exception:
            pass


def run_hof_dialog(dll_path, timeout):
    with AcadSession(dll_path) as session:
        return run_hof_dialog_case(session, timeout)


def get_balloon_sequences(document):
    sequences = []
    model_space = document.ModelSpace
    for index in range(model_space.Count):
        entity = model_space.Item(index)
        if str(getattr(entity, "EntityName", "")) != "AcDbBlockReference":
            continue
        if str(getattr(entity, "Layer", "")) != "球标":
            continue
        try:
            attributes = entity.GetAttributes()
        except Exception:
            continue
        for attribute in attributes:
            if str(getattr(attribute, "TagString", "")).upper() == "SEQ":
                sequences.append((int(attribute.TextString), entity))
                break
    return sorted(sequences, key=lambda item: item[0])


def place_test_balloons(session, points, timeout):
    command = "HBAL\n" + "".join("{:.8f},{:.8f}\n".format(point[0], point[1]) for point in points) + "Done\n"
    session.doc.SendCommand(command)
    if not session.wait_idle(timeout=timeout):
        raise TimeoutError("HBAL did not return to idle after placing test balloons")
    session.wait_modelspace_stable(timeout=10)


def run_hbal_multidoc_case(session, timeout):
    print("\n--- HBAL multi-document renumber workflow ---")
    document_a = session.doc
    document_b = None
    errors = []
    try:
        session.run_script(["_.ERASE", "_ALL", ""], timeout=timeout)
        place_test_balloons(session, [(0, 0), (10, 0), (20, 0)], timeout)
        document_b = session.add_document()
        session.activate_document(document_b)
        session.doc = document_b
        session.prepare_document(document_b)
        place_test_balloons(session, [(0, 20), (10, 20), (20, 20)], timeout)

        session.activate_document(document_a)
        session.doc = document_a
        balloons_a = get_balloon_sequences(document_a)
        if [item[0] for item in balloons_a] != [1, 2, 3]:
            errors.append("document A setup sequences are invalid")
        else:
            balloons_a[1][1].Delete()
            session.activate_document(document_b)
            session.doc = document_b
            session.wait_idle(timeout=timeout)
            time.sleep(2.0)

        sequences_a = [item[0] for item in get_balloon_sequences(document_a)]
        sequences_b = [item[0] for item in get_balloon_sequences(document_b)]
        if sequences_a != [1, 2]:
            errors.append("document A was not renumbered after deleting #2: {}".format(sequences_a))
        if sequences_b != [1, 2, 3]:
            errors.append("document B was incorrectly renumbered: {}".format(sequences_b))

        print("document_A={} document_B={}".format(sequences_a, sequences_b))
        for error in errors:
            print("  ERROR: {}".format(error))
        print("  [{}] HBAL multi-document renumber workflow".format("PASS" if not errors else "FAIL"))
        return len(errors) == 0
    except Exception as exc:
        print("  ERROR: {}".format(exc))
        print("  [FAIL] HBAL multi-document renumber workflow")
        return False
    finally:
        for document in (document_b, document_a):
            if document is None:
                continue
            session.close_document(document)
        session.doc = None
        session.open_document()


def run_hbal_multidoc(dll_path, timeout):
    with AcadSession(dll_path) as session:
        return run_hbal_multidoc_case(session, timeout)


def run_performance_case(session, timeout, rectangle_count=400):
    print("\n--- HJOIN/HCHK large-selection performance ---")
    lines = ["_.ERASE", "_ALL", ""]
    columns = 40
    for index in range(rectangle_count):
        column = index % columns
        row = index // columns
        x = column * 20.0
        y = row * 20.0
        lines.extend(["_.RECTANG", "{:.3f},{:.3f}".format(x, y), "{:.3f},{:.3f}".format(x + 10.0, y + 10.0)])
    session.run_script(lines, timeout=timeout)

    errors = []
    start = time.perf_counter()
    session.run_script(["HCHK", "_ALL", ""], timeout=timeout)
    hchk_seconds = time.perf_counter() - start
    markers = session.get_layer_entities("HCHK_图形健康标识")
    if markers:
        errors.append("HCHK reported {} false markers on separated rectangles".format(len(markers)))

    start = time.perf_counter()
    session.run_script(["HJOIN", "_ALL", ""], timeout=timeout)
    hjoin_seconds = time.perf_counter() - start
    polylines = session.get_polylines()
    if len(polylines) != rectangle_count:
        errors.append("HJOIN changed separated rectangle count {} -> {}".format(rectangle_count, len(polylines)))

    print("rectangles={} HCHK={:.2f}s HJOIN={:.2f}s".format(rectangle_count, hchk_seconds, hjoin_seconds))
    for error in errors:
        print("  ERROR: {}".format(error))
    print("  [{}] HJOIN/HCHK large-selection performance".format("PASS" if not errors else "FAIL"))
    return len(errors) == 0


def run_performance(dll_path, timeout):
    with AcadSession(dll_path) as session:
        return run_performance_case(session, timeout)


def run_locked_layer_case(session, timeout):
    print("\n--- locked-layer batch isolation ---")
    errors = []
    session.run_script([
        "_.ERASE", "_ALL", "",
        "_.-LAYER", "_M", "LOCKED_KEEP", "",
        "_.RECTANG", "0,0", "100,100",
        "_.-LAYER", "_S", "0", "",
        "_.RECTANG", "150,0", "250,100",
        "_.-LAYER", "_LO", "LOCKED_KEEP", "",
        "HPLIT", "_ALL", "", "_non", "-10,50", "_non", "260,50", "",
    ], timeout=timeout)
    hplit_polylines = session.get_polylines()
    hplit_locked = [polyline for polyline in hplit_polylines if polyline["layer"].lower() == "locked_keep"]
    if len(hplit_polylines) != 3:
        errors.append("HPLIT expected locked source plus two unlocked results, got {}".format(len(hplit_polylines)))
    if len(hplit_locked) != 1 or abs(hplit_locked[0]["area"] - 10000.0) > 1.0:
        errors.append("HPLIT changed the locked source polyline")

    session.run_script([
        "_.-LAYER", "_U", "LOCKED_KEEP", "",
        "_.ERASE", "_ALL", "",
        "_.-LAYER", "_S", "LOCKED_KEEP", "",
        "_.RECTANG", "0,0", "100,100",
        "_.-LAYER", "_S", "0", "",
        "_.RECTANG", "150,0", "200,100",
        "_.RECTANG", "200,0", "250,100",
        "_.-LAYER", "_LO", "LOCKED_KEEP", "",
        "HJOIN", "_ALL", "",
    ], timeout=timeout)
    hjoin_polylines = session.get_polylines()
    hjoin_locked = [polyline for polyline in hjoin_polylines if polyline["layer"].lower() == "locked_keep"]
    if len(hjoin_polylines) != 2:
        errors.append("HJOIN expected locked source plus one merged result, got {}".format(len(hjoin_polylines)))
    if len(hjoin_locked) != 1 or abs(hjoin_locked[0]["area"] - 10000.0) > 1.0:
        errors.append("HJOIN changed the locked source polyline")

    session.run_script([
        "_.-LAYER", "_U", "LOCKED_KEEP", "",
        "_.ERASE", "_ALL", "",
    ], timeout=timeout)

    print("HPLIT_count={} HJOIN_count={} locked_preserved={}".format(
        len(hplit_polylines), len(hjoin_polylines), not errors))
    for error in errors:
        print("  ERROR: {}".format(error))
    print("  [{}] locked-layer batch isolation".format("PASS" if not errors else "FAIL"))
    return len(errors) == 0


def run_locked_layer(dll_path, timeout):
    with AcadSession(dll_path) as session:
        return run_locked_layer_case(session, timeout)


def run_all_commands(dll_path, timeout, case_name=None):
    all_ok = True
    with AcadSession(dll_path) as session:
        if not run_grid_in_session(session, timeout, None):
            all_ok = False
        if not run_hix_atomic_case(session, timeout):
            all_ok = False
        if not run_hbr_had_case(session, timeout):
            all_ok = False
        if not run_hof_dialog_case(session, timeout):
            all_ok = False
        if not run_hbal_multidoc_case(session, timeout):
            all_ok = False
        if not run_locked_layer_case(session, timeout):
            all_ok = False
        if not run_hplit_in_session(session, timeout, case_name):
            all_ok = False
        if not run_hset_in_session(session, timeout, None):
            all_ok = False
        if not run_hgap_in_session(session, timeout, None):
            all_ok = False
        if not run_hjoin_in_session(session, timeout, None):
            all_ok = False
        if not run_hchk_in_session(session, timeout, None):
            all_ok = False
        if not run_hnm_in_session(session, timeout, None):
            all_ok = False
        if not run_htc_in_session(session, timeout):
            all_ok = False
    return all_ok


def run_lite_commands(dll_path, timeout):
    # 【已废弃】2026-08-16 起只交付完整版 HexFill.dll，Lite 版不再构建/维护。
    # 本函数仅供验证历史 Lite DLL 时保留。
    all_ok = True
    with AcadSession(dll_path) as session:
        if not run_grid_in_session(session, timeout, None):
            all_ok = False
        if not run_hix_atomic_case(session, timeout):
            all_ok = False
        if not run_hof_dialog_case(session, timeout):
            all_ok = False
        if not run_hbal_multidoc_case(session, timeout):
            all_ok = False
        if not run_hnm_in_session(session, timeout, "single_root_skip"):
            all_ok = False
    return all_ok


def find_net_alias():
    roaming_root = Path(os.environ.get("APPDATA", "")) / "Autodesk" / "AutoCAD 2018"
    if not roaming_root.exists():
        return None
    for pgp_path in roaming_root.rglob("acad.pgp"):
        try:
            for line_number, line in enumerate(pgp_path.read_text(encoding="utf-8", errors="ignore").splitlines(), 1):
                normalized = line.strip().upper().replace(" ", "")
                if normalized.startswith("NET,") and "*NETLOAD" in normalized:
                    return {
                        "file": str(pgp_path),
                        "line": line_number,
                        "text": line.strip(),
                    }
        except Exception:
            continue
    return None


def run_net_state_case(session, timeout):
    print("\n--- NET readiness and AutoCAD state ---")
    session.run_script(["_.ZOOM", "_E"], timeout=timeout)
    direct_command_ok = int(session.doc.GetVariable("CMDACTIVE")) == 0
    actual_state = {
        name: session.doc.GetVariable(name)
        for name, _ in USER_INTERFACE_VARIABLE_VALUES
    }
    state_ok = all(actual_state[name] == expected for name, expected in USER_INTERFACE_VARIABLE_VALUES)
    alias_data = find_net_alias()

    print("state={}".format(json.dumps(actual_state, ensure_ascii=False)))
    print("net_alias={}".format(json.dumps(alias_data, ensure_ascii=False)))
    print("direct_command_ok={}".format(direct_command_ok))
    errors = []
    if not state_ok:
        errors.append("AutoCAD user-interface variables were not restored")
    if alias_data is None:
        errors.append("NET -> NETLOAD alias was not found")
    if not direct_command_ok:
        errors.append("direct command stream did not return to idle")
    for error in errors:
        print("  ERROR: {}".format(error))
    print("  [{}] NET readiness and AutoCAD state".format("PASS" if not errors else "FAIL"))
    return len(errors) == 0


def run_net_state(dll_path, timeout):
    with AcadSession(dll_path) as session:
        return run_net_state_case(session, timeout)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("command", nargs="?", default="HPLIT")
    parser.add_argument("--dll", default=None)
    parser.add_argument("--timeout", type=int, default=60)
    parser.add_argument("--case", default=None)
    parser.add_argument("--scr-only", action="store_true")
    args = parser.parse_args()

    command = args.command.upper()
    if command not in ("HPLIT", "HGAP", "HJOIN", "HSET", "HCHK", "HNM", "HIX", "HAD", "HADPERF", "HRSPERF", "HOF", "HBAL", "GRID", "PERF", "LOCKED", "LITE", "NETSTATE", "HTC", "ALL"):
        raise SystemExit("Supported AutoCAD data verification commands: HPLIT, HGAP, HJOIN, HSET, HCHK, HNM, HIX, HAD, HADPERF, HRSPERF, HOF, HBAL, GRID, PERF, LOCKED, LITE, NETSTATE, HTC, ALL.")

    if args.scr_only:
        if command in ("HPLIT", "ALL"):
            for case in selected_cases(args.case):
                print("=== {} ===".format(case["name"]))
                print("\n".join(hplit_script(case)))
        if command in ("HGAP", "ALL"):
            for case in selected_hgap_cases(args.case if command == "HGAP" else None):
                print("=== {} ===".format(case["name"]))
                print("\n".join(case["script"]()))
        if command in ("HJOIN", "ALL"):
            for case in selected_hjoin_cases(args.case if command == "HJOIN" else None):
                print("=== {} ===".format(case["name"]))
                print("\n".join(case["script"]()))
        if command in ("HSET", "ALL"):
            for case in selected_hset_cases(args.case if command == "HSET" else None):
                print("=== {} ===".format(case["name"]))
                print("\n".join(case["script"]()))
        if command in ("HCHK", "ALL"):
            for case in selected_hchk_cases(args.case if command == "HCHK" else None):
                print("=== {} ===".format(case["name"]))
                print("\n".join(case["script"]()))
        if command in ("HNM", "ALL"):
            for case in selected_hnm_cases(args.case if command == "HNM" else None):
                print("=== {} ===".format(case["name"]))
                print("\n".join(case["script"]()))
        if command in ("GRID", "LITE", "ALL"):
            for case in selected_grid_cases(args.case if command == "GRID" else None):
                print("=== {} ===".format(case["name"]))
                print("\n".join(case["script"]()))
        return

    dll_path = Path(args.dll).expanduser().resolve() if args.dll else latest_dll()
    if dll_path is None or not dll_path.exists():
        raise SystemExit("HexFill DLL not found. Build the project first or pass --dll.")

    print("DLL: {}".format(dll_path))
    if command == "HPLIT":
        ok = run_hplit(dll_path, args.timeout, args.case)
    elif command == "HGAP":
        ok = run_hgap(dll_path, args.timeout, args.case)
    elif command == "HJOIN":
        ok = run_hjoin(dll_path, args.timeout, args.case)
    elif command == "HSET":
        ok = run_hset(dll_path, args.timeout, args.case)
    elif command == "HCHK":
        ok = run_hchk(dll_path, args.timeout, args.case)
    elif command == "HNM":
        ok = run_hnm(dll_path, args.timeout, args.case)
    elif command == "GRID":
        ok = run_grid(dll_path, args.timeout, args.case)
    elif command == "HIX":
        ok = run_hix(dll_path, args.timeout)
    elif command == "HAD":
        ok = run_hbr_had(dll_path, args.timeout)
    elif command == "HADPERF":
        ok = run_had_performance(dll_path, args.timeout)
    elif command == "HRSPERF":
        ok = run_hrs_performance(dll_path, args.timeout)
    elif command == "HOF":
        ok = run_hof_dialog(dll_path, args.timeout)
    elif command == "HBAL":
        ok = run_hbal_multidoc(dll_path, args.timeout)
    elif command == "PERF":
        ok = run_performance(dll_path, args.timeout)
    elif command == "LOCKED":
        ok = run_locked_layer(dll_path, args.timeout)
    elif command == "LITE":
        # 【已废弃】2026-08-16 起只交付完整版 HexFill.dll，Lite 版不再构建/维护；
        # 本分支仅用于验证历史 Lite DLL 时保留，正常交付流程不再使用。
        print("WARNING: LITE suite is deprecated (single full DLL policy since 2026-08-16).")
        ok = run_lite_commands(dll_path, args.timeout)
    elif command == "NETSTATE":
        ok = run_net_state(dll_path, args.timeout)
    elif command == "HTC":
        ok = run_htc(dll_path, args.timeout)
    else:
        ok = run_all_commands(dll_path, args.timeout, args.case)
    raise SystemExit(0 if ok else 1)


if __name__ == "__main__":
    main()
