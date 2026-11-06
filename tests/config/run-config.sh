#!/usr/bin/env bash
# Checks vpncontainer.toml handling without Docker or a VPN: the committed file
# loads, listed hosts and patterns go through the container, everything else
# connects directly, mistakes are refused and change nothing.
# Works on a temporary copy of the repo with a temporary HOME, so your own
# vpncontainer.toml, .env and ~/.ssh are not touched. Needs python3 and ssh.
#   tests/config/run-config.sh
set -uo pipefail
SRC=$(cd "$(dirname "$0")/../.." && pwd)
command -v ssh >/dev/null || { echo "needs an ssh client (sudo apt install openssh-client)"; exit 1; }
T=$(mktemp -d); trap 'rm -rf "$T"' EXIT
umask 077
R=$T/repo; mkdir -p "$R/host" "$T/home/.ssh"
cp "$SRC/vpncontainer" "$SRC/vpncontainer.toml" "$SRC/.env.example" "$SRC/compose.yaml" "$R/"
cp "$SRC/host/config.py" "$R/host/"
cp "$R/.env.example" "$R/.env"
export HOME=$T/home VPNCONTAINER_ENGINE=vpncontainer-test-no-engine
G=$HOME/.ssh/vpncontainer/config
printf 'Include %s\n' "$G" > "$HOME/.ssh/config"
cd "$R" || exit 1

FAILED=0; ok(){ printf '\033[32mPASS\033[0m %s\n' "$*"; }; bad(){ printf '\033[31mFAIL\033[0m %s\n' "$*"; FAILED=1; }
check(){ local d=$1; shift; if "$@" >/dev/null 2>&1; then ok "$d"; else bad "$d"; fi; }
# ssh -G as this computer's ssh / VS Code sees it (capture first: grep -q can SIGPIPE ssh)
sshG() { local o; o=$(ssh -G -F "$G" "$1" 2>/dev/null </dev/null); grep -qx "$2" <<<"$o"; }
jumps() { sshG "$1" 'proxyjump vpncontainer'; }
direct() { local o; o=$(ssh -G -F "$G" "$1" 2>/dev/null </dev/null) && ! grep -q '^proxyjump ' <<<"$o"; }
# ...and as ssh inside the container sees runtime/in/hosts.conf
inG() { local o; o=$(ssh -G -F runtime/in/hosts.conf "$1" 2>/dev/null </dev/null); grep -qx "$2" <<<"$o"; }
refused() { # description, toml text appended to the committed file, expected message
  cp "$SRC/vpncontainer.toml" vpncontainer.toml; printf '%s\n' "$2" >> vpncontainer.toml
  local before out; before=$(cat "$G")
  if out=$(./vpncontainer hosts 2>&1); then bad "$1 (accepted)"; return; fi
  if [[ $(cat "$G") != "$before" ]]; then bad "$1 (config changed)"; return; fi
  if grep -qF -- "$3" <<<"$out"; then ok "$1"; else bad "$1 (message: $out)"; fi
}

# ---- The committed file ---------------------------------------------------------
check "the committed vpncontainer.toml loads" python3 -I host/config.py records vpncontainer.toml
check "it names the VPN provider (globalprotect)" grep -qx 'env VPNCONTAINER_PROVIDER globalprotect' <(python3 -I host/config.py records vpncontainer.toml)
check "vpncontainer hosts accepts it" ./vpncontainer hosts
check "VS Code entry gt-linlab19 goes to ece-linlab19" sshG gt-linlab19 'hostname ece-linlab19.ece.gatech.edu'
check "gt-linlab19 goes through the container" jumps gt-linlab19
check "ece-linlab19 typed by its own name goes through the container" jumps ece-linlab19.ece.gatech.edu
check "another GT name (pattern *.gatech.edu) goes through the container" jumps login-ice.pace.gatech.edu
check "a GT address (pattern 143.215.*) goes through the container" jumps 143.215.155.65
for h in mydesktop.local my-home-pc 192.168.1.20 10.0.0.5 github.com example.com; do
  check "unlisted $h connects directly" direct "$h"
done
check "the container itself (vpncontainer) is not jumped through itself" direct vpncontainer
check "container: ssh linlab19 knows the server" inG linlab19 'hostname ece-linlab19.ece.gatech.edu'
export VPNCONTAINER_SSH_CONFIG=$HOME/.ssh/config  # ssh itself reads the real home's, not $HOME's
check "vpncontainer route: a listed host" bash -c "./vpncontainer route ece-linlab19.ece.gatech.edu | grep -v direct | grep -q 'through the container'"
check "vpncontainer route: an unlisted host" bash -c "./vpncontainer route mydesktop.local | grep -q 'direct, not through'"
check "settings are exported for docker compose (gateway-only sign-in)" \
  bash -c 'source ./vpncontainer; cfg_load; [[ $VPNCONTAINER_SKIP_PORTAL == 1 && $VPNCONTAINER_GATEWAY == dc-ext-gw.vpn.gatech.edu && $VPNCONTAINER_GATEWAY_NAME == "DC Gateway" ]]'
check "a setting vpncontainer.toml leaves out still comes from .env" \
  bash -c 'sed -i "s/^VPNCONTAINER_SSH_PORT=.*/VPNCONTAINER_SSH_PORT=2299/" .env; source ./vpncontainer; cfg_load; [[ $(envget VPNCONTAINER_SSH_PORT) == 2299 ]]'
sed -i 's/^VPNCONTAINER_SKIP_PORTAL=.*/VPNCONTAINER_SKIP_PORTAL=0/' .env; grep -q '^VPNCONTAINER_SKIP_PORTAL=' .env || echo VPNCONTAINER_SKIP_PORTAL=0 >> .env
check "vpncontainer.toml wins over .env, and says so" \
  bash -c 'source ./vpncontainer; cfg_load; [[ $(envget VPNCONTAINER_SKIP_PORTAL) == 1 ]] && cfg_overrides 2>&1 | grep -q VPNCONTAINER_SKIP_PORTAL'
sed -i '/^VPNCONTAINER_SKIP_PORTAL=/d' .env
if docker compose version >/dev/null 2>&1; then
  CC=$(bash -c 'source ./vpncontainer; cfg_load; docker compose config 2>/dev/null')
  check "docker compose gets the provider and the gateway from vpncontainer.toml" \
    bash -c 'grep -q "VPNCONTAINER_PROVIDER: globalprotect" <<<"$1" && grep -q "VPNCONTAINER_GATEWAY: dc-ext-gw.vpn.gatech.edu" <<<"$1" && grep -q "VPNCONTAINER_SKIP_PORTAL: \"1\"" <<<"$1"' _ "$CC"
fi

# ---- Your user name (.env), a local file with more hosts --------------------------------
sed -i 's/^VPNCONTAINER_GT_USER=.*/VPNCONTAINER_GT_USER=gtdefault/' .env
cat > vpncontainer.local.toml <<'EOF'
[ssh]
private = []            # only the listed hosts
[[hosts]]
name = "homelab"
hostname = "10.20.30.40"
user = "alice"
port = 2200
x11 = false
options = { ServerAliveInterval = 60, Compression = true }
EOF
check "vpncontainer hosts accepts vpncontainer.local.toml on top" ./vpncontainer hosts
check "a host from vpncontainer.local.toml is in VS Code's list" sshG gt-homelab 'hostname 10.20.30.40'
check "...with its user and port" bash -c "$(declare -f sshG); G='$G'; sshG gt-homelab 'user alice' && sshG gt-homelab 'port 2200'"
check "...and through the container" jumps gt-homelab
check "its address typed directly also goes through the container, with its user" \
  bash -c "$(declare -f sshG jumps); G='$G'; jumps 10.20.30.40 && sshG 10.20.30.40 'user alice' && sshG 10.20.30.40 'port 2200'"
check "with private = [], other GT names connect directly" direct login-ice.pace.gatech.edu
check "...but the listed GT server still goes through the container" jumps ece-linlab19.ece.gatech.edu
check "a host without a user gets VPNCONTAINER_GT_USER from .env" sshG gt-linlab19 'user gtdefault'
check "container: x11 = false turns X11 off for that host" inG homelab 'forwardx11 no'
check "container: options reach the host's ssh config" bash -c "$(declare -f inG); inG homelab 'serveraliveinterval 60' && inG homelab 'compression yes'"
check "container: a host without a user gets VPNCONTAINER_GT_USER" inG linlab19 'user gtdefault'
rm -f vpncontainer.local.toml

cat > vpncontainer.local.toml <<'EOF'
[ssh]
direct = ["github.gatech.edu"]
EOF
./vpncontainer hosts >/dev/null 2>&1
check "[ssh] direct: an exception connects directly" direct github.gatech.edu
check "[ssh] direct: the rest of the pattern still goes through the container" jumps other.gatech.edu
rm -f vpncontainer.local.toml
./vpncontainer hosts >/dev/null 2>&1

# ---- Mistakes are refused, and nothing changes -------------------------------------------------
cp vpncontainer.toml vpncontainer.toml.good
sed -i 's/^provider = .*/provider = "anyconnect"/' vpncontainer.toml
check "an unknown VPN provider is refused" bash -c "./vpncontainer hosts 2>&1 | grep -q 'anyconnect.*not supported yet'"
check "...and stops vpncontainer up before anything starts" bash -c "./vpncontainer up 2>&1 | grep -q 'nothing was changed'"
cp vpncontainer.toml.good vpncontainer.toml
refused "a port that is not a number" $'[[hosts]]\nname = "x"\nhostname = "x.gatech.edu"\nport = "22"' "port must be a number"
refused "an unknown key in [[hosts]]" $'[[hosts]]\nname = "x"\nhostname = "x.gatech.edu"\nhost = "y"' "unknown key 'host'"
refused "a host name with a wildcard" $'[[hosts]]\nname = "x*"\nhostname = "x.gatech.edu"' "no wildcards"
refused "the same name twice" $'[[hosts]]\nname = "linlab19"\nhostname = "x.gatech.edu"' "used twice"
refused "an ssh option that does not exist" $'[[hosts]]\nname = "x"\nhostname = "x.gatech.edu"\noptions = { NotAnOption = "yes" }' "host 'x'"
refused "an option that would break the blocks" $'[[hosts]]\nname = "x"\nhostname = "x.gatech.edu"\noptions = { Match = "all" }' "can't be set here"
refused "a TOML syntax error" 'this is not toml' "vpncontainer.toml"
refused "an unknown section" $'[vpnn]\nportal = "x"' "unknown section [vpnn]"
cp vpncontainer.toml.good vpncontainer.toml
sed -i 's/^  "\*\.gatech\.edu",.*/  "*",/' vpncontainer.toml
check "a pattern that would match the container itself is refused" bash -c "./vpncontainer hosts 2>&1 | grep -q 'match the container itself'"
cp vpncontainer.toml.good vpncontainer.toml
printf 'Host oldbox\n    HostName old.gatech.edu\n' > hosts.conf
check "an old hosts.conf gets a note to move its hosts" bash -c "./vpncontainer hosts 2>&1 | grep -q 'hosts.conf is no longer read.*oldbox'"
rm -f hosts.conf
check "after all that, the good file still works" ./vpncontainer hosts

# ---- From before the rename (gtvpn): files renamed in place, once ---------------------
H2=$T/home2; R2=$T/repo2; O=$H2/.ssh/gtvpn; mkdir -p "$O" "$R2/host"
cp "$SRC/vpncontainer" "$SRC/vpncontainer.toml" "$SRC/.env.example" "$SRC/compose.yaml" "$R2/"; cp "$SRC/host/config.py" "$R2/host/"
printf 'HOST_UID=1000\nGTVPN_GT_USER=olduser\nGTVPN_SSH_PORT=2299\n' > "$R2/.env"
printf 'username=olduser\n' > "$R2/gtvpn-login.conf"
touch -d '2 days ago' "$R2/gtvpn-login.conf"; touch -d '1 day ago' "$R2/gtvpn-login.conf.refused"
REF_T=$(stat -c %Y "$R2/gtvpn-login.conf.refused")
# ~/.ssh/gtvpn/config as the old ./gtvpn wrote it
printf 'Host gtvpn\n    HostName 127.0.0.1\n    Port 2299\n    IdentityFile %s/id_ed25519\n    UserKnownHostsFile %s/known_hosts\n\nHost gt-linlab19\n    HostName ece-linlab19.ece.gatech.edu\n    ProxyJump gtvpn\n    ControlPath %s/cm-%%C\n' "$O" "$O" "$O" > "$O/config"
printf 'Include %s/config\n\nHost mine\n    HostName example.com\n' "$O" > "$H2/.ssh/config"
OLDCFG=$(cat "$H2/.ssh/config")
legacy_run() { (cd "$R2" && HOME=$H2 ./vpncontainer route example.com 2>&1); }
MSG=$(legacy_run)
N=$H2/.ssh/vpncontainer
check "old .env: GTVPN_ settings become VPNCONTAINER_ ones" \
  bash -c "grep -qx VPNCONTAINER_SSH_PORT=2299 '$R2/.env' && grep -qx VPNCONTAINER_GT_USER=olduser '$R2/.env' && grep -qx HOST_UID=1000 '$R2/.env' && ! grep -q GTVPN_ '$R2/.env'"
check "...the file as it was is kept as .env.before-rename" grep -qx GTVPN_SSH_PORT=2299 "$R2/.env.before-rename"
check "...and the CLI reads the renamed setting" bash -c "cd '$R2' && source ./vpncontainer && [[ \$(envget VPNCONTAINER_SSH_PORT) == 2299 ]]"
check "gtvpn-login.conf becomes vpncontainer-login.conf, .refused with its time" \
  bash -c "[[ -f '$R2/vpncontainer-login.conf' && ! -e '$R2/gtvpn-login.conf' && \$(stat -c %Y '$R2/vpncontainer-login.conf.refused') == $REF_T ]]"
check "the folder .ssh/gtvpn moves to .ssh/vpncontainer" bash -c "[[ -f '$N/config' && ! -e '$O' ]]"
check "...~/.ssh/config includes the new place, the rest unchanged" \
  bash -c "[[ \$(cat '$H2/.ssh/config') == \"\${1//\$2/\$3}\" ]]" _ "$OLDCFG" "$O/config" "$N/config"
check "...its config is usable right away: alias vpncontainer, the new paths" \
  bash -c "o=\$(ssh -G -F '$N/config' vpncontainer) && grep -qx 'port 2299' <<<\"\$o\" && grep -qx 'identityfile $N/id_ed25519' <<<\"\$o\" && grep -qx 'userknownhostsfile $N/known_hosts' <<<\"\$o\""
check "...and gt-<name> hosts jump through vpncontainer" \
  bash -c "o=\$(ssh -G -F '$N/config' gt-linlab19) && grep -qx 'proxyjump vpncontainer' <<<\"\$o\" && grep -q '^controlpath $N/cm-' <<<\"\$o\""
check "it says what it renamed" bash -c 'grep -q "renamed the GTVPN_ settings" <<<"$1" && grep -q "moved ~/.ssh/gtvpn" <<<"$1"' _ "$MSG"
cp -p "$R2/.env.before-rename" "$T/env.copy"; mkdir -p "$O"
MSG=$(legacy_run)
check "a second run renames nothing (and overwrites no copy)" \
  bash -c "[[ -z \$(grep -E 'renamed|moved' <<<\"\$1\") ]] && cmp -s '$R2/.env.before-rename' '$T/env.copy' && [[ -d '$O' && -f '$N/config' ]]" _ "$MSG"
exit $FAILED
