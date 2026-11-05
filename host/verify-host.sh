#!/usr/bin/env bash
# verify-host.sh: checks from the HOST side that the GT VPN is confined to
# the gtvpn container: the host has no VPN interface/route/DNS, cannot reach
# GT-internal hosts, does not share the container's egress IP, and the
# container is configured the way the isolation model requires.
#
# Run via `./gtvpn verify` (container + host) or directly. --quiet prints
# only failures. Exit status 1 if anything failed.
set -uo pipefail
cd "$(dirname "$0")/.." || exit 1
NAME=gtvpn
ENGINE=${GTVPN_ENGINE:-docker}
QUIET=0; [[ ${1:-} == --quiet ]] && QUIET=1
FAILS=0; PASSES=0

emit() {
  [[ $1 == FAIL ]] && FAILS=$((FAILS+1)); [[ $1 == PASS ]] && PASSES=$((PASSES+1))
  if [[ $QUIET == 0 || $1 == FAIL ]]; then
    case $1 in PASS) c=32;; FAIL) c=31;; *) c=33;; esac
    printf '\033[%sm%-4s %-4s %s\033[0m\n' "$c" "$1" "$2" "$3"
  fi
}
envget() { local v=${!1:-}; [[ -n $v ]] && { printf '%s' "$v"; return; }; local l; l=$(grep -E "^$1=" .env 2>/dev/null | tail -1 || true); l=${l#*=}; l=${l%\"}; l=${l#\"}; printf '%s' "$l"; }
cx() { "$ENGINE" exec -u root "$NAME" "$@"; }
is_wsl() { grep -qi microsoft /proc/sys/kernel/osrelease 2>/dev/null; }
tcp_open() { timeout 4 bash -c "exec 3<>/dev/tcp/$1/$2" 2>/dev/null; }
host_route_dev() { ip -4 route get "$1" 2>/dev/null | awk '{for(i=1;i<=NF;i++) if($i=="dev"){print $(i+1); exit}}'; }

[[ $QUIET == 0 ]] && printf '\033[1m== gtvpn host-side verification %s ==\033[0m\n' "$(date -u +%FT%TZ)"
command -v ip >/dev/null || { emit FAIL H0 "iproute2 ('ip') not installed on the host; cannot verify routing"; exit 1; }

# ---- container configuration ------------------------------------------------
if [[ $("$ENGINE" inspect -f '{{.State.Running}}' "$NAME" 2>/dev/null) != true ]]; then
  emit FAIL H1 "container '$NAME' is not running"; exit 1
fi
nm=$("$ENGINE" inspect -f '{{.HostConfig.NetworkMode}}' "$NAME")
if [[ $nm == host ]]; then emit FAIL H1 "container shares the HOST network namespace (VPN would capture host traffic)"
else emit PASS H1 "container has its own network namespace ($nm)"; fi
[[ $("$ENGINE" inspect -f '{{.HostConfig.Privileged}}' "$NAME") == false ]] \
  && emit PASS H2 "container is not privileged" || emit FAIL H2 "container is privileged"
caps=$("$ENGINE" inspect -f '{{join .HostConfig.CapAdd " "}}' "$NAME" | tr 'a-z' 'A-Z' | sed 's/CAP_//g')
extra=""
for c in $caps; do
  case $c in NET_ADMIN|NET_RAW|SETUID|SETGID|CHOWN|DAC_OVERRIDE|FOWNER|KILL|SYS_CHROOT|AUDIT_WRITE) ;; *) extra+="$c ";; esac
done
[[ -z $extra ]] && emit PASS H3 "capabilities limited to the expected set" || emit FAIL H3 "unexpected capabilities: $extra"
"$ENGINE" inspect -f '{{json .HostConfig.SecurityOpt}}' "$NAME" | grep -q no-new-privileges \
  && emit PASS H4 "no-new-privileges is set" || emit FAIL H4 "no-new-privileges is not set"
bad_ports=$("$ENGINE" inspect -f '{{range $p, $b := .NetworkSettings.Ports}}{{range $b}}{{.HostIp}} {{end}}{{end}}' "$NAME" | tr ' ' '\n' | grep -v -e '^127\.0\.0\.1$' -e '^$' || true)
[[ -z $bad_ports ]] && emit PASS H5 "published ports are bound to 127.0.0.1 only" || emit FAIL H5 "ports published on: $bad_ports"

# sshd must not be reachable from the LAN side of this machine
lan_ip=$(ip -4 route get 1.1.1.1 2>/dev/null | awk '{for(i=1;i<=NF;i++) if($i=="src"){print $(i+1); exit}}')
port=$(envget GTVPN_SSH_PORT); port=${port:-2222}
if [[ -n $lan_ip ]]; then
  if tcp_open "$lan_ip" "$port"; then emit FAIL H6 "container sshd reachable on LAN address $lan_ip:$port"
  else emit PASS H6 "container sshd is not reachable on LAN address $lan_ip:$port"; fi
fi

# ---- tunnel confinement ---------------------------------------------------------
tun_ip=$(cx sh -c "ip -4 -o addr show tun0 2>/dev/null | awk '{print \$4}' | cut -d/ -f1")
if [[ -z $tun_ip ]]; then
  emit INFO H7 "tunnel is not up in the container; tunnel-confinement checks skipped"
else
  if ip -4 -o addr | awk '{print $4}' | cut -d/ -f1 | grep -qx "$tun_ip"; then
    emit FAIL H7 "the host has the VPN address $tun_ip on one of its interfaces"
  else emit PASS H7 "VPN address $tun_ip exists only inside the container"; fi

  # Every route the VPN pushed into the container must be absent on the host.
  leaked=""
  while read -r net; do
    [[ -z $net || $net == default ]] && continue
    probe=${net%/*}
    d=$(host_route_dev "$probe")
    [[ $d =~ ^(tun|gpd|vpn|ppp|utun) ]] && leaked+="$net->$d "
  done < <(cx sh -c "ip -4 route show dev tun0 | awk '{print \$1}'")
  [[ -z $leaked ]] && emit PASS H8 "no VPN-pushed route is present on the host" || emit FAIL H8 "host routes through a VPN interface: $leaked"

  # The container's VPN DNS servers must not be the host's resolvers.
  vdns=$(cx sh -c "awk '/^nameserver/{print \$2}' /etc/resolv.conf")
  hdns=$( { awk '/^nameserver/{print $2}' /etc/resolv.conf; resolvectl dns 2>/dev/null | tr ' ' '\n'; } 2>/dev/null | sort -u)
  bad=""
  for d in $vdns; do grep -qx "$d" <<<"$hdns" && bad+="$d "; done
  [[ -z $bad ]] && emit PASS H9 "host DNS does not use the VPN's DNS servers" || emit FAIL H9 "host DNS uses VPN DNS: $bad"
  for d in $vdns; do
    dd=$(host_route_dev "$d")
    [[ $dd =~ ^(tun|gpd|vpn|ppp) ]] && emit FAIL H10 "host would reach VPN DNS $d via $dd"
  done
fi

# GT-internal probe targets: routed via the host's normal network, and not
# reachable (they only answer through the VPN).
for t in $(envget GTVPN_PROBE_TARGETS); do
  h=${t%:*}; p=${t##*:}
  ip=$(getent ahostsv4 "$h" 2>/dev/null | awk 'NR==1{print $1}')
  [[ -z $ip ]] && { emit PASS H11 "host cannot even resolve $h (GT-internal name)"; continue; }
  d=$(host_route_dev "$ip")
  if [[ $d =~ ^(tun|gpd|vpn|ppp) ]]; then emit FAIL H11 "host routes $h ($ip) via VPN interface $d"; continue; fi
  if tcp_open "$ip" "$p"; then
    emit INFO H11 "host reaches $h:$p directly via $d (public service, or you are on campus); this traffic is NOT using the VPN"
  else
    emit PASS H11 "host cannot reach GT-internal $h:$p (only the container can)"
  fi
done

# ---- no VPN client on the host ---------------------------------------------------
# (processes of the gtvpn container are visible here too; exclude them)
cpids=$("$ENGINE" top "$NAME" -eo pid 2>/dev/null | awk 'NR>1{print $1}')
hostvpn=""
while read -r pid comm; do
  [[ -z $pid ]] && continue
  grep -qx "$pid" <<<"$cpids" || hostvpn+="$comm($pid) "
done < <(pgrep -x -l 'PanGPS|PanGPA|gpclient|gpservice|openconnect' 2>/dev/null)
if [[ -n $hostvpn ]]; then emit FAIL H12 "a VPN client is running on the host itself: $hostvpn"
else emit PASS H12 "no GlobalProtect/openconnect client is running on the host (outside the container)"; fi
if ip -o link 2>/dev/null | awk -F': ' '{print $2}' | grep -qE '^(gpd[0-9]|tun[0-9])'; then
  emit INFO H13 "host has a tun/gpd interface ($(ip -o link | awk -F': ' '{print $2}' | grep -E '^(gpd|tun)' | tr '\n' ' ')); make sure it is not a GT VPN"
fi
if is_wsl; then
  if powershell.exe -NoProfile -Command "Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object { \$_.InterfaceDescription -like '*PANGP*' -and \$_.Status -eq 'Up' } | Select-Object -First 1 -ExpandProperty Name" 2>/dev/null | tr -d '\r' | grep -q .; then
    emit FAIL H14 "the Windows GlobalProtect adapter (PANGP) is UP: Windows traffic is going to GT"
  else emit PASS H14 "Windows GlobalProtect adapter is not up"; fi
fi

# ---- egress identity -----------------------------------------------------------
url=$(envget GTVPN_EGRESS_URL)
if [[ -n $url ]] && [[ -s runtime/out/egress_ip ]]; then
  hip=$(curl -fsS -m 10 "$url" 2>/dev/null | tr -dc '0-9a-fA-F.:' | head -c 64)
  cip=$(cat runtime/out/egress_ip)
  if [[ -n $hip && $hip == "$cip" ]]; then emit FAIL H15 "host and container share egress IP $hip (host traffic may be using the VPN)"
  elif [[ -n $hip ]]; then emit PASS H15 "host egress $hip differs from container egress $cip"; fi
fi

[[ $QUIET == 0 ]] && printf '\033[1m== host: %d passed, %d failed ==\033[0m\n' "$PASSES" "$FAILS"
exit $(( FAILS > 0 ))
