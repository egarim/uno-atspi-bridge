#!/usr/bin/env bash
# The Linux demo: start the AT-SPI registry, launch the Uno app (bridge exposes the
# tree AND the Action write-path), then run an AT-SPI *agent* that finds a control by
# name and clicks it through the Action interface — while a listener captures the
# state-changed event the click causes. No mouse, no pixels: perception + action both
# ride the accessibility bus.
set -e
APP_DLL="${1:?usage: run_agent.sh <app.dll>}"

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
  echo ">> app pid $APP; letting it render + embed ..."
  sleep 6

  unset LD_PRELOAD
  echo ">> listener armed (captures what the agent causes) ..."
  python3 /harness/atspi_listen.py 12 >/tmp/listen.log 2>&1 &
  LIS=$!
  sleep 1

  echo ">> AGENT RUN"
  echo "----------------------------------------------------------------"
  RC=0; python3 /harness/atspi_agent.py unodemo "check box" notification || RC=$?
  echo "----------------------------------------------------------------"
  sleep 1

  echo ">> events the listener caught (this is what Orca would hear):"
  cat /tmp/listen.log 2>/dev/null | sed "s/^/   /"
  echo ">> bridge log (DoAction → real control):"
  grep "\[atspi\]" /tmp/app.log | tail -15 | sed "s/^/   /"
  kill $APP $LIS 2>/dev/null || true
  exit $RC
'
