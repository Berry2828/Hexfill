#!/usr/bin/env python3
# HexFill Verification Runner
# Unified test runner for all HexFill command verifications
# Usage: python verify/runner.py              # run all tests
#   python verify/runner.py HPLIT             # run specific command
#   python verify/runner.py --list            # list available test suites

import sys, os, argparse, importlib, traceback
from pathlib import Path

VERIFY_DIR = Path(__file__).parent.absolute()


def discover_test_suites():
    """Discover all test_*.py modules with run_all() in verify/ directory."""
    suites = {}
    for f in sorted(VERIFY_DIR.glob("test_*.py")):
        mod_name = f.stem
        try:
            mod = importlib.import_module(mod_name)
            if hasattr(mod, "run_all"):
                cmd = mod_name.replace("test_", "").upper()
                suites[cmd] = mod_name
        except ImportError as e:
            print(f"WARNING: Cannot import {mod_name}: {e}")
    return suites


def run_suite(cmd_name, suites):
    """Run a specific test suite by command name."""
    cmd_upper = cmd_name.upper()
    if cmd_upper not in suites:
        print(f"ERROR: No test suite for command '{cmd_name}'")
        print(f"Available: {', '.join(sorted(suites.keys()))}")
        return False

    mod_name = suites[cmd_upper]
    try:
        mod = importlib.import_module(mod_name)
    except ImportError as e:
        print(f"ERROR: Cannot import {mod_name}: {e}")
        return False

    print(f"\n{'#'*60}")
    print(f"#  HexFill Verification: {cmd_upper}")
    print(f"{'#'*60}")
    return mod.run_all()


def run_all(suites):
    """Run all discovered test suites."""
    all_pass = True
    for cmd, mod_name in sorted(suites.items()):
        ok = run_suite(cmd, suites)
        if not ok:
            all_pass = False
    return all_pass


def list_suites(suites):
    """List available test suites."""
    print("Available test suites:")
    for cmd, mod_name in sorted(suites.items()):
        mod_path = VERIFY_DIR / f"{mod_name}.py"
        status = "[found]" if mod_path.exists() else "[MISSING]"
        print(f"  {cmd:<8} -> {mod_name} {status}")


def main():
    parser = argparse.ArgumentParser(description="HexFill Verification Runner")
    parser.add_argument("command", nargs="?", help="Command name to test (e.g., HPLIT)")
    parser.add_argument("--list", "-l", action="store_true", help="List available test suites")
    args = parser.parse_args()

    sys.path.insert(0, str(VERIFY_DIR))
    suites = discover_test_suites()

    if args.list:
        list_suites(suites)
        return 0

    if args.command:
        ok = run_suite(args.command, suites)
    else:
        list_suites(suites)
        ok = run_all(suites)

    if ok:
        print(f"\n{'='*50}")
        print("  VERIFICATION PASSED")
        print(f"{'='*50}")
    else:
        print(f"\n{'='*50}")
        print("  VERIFICATION FAILED")
        print(f"{'='*50}")

    return 0 if ok else 1


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as e:
        print(f"\nFATAL: {e}")
        traceback.print_exc()
        sys.exit(2)
