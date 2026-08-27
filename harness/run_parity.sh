#!/usr/bin/env bash
# Parity run: identical AT-SPI client (dump + all-mode agent proofs) against any
# app. Run once against UnoDemo (bridge) and once against AvaloniaDemo (the native
# reference backend the bridge is ported from), then diff the dumps.
set -e
APP_DLL="${1:?usage: run_parity.sh <app.dll> <app-match>}"
APP_MATCH="${2:?usage: run_parity.sh <app.dll> <app-match>}"

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

  UNODEMO_NO_AUTODEMO=1 DISPLAY=:99 dotnet "'"$APP_DLL"'" >/tmp/app.log 2>&1 &
  APP=$!
  sleep 8
  unset LD_PRELOAD

  echo "== PARITY DUMP ('"$APP_MATCH"') =="
  python3 /harness/parity_dump.py "'"$APP_MATCH"'"
  echo
  echo "== AGENT PROOFS (toggle / value / text / combo) =="
  RC=0
  python3 /harness/atspi_agent.py "'"$APP_MATCH"'" "check box" notification || RC=$?
  python3 /harness/atspi_agent.py "'"$APP_MATCH"'" all || RC=$?
  kill $APP 2>/dev/null || true
  exit $RC
'
