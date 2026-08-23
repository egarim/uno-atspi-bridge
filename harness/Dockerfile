# Headless AT-SPI test harness for .NET desktop apps (Avalonia / Uno).
# Builds the app framework-dependent (matches the SDK's arch), then runs it under
# Xvfb + a session D-Bus + the AT-SPI registry, and dumps the accessibility tree.
FROM mcr.microsoft.com/dotnet/sdk:10.0

RUN apt-get update && apt-get install -y --no-install-recommends \
      xvfb dbus dbus-x11 \
      at-spi2-core \
      python3 python3-gi gir1.2-atspi-2.0 \
      libx11-6 libice6 libsm6 libfontconfig1 libglib2.0-0 libgtk-3-0 \
      libgl1 libegl1 libx11-xcb1 libharfbuzz0b libuuid1 libfreetype6 \
      fonts-dejavu-core \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /src
# APP_DIR = dir copied into the build context; APP_PROJ = the .csproj to publish
ARG APP_DIR=AvaloniaDemo
ARG APP_PROJ=app/AvaloniaDemo.csproj
ARG APP_TFM=
COPY ${APP_DIR}/ /src/app/
RUN dotnet publish "/src/${APP_PROJ}" -c Release -o /app ${APP_TFM:+-f ${APP_TFM}}

COPY harness/atspi_dump.py /harness/atspi_dump.py
COPY harness/run.sh /harness/run.sh
RUN chmod +x /harness/run.sh

# APP_DLL and APP_MATCH are provided at run time
ENTRYPOINT ["/harness/run.sh"]
