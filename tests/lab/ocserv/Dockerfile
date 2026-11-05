# Test-lab VPN server. Stands in for the GT GlobalProtect gateway so the
# kill-switch, routing and DNS handling can be exercised with a real tunnel.
ARG BASE_IMAGE=ubuntu:24.04
FROM ${BASE_IMAGE}
RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
      ocserv gnutls-bin dnsmasq iproute2 netcat-openbsd ca-certificates \
      openssh-server xauth x11-utils && rm -rf /var/lib/apt/lists/*
COPY start.sh /start.sh
RUN chmod +x /start.sh
ENTRYPOINT ["/start.sh"]
