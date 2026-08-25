#!/usr/bin/env bash
# Headless screen-recording of the self-playing demo. Boots Xvfb + the AT-SPI stack,
# launches the app in AUTOPLAY mode (it drives itself), and captures the window region
# with ffmpeg x11grab → /out/demo-raw.mp4.
set -e
APP_DLL="${1:?usage: record.sh <app.dll>}"
OUT="${OUT:-/out}"; mkdir -p "$OUT"
SECS="${SECS:-15}"

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

  UNODEMO_AUTOPLAY=1 UNODEMO_NO_AUTODEMO=1 DISPLAY=:99 dotnet "'"$APP_DLL"'" >/tmp/app.log 2>&1 &
  APP=$!
  echo ">> app pid $APP; letting it render ..."
  sleep 4

  echo ">> recording '"$SECS"'s of the app window (1024x768 @ 0,0) ..."
  unset LD_PRELOAD
  ffmpeg -y -loglevel error -f x11grab -framerate 30 -video_size 1024x768 -i :99.0+0,0 \
         -t "'"$SECS"'" -pix_fmt yuv420p -c:v libx264 -crf 18 -preset veryfast \
         "'"$OUT"'/demo-raw.mp4"
  echo ">> wrote demo-raw.mp4"
  kill $APP 2>/dev/null || true
'
