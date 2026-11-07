# gtvpn's viewer under mono, for `gtvpn vnc <host> windows` where the viewer
# cannot run as a Windows program: Smart App Control blocks a program built
# on the PC (it is not signed). It runs in its own container instead, in the
# gtvpn container's network (the session's forwards are its 127.0.0.1
# ports), and WSLg shows each of its windows as a window on the Windows
# desktop. On Linux it needs no mono on the computer.
ARG BASE_IMAGE=ubuntu:24.04
FROM ${BASE_IMAGE}

ARG DEBIAN_FRONTEND=noninteractive
# As in image/Dockerfile: some networks corrupt apt downloads.
RUN set -eu; printf '%s\n' 'Acquire::Retries "5";' 'Acquire::http::Pipeline-Depth "0";' \
      'Acquire::http::No-Cache "true";' 'Acquire::BrokenProxy "true";' > /etc/apt/apt.conf.d/80gtvpn-net; \
    for i in 1 2 3 4; do apt-get update && break; [ "$i" = 4 ] && exit 1; \
      echo "apt-get update failed (try $i), retrying"; rm -rf /var/lib/apt/lists/*; sleep 5; done; \
    apt-get install -y --no-install-recommends \
      mono-runtime mono-mcs libmono-system-windows-forms4.0-cil libmono-system-drawing4.0-cil libgdiplus; \
    rm -rf /var/lib/apt/lists/*

COPY *.cs /opt/gtvpn/src/
RUN mcs -langversion:5 -optimize+ -target:winexe -out:/opt/gtvpn/gtvpn-viewer.exe \
      -r:System.Windows.Forms.dll -r:System.Drawing.dll /opt/gtvpn/src/*.cs
