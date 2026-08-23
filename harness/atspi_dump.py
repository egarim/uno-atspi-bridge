#!/usr/bin/env python3
"""Walk the AT-SPI accessibility tree and print every widget's role, name,
bounding box, and states. This is an AT-SPI *client* (like Orca) — it proves the
app under test is publishing an accessible tree, and shows exactly what an
AT-SPI-first agent would read.

Usage: atspi_dump.py [app-name-substring]
"""
import sys
import gi
gi.require_version("Atspi", "2.0")
from gi.repository import Atspi

WANT = (sys.argv[1] if len(sys.argv) > 1 else "").lower()

INTERESTING_STATES = [
    Atspi.StateType.FOCUSABLE, Atspi.StateType.ENABLED,
    Atspi.StateType.SHOWING, Atspi.StateType.SENSITIVE,
    Atspi.StateType.VISIBLE, Atspi.StateType.CHECKED,
]

def states_of(acc):
    ss = acc.get_state_set()
    return [Atspi.StateType(s).value_nick for s in INTERESTING_STATES if ss.contains(s)]

def dump(acc, depth=0, out=None):
    try:
        role = acc.get_role_name()
        name = acc.get_name() or ""
    except Exception:
        return
    box = ""
    try:
        comp = acc.get_component_iface() if hasattr(acc, "get_component_iface") else acc
        ext = acc.get_extents(Atspi.CoordType.SCREEN)
        box = f"box=({ext.x},{ext.y},{ext.width},{ext.height})"
    except Exception:
        box = "box=(n/a)"
    st = ",".join(states_of(acc))
    indent = "  " * depth
    line = f"{indent}[{role}] {name!r} {box} states={st}"
    print(line)
    if out is not None:
        out.append(line)
    for i in range(acc.get_child_count()):
        child = acc.get_child_at_index(i)
        if child is not None:
            dump(child, depth + 1, out)

def main():
    Atspi.init()
    desktop = Atspi.get_desktop(0)
    n = desktop.get_child_count()
    print(f"# AT-SPI desktop has {n} application(s) registered\n")
    found = False
    for i in range(n):
        app = desktop.get_child_at_index(i)
        if app is None:
            continue
        app_name = (app.get_name() or "").strip()
        if WANT and WANT not in app_name.lower():
            continue
        found = True
        print(f"=== application: {app_name!r} ===")
        dump(app)
        print()
    if WANT and not found:
        print(f"# no application matching {WANT!r} found among "
              f"{[Atspi.get_desktop(0).get_child_at_index(i).get_name() for i in range(n)]}")
        sys.exit(2)
    Atspi.exit()

if __name__ == "__main__":
    main()
