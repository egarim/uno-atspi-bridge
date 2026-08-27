# Avalonia parity report

**Method:** differential testing. `AvaloniaDemo/` is a twin of UnoDemo's surface —
the same six controls with the same accessible names — built on Avalonia 12.1.1,
whose native `Avalonia.FreeDesktop.AtSpi` backend is the reference implementation
this bridge is ported from. The **identical AT-SPI client** (`harness/parity_dump.py`
+ `harness/atspi_agent.py`) runs against both apps in the same Docker harness:

```bash
docker build -f harness/Dockerfile --build-arg APP_DIR=UnoApp \
  --build-arg APP_PROJ=app/UnoDemo/UnoDemo.csproj --build-arg APP_TFM=net10.0-desktop \
  -t uno-atspi-poc .
docker build -f harness/Dockerfile --build-arg APP_DIR=AvaloniaDemo \
  --build-arg APP_PROJ=app/AvaloniaDemo.csproj -t avalonia-atspi-poc .

docker run --rm -v "$PWD/harness:/harness" --entrypoint bash uno-atspi-poc \
  /harness/run_parity.sh /app/UnoDemo.dll unodemo
docker run --rm -v "$PWD/harness:/harness" --entrypoint bash avalonia-atspi-poc \
  /harness/run_parity.sh /app/AvaloniaDemo.dll avalonia
```

## Behavioral parity — the same client, 4/4 proofs on both

| proof | UnoDemo (this bridge) | AvaloniaDemo (native) |
|---|---|---|
| Action: toggle checkbox, CHECKED flips | ✅ | ✅ |
| Value: slider 50→80, read back | ✅ | ✅ |
| EditableText: write text, read back via Text | ✅ | ✅ |
| Selection: expand combo, enumerate, select Dark, verify | ✅ | ✅ |

Full logs: [parity-uno.txt](parity-uno.txt), [parity-avalonia.txt](parity-avalonia.txt).

## Static surface — role / interfaces / actions / states per control

Identical rows for both apps: push buttons (`Action`/`click`), entry
(`EditableText`+`Text`, `EDITABLE`), checkbox (`Action`/`toggle`), slider (`Value`),
combo box (`Action`+`Selection`+`Text`, `expand or collapse`, `EXPANDABLE`).

**One intentional difference:** dropdown items. Avalonia realizes them inside a
separate top-level `PopupRoot` frame only while the popup is open; this bridge
parents them under the combo box and keeps them on the bus permanently (the
static-tree PoC has no popup tracking). Both shapes are AT-SPI-legal, and the
generic client flow — expand, then search the app for `list item` nodes — works on
both. The bridge's shape is strictly easier for an agent (items discoverable before
expanding).

## Notes for future drift-checking

- Action names are aligned to the oracle on purpose: `click`, `toggle`,
  `expand or collapse`, `select`. `UnoDemo.Tests` locks the role map; re-running
  the two parity commands above is the drift check for everything else.
- Avalonia's Linux AT-SPI backend shipped in 12.0 (`Avalonia.FreeDesktop.AtSpi`,
  AvaloniaUI/Avalonia#20735); 11.x still has the canvas gap.
