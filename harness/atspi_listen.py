#!/usr/bin/env python3
"""Listen for AT-SPI object:state-changed events and print them — this is what a
screen reader (Orca) does to track focus and toggles live. Runs for a fixed window
then exits."""
import sys
import gi
gi.require_version("Atspi", "2.0")
from gi.repository import Atspi, GLib

SECONDS = int(sys.argv[1]) if len(sys.argv) > 1 else 12
count = 0

def on_event(e):
    global count
    try:
        src = e.source
        name = (src.get_name() if src else "") or ""
        role = (src.get_role_name() if src else "") or ""
    except Exception:
        name = role = "?"
    count += 1
    print(f"[EVENT] {e.type}  value={e.detail1}  source=[{role}] '{name}'", flush=True)

def main():
    Atspi.init()
    listener = Atspi.EventListener.new(on_event)
    listener.register("object:state-changed")
    print(f"# listening for object:state-changed for {SECONDS}s ...", flush=True)
    loop = GLib.MainLoop()
    GLib.timeout_add_seconds(SECONDS, loop.quit)
    loop.run()
    listener.deregister("object:state-changed")
    print(f"# done. {count} event(s) received.", flush=True)
    Atspi.exit()

if __name__ == "__main__":
    main()
