# Test-lab VPN server. Stands in for the GT GlobalProtect gateway so the
# kill-switch, routing and DNS handling can be exercised with a real tunnel.
# Xvfb, curl and bzip2 are for the xpra test (run-xpra.sh), like a GT server.
ARG BASE_IMAGE=ubuntu:24.04
FROM ${BASE_IMAGE}
RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
      ocserv gnutls-bin dnsmasq iproute2 netcat-openbsd ca-certificates \
      openssh-server xauth x11-utils tcsh \
      xvfb x11-xkb-utils xkb-data x11-apps curl bzip2 && rm -rf /var/lib/apt/lists/*
COPY start.sh /start.sh
RUN chmod +x /start.sh
ENTRYPOINT ["/start.sh"]
