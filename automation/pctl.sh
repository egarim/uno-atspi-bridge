#!/usr/bin/env bash
# Pixel control over the mesh via the Surface (cmd shell; ignores adb stderr noise).
# On the Surface itself, drop the ssh hop: set H="" and call adb directly.
set -uo pipefail
H=joche@100.64.0.3
ADB='%USERPROFILE%\platform-tools\adb.exe'
SC="$(cd "$(dirname "$0")" && pwd)"
S(){ ssh -o BatchMode=yes -o ConnectTimeout=15 "$H" "$@"; }
case "${1:-}" in
  shot)   S "$ADB exec-out screencap -p > %USERPROFILE%\\shot.png" >/dev/null 2>&1
          scp -q -o BatchMode=yes "$H:shot.png" "$SC/shot.png" && echo "$SC/shot.png" ;;
  tap)    S "$ADB shell input tap $2 $3" >/dev/null 2>&1; echo "tap $2 $3" ;;
  swipe)  S "$ADB shell input swipe $2 $3 $4 $5 ${6:-300}" >/dev/null 2>&1; echo "swipe" ;;
  key)    S "$ADB shell input keyevent $2" >/dev/null 2>&1; echo "key $2" ;;
  text)   b=$(printf '%s' "$2" | base64 | tr -d '\n')
          S "$ADB shell am broadcast -a ADB_INPUT_B64 --es msg $b" >/dev/null 2>&1; echo "typed ${#2} chars" ;;
  clear)  S "$ADB shell am broadcast -a ADB_CLEAR_TEXT" >/dev/null 2>&1; echo cleared ;;
  open)   S "$ADB shell monkey -p $2 -c android.intent.category.LAUNCHER 1" >/dev/null 2>&1; echo "opened $2" ;;
  back)   S "$ADB shell input keyevent 4" >/dev/null 2>&1; echo back ;;
  home)   S "$ADB shell input keyevent 3" >/dev/null 2>&1; echo home ;;
  raw)    shift; S "$ADB $*" ;;
  *) echo "usage: pctl {shot|tap x y|swipe x1 y1 x2 y2 [ms]|key N|text STR|clear|open PKG|back|home|raw ...}" ;;
esac
