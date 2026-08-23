# uno-atspi-bridge (PoC)

**Can we give Uno Platform an AT-SPI accessibility backend on Linux ourselves?**

Uno's Skia desktop head [does not expose an AT-SPI tree on Linux](https://github.com/egarim/atspi-dotnet-demo)
— a screen reader (or an [AT-SPI-first agent](https://jocheojeda.com/2026/08/22/at-spi-first-grounding/))
sees nothing. This repo is a focused proof-of-concept: build the missing bridge
that projects Uno's controls onto AT-SPI, and verify it in a headless container.

## Step 1 — is the semantic tree even there? ✅ YES

Before writing a D-Bus bridge, the question is whether Uno instantiates
`AutomationPeer`s at runtime on the Linux Skia head. If it does, the bridge just
has to *publish* them; if it doesn't, there's nothing to publish.

It does. Dumped from **inside the app** running under Xvfb in a Linux container
(full output in [`results/step1-uno-peer-tree.txt`](results/step1-uno-peer-tree.txt)):

```
[Button]   name='Open File Manager'    box=(20,52,148,33)   focusable=True
[Button]   name='Save Document'        box=(20,97,127,33)   focusable=True
[Edit]     name='Search box'           box=(20,142,217,33)  focusable=True
[CheckBox] name='Enable notifications' box=(20,187,157,32)  focusable=True
[Slider]   name='Volume'               box=(28,231,200,32)  focusable=True
[ComboBox] name='Theme selector'       box=(20,275,83,32)   focusable=True
```

A full visual-tree walk finds **30 peers** with roles, names, and bounding boxes.
Everything AT-SPI needs is present at runtime. The only missing piece is the
Linux AT-SPI **D-Bus backend** that projects this tree.

## Step 2 — the AT-SPI bridge ✅ WORKS

[`UnoApp/UnoDemo/Atspi/AtspiBridge.cs`](UnoApp/UnoDemo/Atspi/AtspiBridge.cs) — a
~350-line AT-SPI2 backend that, on Linux startup:
1. connects to the a11y bus (`org.a11y.Bus.GetAddress`),
2. performs the `Socket.Embed` handshake to attach the app root to the desktop,
3. exports each Uno peer as a D-Bus object implementing `org.a11y.atspi.Accessible`
   (role, name, states, children) + `Component` (extents), mapping
   `AutomationControlType`→AtspiRole, `GetName()`→name, `GetBoundingRectangle()`→box,
   `IsKeyboardFocusable`/`IsEnabled`→states.

Built on `Tmds.DBus.Protocol`.

**Result** — the same `harness/atspi_dump.py` client (like Orca) that saw *nothing*
before now reads the full tree (full output in
[`results/step2-uno-atspi-via-bridge.txt`](results/step2-uno-atspi-via-bridge.txt)):

```
# AT-SPI desktop has 1 application(s) registered
=== application: 'UnoDemo' ===
    [push button] 'Open File Manager'    box=(20,52,148,33)   focusable,enabled,showing,sensitive,visible
    [push button] 'Save Document'        box=(20,97,127,33)   ...
    [entry]       'Search box'           box=(20,142,217,33)
    [check box]   'Enable notifications' box=(20,187,157,32)
    [slider]      'Volume'               box=(28,231,200,32)
    [combo box]   'Theme selector'       box=(20,275,83,32)
```

Correct roles, names, exact boxes, live states — everything an AT-SPI-first agent
or a screen reader needs. **We gave Uno the Linux accessibility backend it was
missing, in application code, no fork required.**

## Scope / honesty

This is a PoC, not production. It implements the read path (Accessible +
Component + Application + `Socket.Embed`) — enough for grounding and tree
inspection. Not yet done: live event signals (focus/state/children-changed for a
screen reader tracking changes), the `Text`/`Value`/`Action` interfaces, filtering
of non-actionable internals (scrollbar repeat-buttons show up), and screen-space
coordinate offset (boxes are window-relative here). The right home for a complete
version is [Uno's own Skia a11y abstraction](https://platform.uno/docs/articles/features/working-with-accessibility.html)
(6.6 already routes peers to Win/mac/wasm backends — this shows the Linux one is
tractable).

## Mapping (peer → AT-SPI)

| Uno peer | AT-SPI role |
|---|---|
| Button | push button |
| Edit (TextBox) | entry |
| CheckBox | check box |
| Slider | slider |
| ComboBox | combo box |
| Text (TextBlock) | label |

## Layout

```
UnoApp/     the Uno desktop app (named controls) + the peer dumper (Step 1)
harness/    Dockerfile + run.sh + atspi_dump.py (headless AT-SPI test)
results/    captured output
```

## License

MIT.
