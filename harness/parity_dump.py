#!/usr/bin/env python3
"""Parity dump: for each named control, print what an AT-SPI client can rely on —
role, interfaces, action names, and the load-bearing states. Run against UnoDemo
(this bridge) and AvaloniaDemo (the native reference backend), then diff.

Usage: parity_dump.py <app-substr>
"""
import sys, time
import gi
gi.require_version("Atspi", "2.0")
from gi.repository import Atspi

APP = (sys.argv[1] if len(sys.argv) > 1 else "unodemo").lower()

# the six parity controls, by accessible name
CONTROLS = ["Open File Manager", "Save Document", "Search box",
            "Enable notifications", "Volume", "Theme selector", "Light", "Dark"]

# the states a driver actually branches on
STATES = ["CHECKED", "EDITABLE", "ENABLED", "EXPANDABLE", "FOCUSABLE",
          "SELECTABLE", "SELECTED", "SENSITIVE", "SHOWING", "VISIBLE"]

def find_app(substr):
    d = Atspi.get_desktop(0)
    for i in range(d.get_child_count()):
        a = d.get_child_at_index(i)
        if a and substr in (a.get_name() or "").lower():
            return a
    return None

def collect(acc, out):
    try:
        name = acc.get_name() or ""
        if name in CONTROLS and name not in out:   # keep-first: the control, not its inner label
            out[name] = acc
    except Exception:
        pass
    for i in range(acc.get_child_count()):
        c = acc.get_child_at_index(i)
        if c is not None:
            collect(c, out)

def describe(acc):
    role = acc.get_role_name()
    ifaces = sorted(i for i in (acc.get_interfaces() or [])
                    if i not in ("Accessible", "Component", "Collection", "Hypertext"))
    ss = acc.get_state_set()
    states = sorted(s for s in STATES if ss.contains(getattr(Atspi.StateType, s)))
    actions = []
    try:
        n = Atspi.Action.get_n_actions(acc)
        actions = sorted(Atspi.Action.get_action_name(acc, i) for i in range(n))
    except Exception:
        pass
    return role, ifaces, actions, states

def main():
    Atspi.init()
    app = None
    for _ in range(20):
        app = find_app(APP)
        if app: break
        time.sleep(0.5)
    if not app:
        print(f"app ~{APP!r} not found"); sys.exit(2)

    found = {}
    collect(app, found)
    for name in CONTROLS:
        if name not in found:
            print(f"{name:22s} | MISSING")
            continue
        role, ifaces, actions, states = describe(found[name])
        print(f"{name:22s} | role={role} | ifaces={','.join(ifaces)} "
              f"| actions={','.join(actions)} | states={','.join(states)}")
    Atspi.exit()

if __name__ == "__main__":
    main()
