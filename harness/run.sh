#!/usr/bin/env bash
# Headless AT-SPI test: start a virtual display + session bus + the AT-SPI a11y
# registry, launch the .NET app, then dump its accessibility tree with a client.
set -e
APP_DLL="${1:?usage: run.sh <path-to-app.dll> [app-name-substring]}"
APP_MATCH="${2:-}"

export DISPLAY=:99
Xvfb :99 -screen 0 1280x1024x24 >/tmp/xvfb.log 2>&1 &
sleep 2

# accessibility on for the whole session
export GTK_A11Y=1
export ACCESSIBILITY_ENABLED=1
export QT_ACCESSIBILITY=1
export NO_AT_BRIDGE=0

echo ">> launching AT-SPI registry + app inside a session bus ..."
dbus-run-session -- bash -c '
  set -e
  # start the AT-SPI accessibility bus + registry
  /usr/libexec/at-spi-bus-launcher --launch-immediately >/tmp/atspi-bus.log 2>&1 &
  sleep 1
  /usr/libexec/at-spi2-registryd >/tmp/atspi-reg.log 2>&1 &
  sleep 2

  # SkiaSharp (Uno) needs system libuuid/libfreetype preloaded on Linux;
  # harmless for apps that do not use them.
  UU=$(ls /lib/*/libuuid.so.1 2>/dev/null | head -1)
  FT=$(ls /lib/*/libfreetype.so.6 2>/dev/null | head -1)
  export LD_PRELOAD="$UU $FT"

  # launch the app under test (background), give it time to register
  DISPLAY=:99 dotnet "'"$APP_DLL"'" >/tmp/app.log 2>&1 &
  APP_PID=$!
  unset LD_PRELOAD   # do not preload into the python client
  echo ">> app pid $APP_PID, waiting for it to publish its a11y tree ..."
  sleep 10

  echo ">> dumping AT-SPI tree:"
  echo "----------------------------------------------------------------"
  python3 /harness/atspi_dump.py "'"$APP_MATCH"'"
  echo "----------------------------------------------------------------"
  kill $APP_PID 2>/dev/null || true
'
