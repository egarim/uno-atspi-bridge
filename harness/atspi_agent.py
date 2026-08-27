#!/usr/bin/env python3
"""An AT-SPI *agent*: it perceives and acts through the accessibility tree only.

  1. read the tree, find a control by role + name (no pixels, no coordinates)
  2. read its CHECKED state (before)
  3. invoke it through the Action interface  →  Action.do_action(0)   ← "the click"
  4. read CHECKED again (after) and assert it flipped

This is the client half of the write-path we added to the bridge: DoAction travels the
same a11y bus the tree was read from, and the app's real control actually changes.

Usage: atspi_agent.py [app-substr] [role-substr] [name-substr]
       defaults: UnoDemo  "check box"  "notification"
"""
import sys, time
import gi
gi.require_version("Atspi", "2.0")
from gi.repository import Atspi

APP  = (sys.argv[1] if len(sys.argv) > 1 else "unodemo").lower()
ROLE = (sys.argv[2] if len(sys.argv) > 2 else "check box").lower()
NAME = (sys.argv[3] if len(sys.argv) > 3 else "notification").lower()

def find_app(substr):
    d = Atspi.get_desktop(0)
    for i in range(d.get_child_count()):
        a = d.get_child_at_index(i)
        if a and substr in (a.get_name() or "").lower():
            return a
    return None

def walk(acc, role, name):
    try:
        if role in (acc.get_role_name() or "").lower() and name in (acc.get_name() or "").lower():
            return acc
    except Exception:
        pass
    for i in range(acc.get_child_count()):
        c = acc.get_child_at_index(i)
        if c is not None:
            hit = walk(c, role, name)
            if hit is not None:
                return hit
    return None

def is_checked(acc):
    return acc.get_state_set().contains(Atspi.StateType.CHECKED)

def do_action(acc, index=0):
    # gi exposes Action either as a bound method or via the interface class
    try:
        return acc.do_action(index)
    except Exception:
        return Atspi.Action.do_action(acc, index)

def n_actions(acc):
    try:
        return acc.get_n_actions()
    except Exception:
        try: return Atspi.Action.get_n_actions(acc)
        except Exception: return 0

def action_name(acc, i=0):
    try:
        return acc.get_action_name(i)
    except Exception:
        try: return Atspi.Action.get_name(acc, i)
        except Exception: return "?"

def main():
    Atspi.init()
    print(f"agent > looking for app ~ {APP!r}")
    app = None
    for _ in range(20):
        app = find_app(APP)
        if app: break
        time.sleep(0.5)
    if not app:
        print("agent > app not found on the AT-SPI desktop"); sys.exit(2)
    print(f"agent > app: {app.get_name()!r}")

    print(f"agent > read tree → find [{ROLE}] name~{NAME!r}")
    target = walk(app, ROLE, NAME)
    if not target:
        print("agent > no matching control in the tree"); sys.exit(3)

    ext = target.get_extents(Atspi.CoordType.SCREEN)
    print(f"agent > matched [{target.get_role_name()}] {target.get_name()!r} "
          f"box=({ext.x},{ext.y},{ext.width},{ext.height})")
    print(f"agent > it advertises {n_actions(target)} action(s): "
          f"{action_name(target,0)!r}   ← this is how I click")

    before = is_checked(target)
    print(f"agent > CHECKED before = {before}")

    print(f"agent > invoke  Action.do_action(0)  … (no mouse, no coordinates)")
    ok = do_action(target, 0)
    print(f"agent > do_action returned {ok}")

    # let the UI thread apply it + the bridge emit the state-changed event
    time.sleep(1.5)
    after = is_checked(target)
    print(f"agent > CHECKED after  = {after}")

    if after != before:
        print(f"agent > ✓ PROOF: the control flipped {before} → {after} — the agent "
              f"clicked through the accessibility tree.")
        Atspi.exit(); sys.exit(0)
    print("agent > ✗ state did not change"); Atspi.exit(); sys.exit(4)

if __name__ == "__main__":
    main()
