from acad_verify import (
    AcadSession,
    SNAPSHOT_VARIABLE_NAMES,
    TEST_VARIABLE_VALUES,
    USER_INTERFACE_VARIABLE_VALUES,
)


class FakeDocument:
    def __init__(self, values, close_error=False):
        self.values = dict(values)
        self.close_error = close_error
        self.closed_values = None

    def GetVariable(self, name):
        return self.values[name]

    def SetVariable(self, name, value):
        self.values[name] = value

    def Activate(self):
        return None

    def Close(self, save_changes):
        self.closed_values = dict(self.values)
        if self.close_error:
            raise RuntimeError("simulated close failure")


def baseline_values():
    return {
        "FILEDIA": 1,
        "CMDDIA": 1,
        "CMDECHO": 1,
        "SECURELOAD": 1,
        "DYNMODE": 3,
        "DYNPROMPT": 1,
        "USERR1": 0.0,
        "OSMODE": 4133,
        "ORTHOMODE": 1,
        "SNAPMODE": 1,
        "CMDACTIVE": 0,
    }


def verify_prepared(document):
    return all(document.values[name] == value for name, value in TEST_VARIABLE_VALUES)


def verify_restored(document, expected):
    return all(document.values[name] == expected[name] for name in SNAPSHOT_VARIABLE_NAMES)


def run_all():
    expected = baseline_values()

    session = AcadSession("HexFill.dll")
    document = FakeDocument(expected)
    session.doc = document
    session.prepare_document()
    normal_prepared = verify_prepared(document)
    dynamic_input_preserved = document.values["DYNMODE"] == 3 and document.values["DYNPROMPT"] == 1
    session.close_document()
    normal_restored = verify_restored(document, expected)
    normal_closed_restored = all(
        document.closed_values[name] == expected[name]
        for name in SNAPSHOT_VARIABLE_NAMES
    )

    failed_session = AcadSession("HexFill.dll")
    failed_document = FakeDocument(expected, close_error=True)
    failed_session.doc = failed_document
    failed_session.prepare_document()
    failed_session.close_document()
    failed_close_restored = verify_restored(failed_document, expected)

    leaked_values = baseline_values()
    leaked_values.update({"FILEDIA": 0, "SECURELOAD": 0, "DYNMODE": 0, "DYNPROMPT": 0})
    leaked_session = AcadSession("HexFill.dll")
    leaked_document = FakeDocument(leaked_values)
    leaked_session.doc = leaked_document
    leaked_session.prepare_document()
    leaked_session.close_document()
    interface_repaired = all(
        leaked_document.values[name] == value
        for name, value in USER_INTERFACE_VARIABLE_VALUES
    )

    multidoc_session = AcadSession("HexFill.dll")
    document_a = FakeDocument(expected)
    document_b = FakeDocument(expected)
    multidoc_session.doc = document_a
    multidoc_session.prepare_document(document_a)
    multidoc_session.prepare_document(document_b)
    multidoc_prepared = verify_prepared(document_a) and verify_prepared(document_b)
    multidoc_session.close_document(document_b)
    multidoc_session.close_document(document_a)
    multidoc_restored = verify_restored(document_a, expected) and verify_restored(document_b, expected)

    checks = (
        ("Normal session applies test variables", normal_prepared),
        ("Dynamic input remains enabled during tests", dynamic_input_preserved),
        ("Normal close restores original variables", normal_restored and normal_closed_restored),
        ("Failed close still restores variables", failed_close_restored),
        ("Leaked dialog and dynamic-input state is repaired", interface_repaired),
        ("Multiple documents receive test variables", multidoc_prepared),
        ("Multiple documents restore the same baseline", multidoc_restored),
    )
    all_ok = all(result for _, result in checks)
    print("\n" + "=" * 50)
    print("  AutoCAD state restoration: {}".format("PASS" if all_ok else "FAIL"))
    print("=" * 50)
    for label, result in checks:
        print("  [{}] {}".format("+" if result else "-", label))
    return all_ok


if __name__ == "__main__":
    raise SystemExit(0 if run_all() else 1)
