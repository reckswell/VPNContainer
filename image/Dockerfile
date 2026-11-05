# syntax=docker/dockerfile:1
#
# gtvpn: an isolated GlobalProtect (Georgia Tech) VPN jump box.
#
# The VPN client is the open-source GlobalProtect-openconnect project
# (gpclient + gpauth, https://github.com/yuezk/GlobalProtect-openconnect).
# Its official image ships musl (Alpine) binaries; we lift just the two
# binaries and the shared libraries they need onto an Ubuntu 24.04 base so
# the rest of the container is glibc (needed by the VS Code server).

ARG GP_IMAGE=yuezk/globalprotect-openconnect:3.2.0@sha256:506577754b9152c4581fe6a2a6b4c020cc1d648f5ed1c4590e9a137e94f93bb0
ARG BASE_IMAGE=ubuntu:24.04

# ---------------------------------------------------------------------------
FROM ${GP_IMAGE} AS gp
# Collect gpclient/gpauth, the musl loader, and exactly the libraries they link.
RUN set -eu; mkdir -p /out/bin /out/lib /out/libexec; \
    cp /usr/bin/gpclient /usr/bin/gpauth /out/bin/; \
    cp /usr/libexec/gpclient/vpnc-script /out/libexec/vpnc-script; \
    cp /lib/ld-musl-*.so.1 /out/; \
    for b in /usr/bin/gpclient /usr/bin/gpauth; do \
      ldd "$b" | awk '$3 ~ /^\// {print $3}'; \
    done | sort -u | while read -r lib; do cp -L "$lib" /out/lib/; done; \
    ls -la /out /out/lib

# ---------------------------------------------------------------------------
FROM ${BASE_IMAGE}

ARG DEBIAN_FRONTEND=noninteractive
# Some networks (proxies, antivirus web filters) corrupt apt downloads
# ("Hash Sum mismatch"): no pipelining, no proxy caches, retry, and redo a
# failed index update from scratch a few times.
RUN set -eu; printf '%s\n' 'Acquire::Retries "5";' 'Acquire::http::Pipeline-Depth "0";' \
      'Acquire::http::No-Cache "true";' 'Acquire::BrokenProxy "true";' > /etc/apt/apt.conf.d/80gtvpn-net; \
    for i in 1 2 3 4; do apt-get update && break; [ "$i" = 4 ] && exit 1; \
      echo "apt-get update failed (try $i), retrying"; rm -rf /var/lib/apt/lists/*; sleep 5; done; \
    apt-get install -y --no-install-recommends \
      ca-certificates iproute2 iptables \
      openssh-server openssh-client \
      xauth x11-apps x11-utils \
      curl dnsutils netcat-openbsd iputils-ping tinyproxy \
      openconnect \
      bash-completion less nano git rsync tmux procps; \
    rm -rf /var/lib/apt/lists/* /etc/ssh/ssh_host_*; \
    # Ubuntu 24.04 defaults to the nft backend; pin it explicitly.
    update-alternatives --set iptables /usr/sbin/iptables-nft; \
    update-alternatives --set ip6tables /usr/sbin/ip6tables-nft

# musl runtime for the GlobalProtect binaries (does not touch glibc).
COPY --from=gp /out/ld-musl-*.so.1 /lib/
COPY --from=gp /out/lib/ /opt/gp/lib/
COPY --from=gp /out/bin/ /usr/bin/
COPY --from=gp /out/libexec/vpnc-script /usr/libexec/gpclient/vpnc-script
RUN set -eu; for l in /lib/ld-musl-*.so.1; do \
      a=$(basename "$l" .so.1); echo /opt/gp/lib > "/etc/${a}.path"; done; \
    gpclient --version; gpauth --version

# Users/groups:
#   gpvpn (gid 4501): the ONLY group whose sockets may leave via eth0
#                     (VPN control plane). gpclient/gpauth run as root:gpvpn.
#   gtegress (4502):  the allow-listed direct-egress proxy (Claude Code etc.)
#   dev:              the interactive user (ssh, VS Code, your tools). Its
#                     traffic may only use the tunnel. UID is remapped to
#                     your host UID at start so X11 auth files are readable.
RUN set -eu; groupadd -g 4501 gpvpn; groupadd -g 4502 gtegress; \
    userdel -r ubuntu 2>/dev/null || true; \
    useradd -m -u 1000 -s /bin/bash dev; \
    # No password, but not "locked" (sshd refuses locked accounts).
    usermod -p "*" dev; \
    mkdir -p /run/sshd /var/lib/gtvpn /var/log/gtvpn /run/gtvpn

# ---------------------------------------------------------------------------
# Claude Code (optional, ~340 MB on disk, nothing when idle). The native
# binary is taken from its official npm platform package and checked against
# the registry's sha512 integrity hash. Set INSTALL_CLAUDE_CODE=0 to skip.
ARG INSTALL_CLAUDE_CODE=1
ARG CLAUDE_CODE_VERSION=latest
RUN set -eu; if [ "$INSTALL_CLAUDE_CODE" = 1 ]; then \
      case "$(dpkg --print-architecture)" in amd64) a=x64;; arm64) a=arm64;; *) echo "unsupported arch"; exit 1;; esac; \
      pkg="@anthropic-ai/claude-code-linux-$a"; \
      meta=$(curl -fsSL "https://registry.npmjs.org/$pkg/$CLAUDE_CODE_VERSION"); \
      url=$(printf '%s' "$meta" | grep -o '"tarball":"[^"]*"' | head -1 | cut -d'"' -f4); \
      sri=$(printf '%s' "$meta" | grep -o '"integrity":"sha512-[^"]*"' | head -1 | cut -d'"' -f4); \
      curl -fsSL -o /tmp/cc.tgz "$url"; \
      echo "${sri#sha512-}" | base64 -d | od -An -tx1 | tr -d ' \n' > /tmp/cc.want; \
      sha512sum /tmp/cc.tgz | cut -d' ' -f1 | tr -d '\n' > /tmp/cc.have; \
      cmp -s /tmp/cc.want /tmp/cc.have || { echo "claude-code integrity check FAILED"; exit 1; }; \
      tar -xzf /tmp/cc.tgz -C /tmp package/claude; install -m 0755 /tmp/package/claude /usr/local/bin/claude; \
      rm -rf /tmp/cc.* /tmp/package; /usr/local/bin/claude --version; \
    fi

# Our scripts run as root, so they and every directory above them must be
# root-owned and not writable by dev. A checkout on a Windows drive (WSL
# /mnt/c) shows every file as 0777, and a plain `COPY rootfs/ /` would carry
# that onto /etc, /usr and /usr/local/sbin. So stage, normalise, then unpack
# without touching the existing directories (--no-overwrite-dir).
COPY rootfs/ /tmp/rootfs/
RUN set -eu; cd /tmp/rootfs; \
    chown -R root:root .; \
    find . -type d -exec chmod 0755 {} +; \
    find . -type f -exec chmod 0644 {} +; \
    chmod 0755 usr/local/sbin/* usr/local/bin/*; \
    tar -cf - . | tar -C / --no-overwrite-dir -xf -; \
    cd /; rm -rf /tmp/rootfs; \
    /usr/local/sbin/gtvpn-permcheck || { echo "unsafe permissions (see above)"; exit 1; }

# VS Code Dev Containers reads this when it attaches: run as dev, never root
# (root holds NET_ADMIN, i.e. the kill-switch). gtvpn setup also installs the
# same choice as an attach config (vscode/attached-container-config.json).
LABEL devcontainer.metadata='[{"remoteUser":"dev"}]'

# Nothing listens except sshd (key-only) on 2222, published to 127.0.0.1.
EXPOSE 2222
STOPSIGNAL SIGTERM
HEALTHCHECK --interval=60s --timeout=20s --start-period=20s \
  CMD /usr/local/sbin/gtvpn-firewall check >/dev/null || exit 1
ENTRYPOINT ["/usr/local/sbin/gtvpn-entrypoint"]
