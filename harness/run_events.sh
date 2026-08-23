#!/usr/bin/env bash
# Full demo: start the AT-SPI registry, launch the Uno app (which embeds its tree
# and then drives a focus + a checkbox toggle), capture a screenshot of the running
# app, and listen for the live AT-SPI events the bridge emits.
set -e
APP_DLL="${1:?usage: run_events.sh <app.dll>}"
OUT="${OUT:-/out}"
mkdir -p "$OUT"

export DISPLAY=:99
Xvfb :99 -screen 0 1280x1024x24 >/tmp/xvfb.log 2>&1 &
sleep 2
export LD_PRELOAD="$(ls /lib/*/libuuid.so.1 2>/dev/null | head -1) $(ls /lib/*/libfreetype.so.6 2>/dev/null | head -1)"

dbus-run-session -- bash -c '
  set -e
  /usr/libexec/at-spi-bus-launcher --launch-immediately >/tmp/atspi-bus.log 2>&1 &
  sleep 1
  /usr/libexec/at-spi2-registryd >/tmp/atspi-reg.log 2>&1 &
  sleep 2

  DISPLAY=:99 dotnet "'"$APP_DLL"'" >/tmp/app.log 2>&1 &
  APP=$!
  echo ">> app pid $APP; letting it render + embed ..."
  sleep 5

  echo ">> screenshot of the running app ..."
  DISPLAY=:99 scrot -o "'"$OUT"'/app-screenshot.png" 2>/dev/null && echo "   wrote app-screenshot.png" || echo "   scrot failed"

  echo ">> listening for live AT-SPI events (the bridge drives a focus + a toggle):"
  echo "----------------------------------------------------------------"
  unset LD_PRELOAD
  python3 /harness/atspi_listen.py 10
  echo "----------------------------------------------------------------"

  echo ">> bridge log:"; grep "\[atspi\]" /tmp/app.log | head -20
  kill $APP 2>/dev/null || true
'
