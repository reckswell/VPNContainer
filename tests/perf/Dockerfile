# Remote-display benchmark lab: one image for both ends.
#   gthost: stands in for the GT server (sshd, Xvfb, Xvnc, the workloads)
#   client: stands in for the gtvpn container + your desktop's X server
FROM ubuntu:24.04
ARG DEBIAN_FRONTEND=noninteractive
RUN set -eu; printf '%s\n' 'Acquire::Retries "5";' > /etc/apt/apt.conf.d/80retry; \
    apt-get update; apt-get install -y --no-install-recommends \
      openssh-server openssh-client xvfb xauth x11-xkb-utils xkb-data x11-utils x11-apps xterm \
      python3-pyqt5 tigervnc-standalone-server tigervnc-viewer tigervnc-tools \
      iproute2 iputils-ping procps gcc libc6-dev libx11-dev libxtst-dev libxdamage-dev libxfixes-dev \
      fonts-dejavu-core dbus-x11 netcat-openbsd ca-certificates time; \
    rm -rf /var/lib/apt/lists/*; mkdir -p /run/sshd; \
    useradd -m -s /bin/bash gtuser; useradd -m -s /bin/bash dev; \
    sed -i 's/^#\?X11Forwarding.*/X11Forwarding yes/; s/^#\?X11UseLocalhost.*/X11UseLocalhost yes/' /etc/ssh/sshd_config; \
    echo 'AcceptEnv XPRA_*' >> /etc/ssh/sshd_config
COPY qtcanvas.py xcanvas.c x11lat.c /opt/perf/
RUN set -eu; cd /opt/perf; cc -O2 -o xcanvas xcanvas.c -lX11 -lm; cc -O2 -o x11lat x11lat.c -lX11 -lXtst -lXdamage -lXfixes -lm; \
    chmod 755 qtcanvas.py; ln -s /opt/perf/xcanvas /opt/perf/x11lat /opt/perf/qtcanvas.py /usr/local/bin/
