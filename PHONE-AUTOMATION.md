# Phone Automation — driving the Pixel for demos & social posting

How we drive the Pixel from a Claude session: **an agent that perceives via the
accessibility tree and acts by element** — the same thesis as this repo's bridge,
turned on a real phone. Two paths, preferred first.

## The rig
- **Pixel 7** (`27131FDH2002Q9`) — USB → the **Surface** (`surfacejoche`, mesh
  `100.64.0.3`, Windows **ARM64**, user `joche`). adb at `%USERPROFILE%\platform-tools`.
- Reach the Surface over the mesh: `ssh joche@100.64.0.3` (key auth). The Mac drives
  the Surface; the Surface's adb drives the Pixel.
- **DeepSeek** API key lives in the Machine-scope env var `deep_seek_api` — **never print it**.

---

## Preferred: ghost (android-agent) + DeepSeek, via MCP

[`ghost-in-the-droid/android-agent`](https://github.com/ghost-in-the-droid/android-agent) —
"drive a real phone with AI agents over ADB, 62 MCP tools." Runs on the Surface (where the
Pixel is); exposed to any Claude session as the `android-ghost` MCP server over SSH-stdio.

### One-time setup (already done on the Surface)
```powershell
# uv (ARM64-aware), then preflight
irm https://astral.sh/uv/install.ps1 | iex
uv tool run --from ghost-in-the-droid ghost doctor      # needs adb on PATH + a device

# point ghost at DeepSeek (OpenAI-compatible → the vllm backend)
ghost setup --backend vllm --model deepseek-chat --mode fast
# persisted (User scope), copied from deep_seek_api — NEVER printed:
#   OPENAI_API_KEY / VLLM_API_KEY = <deepseek>
#   OPENAI_BASE_URL / VLLM_BASE_URL = https://api.deepseek.com
```
MCP wiring (from the Mac): `automation/ghost-mcp.ps1` sits on the Surface and sets the
DeepSeek env + PATH + pre-starts adb, then execs the stdio MCP server. Register it:
```bash
claude mcp add android-ghost -s user -- \
  ssh -o BatchMode=yes -o LogLevel=QUIET -o ConnectTimeout=20 joche@100.64.0.3 \
  powershell -NoProfile -ExecutionPolicy Bypass -File C:\Users\joche\ghost-mcp.ps1
claude mcp get android-ghost   # → Status: ✔ Connected
```

### Using it (from a Claude session)
MCP tools appear as `mcp__android-ghost__*` (they load at **session start**).
- **Perceive (do this, not screenshots):** `get_screen_tree` (indented a11y hierarchy with
  `[idx] Class "label" [clickable] [x1,y1][x2,y2]`), `find_on_screen`, `extract_visible_text`.
- **Act:** `tap x y` / `tap_element idx` / `type_text` / `swipe` / `launch_app` /
  `press_back` / `press_home`.
- **Hands-off:** `ghost "<task>" --mode fast` runs ghost's own DeepSeek agent loop.

### Gotchas (learned the hard way)
- **Read the tree, never the raw screenshot** — the screenshot base64 overflows the MCP
  token cap and the client falls back to OCR. `get_screen_tree` is cheap, precise, on-theme.
- MCP servers load at **session start** — register once, then start a fresh session to call them.
- Windows console is cp1252 → the tool's `✓` output crashes; set `PYTHONUTF8=1`.
- Pre-start the adb daemon (`adb start-server`) so its stderr chatter never pollutes MCP stdio.
- Non-interactive SSH may not load User-scope env → `ghost-mcp.ps1` reads `deep_seek_api`
  from Machine scope and sets the env in-process every launch.

---

## Fallback: `pctl.sh` (hand-rolled adb over SSH)

When MCP isn't loaded (e.g. mid-session), `automation/pctl.sh` drives the phone directly:
```
pctl.sh {shot | tap x y | swipe x1 y1 x2 y2 [ms] | key N | text STR | clear | open PKG | back | home | raw ...}
```
`shot` pulls a screenshot to view; `text` uses **ADBKeyboard** (base64 broadcast) for
emoji/newlines. Slower and coordinate-based — prefer ghost.

---

## Platform posting gotchas (apply to both paths)
- **X + TikTok are region-blocked** on the RU connection → connect **AmneziaVPN**
  (`org.amnezia.vpn`). **X** = IP-throttle only (VPN fixes it). **TikTok** = *account/device*
  region ban — **the VPN does NOT fix it**; every Post just saves a draft.
- **Instagram** composer hard-caps **linked hashtags at 5** (strips `#` past that) — use the 5 best.
- **YouTube** Shorts editor **filters synthetic taps** → upload via the **Video tab**
  (a vertical ≤3-min clip still publishes as a Short). Switch to the **Joche Ojeda** channel first.
- **X** free tier (@jmojeda) = **280 chars**; emoji count as 2.
- Package ids: TikTok `com.zhiliaoapp.musically` · Instagram `com.instagram.android` ·
  X `com.twitter.android` · YouTube `com.google.android.youtube`.
- **Standing rule: draft captions, post only on explicit approval.**

## See also
- Reels: HyperFrames projects in `reels/` (gitignored); VO via ElevenLabs
  (`gen_vo.py`, voice_id `otx1aI1BjNBQMbMX2aII`, key read via `plutil`, never printed).
- Surface kit: `~/blog-video-kit/HANDOFF.md`.
