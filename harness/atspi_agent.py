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

def iface(acc, getter, legacy):
    try: return getattr(acc, getter)()
    except Exception:
        try: return getattr(acc, legacy)()
        except Exception: return None

def proof_value(app):
    """Drive the Volume slider through org.a11y.atspi.Value: read → set → read back."""
    sl = walk(app, "slider", "volume")
    if not sl: print("agent > no slider found"); return False
    v = iface(sl, "get_value_iface", "get_value")
    lo, hi, cur = v.get_minimum_value(), v.get_maximum_value(), v.get_current_value()
    print(f"agent > [slider] '{sl.get_name()}' range=[{lo},{hi}] value={cur}")
    target = 80.0 if cur != 80.0 else 20.0
    v.set_current_value(target)
    time.sleep(1.5)
    after = iface(sl, "get_value_iface", "get_value").get_current_value()
    print(f"agent > set_current_value({target}) → read back {after}")
    ok = abs(after - target) < 0.01
    print(f"agent > {'✓ PROOF: slider moved through the Value interface.' if ok else '✗ value did not change'}")
    return ok

def proof_text(app):
    """Type into the Search box through org.a11y.atspi.EditableText, read back via Text."""
    tb = walk(app, "entry", "search")
    if not tb: print("agent > no entry found"); return False
    before = Atspi.Text.get_text(tb, 0, -1)
    print(f"agent > [entry] '{tb.get_name()}' text before = {before!r}")
    Atspi.EditableText.set_text_contents(tb, "hello from the bus")
    time.sleep(1.5)
    after = Atspi.Text.get_text(tb, 0, -1)
    print(f"agent > set_text_contents(...) → read back {after!r}")
    ok = after == "hello from the bus"
    print(f"agent > {'✓ PROOF: text written through EditableText, read back through Text.' if ok else '✗ text did not change'}")
    return ok

def proof_combo(app):
    """Expand the Theme selector, enumerate its items, select 'Dark' — all over the bus."""
    cb = walk(app, "combo box", "theme")
    if not cb: print("agent > no combo box found"); return False

    print(f"agent > invoke Action.do_action(0) → {action_name(cb, 0)!r}")
    do_action(cb, 0)
    time.sleep(1.5)
    expanded = cb.get_state_set().contains(Atspi.StateType.EXPANDED)
    print(f"agent > EXPANDED state = {expanded}")

    # enumerate app-wide after expanding — our bridge parents the items under the
    # combo, but the native Avalonia backend realizes them inside a separate
    # top-level PopupRoot frame, so the generic flow is expand → search the app
    def list_items(acc, depth=0):
        found = []
        for i in range(acc.get_child_count()):
            c = acc.get_child_at_index(i)
            if c is None: continue
            if "list item" in (c.get_role_name() or ""): found.append(c)
            elif depth < 12: found.extend(list_items(c, depth + 1))
        return found
    items = list_items(app)
    names = [(c.get_name() or "?") for c in items]
    print(f"agent > [combo box] '{cb.get_name()}' list items = {names}")
    if not names: print("agent > ✗ no list items on the bus"); return False

    target = names.index("Dark") if "Dark" in names else 1
    print(f"agent > Selection.select_child({target})  → {names[target]!r}")
    Atspi.Selection.select_child(cb, target)
    time.sleep(1.5)
    sel_ok = Atspi.Selection.is_child_selected(cb, target)
    item_sel = items[target].get_state_set().contains(Atspi.StateType.SELECTED)
    print(f"agent > is_child_selected({target}) = {sel_ok}, item SELECTED state = {item_sel}")
    ok = sel_ok and item_sel and expanded
    print(f"agent > {'✓ PROOF: combo expanded, items enumerated, selection made over the bus.' if ok else '✗ combo flow incomplete'}")
    return ok

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

    if ROLE == "all":   # step 5+6: slider (Value) + entry (EditableText/Text) + combo (Selection)
        ok = proof_value(app)
        ok = proof_text(app) and ok
        ok = proof_combo(app) and ok
        Atspi.exit(); sys.exit(0 if ok else 5)

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
