#!/usr/bin/env bash
# Checks gtvpn.toml handling without Docker or a VPN: the committed file
# loads, listed hosts and patterns go through the container, everything else
# connects directly, mistakes are refused and change nothing.
# Works on a temporary copy of the repo with a temporary HOME, so your own
# gtvpn.toml, .env and ~/.ssh are not touched. Needs python3 and ssh.
#   tests/config/run-config.sh
set -uo pipefail
SRC=$(cd "$(dirname "$0")/../.." && pwd)
command -v ssh >/dev/null || { echo "needs an ssh client (sudo apt install openssh-client)"; exit 1; }
T=$(mktemp -d); trap 'rm -rf "$T"' EXIT
umask 077
R=$T/repo; mkdir -p "$R/host" "$T/home/.ssh"
cp "$SRC/gtvpn" "$SRC/gtvpn.toml" "$SRC/.env.example" "$SRC/compose.yaml" "$R/"
cp "$SRC/host/config.py" "$R/host/"
cp "$R/.env.example" "$R/.env"
export HOME=$T/home GTVPN_ENGINE=gtvpn-test-no-engine
G=$HOME/.ssh/gtvpn/config
printf 'Include %s\n' "$G" > "$HOME/.ssh/config"
cd "$R" || exit 1

FAILED=0; ok(){ printf '\033[32mPASS\033[0m %s\n' "$*"; }; bad(){ printf '\033[31mFAIL\033[0m %s\n' "$*"; FAILED=1; }
check(){ local d=$1; shift; if "$@" >/dev/null 2>&1; then ok "$d"; else bad "$d"; fi; }
# ssh -G as this computer's ssh / VS Code sees it (capture first: grep -q can SIGPIPE ssh)
sshG() { local o; o=$(ssh -G -F "$G" "$1" 2>/dev/null </dev/null); grep -qx "$2" <<<"$o"; }
jumps() { sshG "$1" 'proxyjump gtvpn'; }
direct() { local o; o=$(ssh -G -F "$G" "$1" 2>/dev/null </dev/null) && ! grep -q '^proxyjump ' <<<"$o"; }
# ...and as ssh inside the container sees runtime/in/hosts.conf
inG() { local o; o=$(ssh -G -F runtime/in/hosts.conf "$1" 2>/dev/null </dev/null); grep -qx "$2" <<<"$o"; }
refused() { # description, toml text appended to the committed file, expected message
  cp "$SRC/gtvpn.toml" gtvpn.toml; printf '%s\n' "$2" >> gtvpn.toml
  local before out; before=$(cat "$G")
  if out=$(./gtvpn hosts 2>&1); then bad "$1 (accepted)"; return; fi
  if [[ $(cat "$G") != "$before" ]]; then bad "$1 (config changed)"; return; fi
  if grep -qF -- "$3" <<<"$out"; then ok "$1"; else bad "$1 (message: $out)"; fi
}

# ---- The committed file ---------------------------------------------------------
check "the committed gtvpn.toml loads" python3 -I host/config.py records gtvpn.toml
check "it names the VPN provider (globalprotect)" grep -qx 'env GTVPN_PROVIDER globalprotect' <(python3 -I host/config.py records gtvpn.toml)
check "gtvpn hosts accepts it" ./gtvpn hosts
check "VS Code entry gt-linlab19 goes to ece-linlab19" sshG gt-linlab19 'hostname ece-linlab19.ece.gatech.edu'
check "gt-linlab19 goes through the container" jumps gt-linlab19
check "ece-linlab19 typed by its own name goes through the container" jumps ece-linlab19.ece.gatech.edu
check "another GT name (pattern *.gatech.edu) goes through the container" jumps login-ice.pace.gatech.edu
check "a GT address (pattern 143.215.*) goes through the container" jumps 143.215.155.65
for h in mydesktop.local my-home-pc 192.168.1.20 10.0.0.5 github.com example.com; do
  check "unlisted $h connects directly" direct "$h"
done
check "the container itself (gtvpn) is not jumped through itself" direct gtvpn
check "container: ssh linlab19 knows the server" inG linlab19 'hostname ece-linlab19.ece.gatech.edu'
export GTVPN_SSH_CONFIG=$HOME/.ssh/config  # ssh itself reads the real home's, not $HOME's
check "gtvpn route: a listed host" bash -c "./gtvpn route ece-linlab19.ece.gatech.edu | grep -v direct | grep -q 'through the container'"
check "gtvpn route: an unlisted host" bash -c "./gtvpn route mydesktop.local | grep -q 'direct, not through'"
check "settings are exported for docker compose (gateway-only sign-in)" \
  bash -c 'source ./gtvpn; cfg_load; [[ $GTVPN_SKIP_PORTAL == 1 && $GTVPN_GATEWAY == dc-ext-gw.vpn.gatech.edu && $GTVPN_GATEWAY_NAME == "DC Gateway" ]]'
check "a setting gtvpn.toml leaves out still comes from .env" \
  bash -c 'sed -i "s/^GTVPN_SSH_PORT=.*/GTVPN_SSH_PORT=2299/" .env; source ./gtvpn; cfg_load; [[ $(envget GTVPN_SSH_PORT) == 2299 ]]'
sed -i 's/^GTVPN_SKIP_PORTAL=.*/GTVPN_SKIP_PORTAL=0/' .env; grep -q '^GTVPN_SKIP_PORTAL=' .env || echo GTVPN_SKIP_PORTAL=0 >> .env
check "gtvpn.toml wins over .env, and says so" \
  bash -c 'source ./gtvpn; cfg_load; [[ $(envget GTVPN_SKIP_PORTAL) == 1 ]] && cfg_overrides 2>&1 | grep -q GTVPN_SKIP_PORTAL'
sed -i '/^GTVPN_SKIP_PORTAL=/d' .env
if docker compose version >/dev/null 2>&1; then
  CC=$(bash -c 'source ./gtvpn; cfg_load; docker compose config 2>/dev/null')
  check "docker compose gets the provider and the gateway from gtvpn.toml" \
    bash -c 'grep -q "GTVPN_PROVIDER: globalprotect" <<<"$1" && grep -q "GTVPN_GATEWAY: dc-ext-gw.vpn.gatech.edu" <<<"$1" && grep -q "GTVPN_SKIP_PORTAL: \"1\"" <<<"$1"' _ "$CC"
fi

# ---- Your user name (.env), a local file with more hosts --------------------------------
sed -i 's/^GTVPN_GT_USER=.*/GTVPN_GT_USER=gtdefault/' .env
cat > gtvpn.local.toml <<'EOF'
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
check "gtvpn hosts accepts gtvpn.local.toml on top" ./gtvpn hosts
check "a host from gtvpn.local.toml is in VS Code's list" sshG gt-homelab 'hostname 10.20.30.40'
check "...with its user and port" bash -c "$(declare -f sshG); G='$G'; sshG gt-homelab 'user alice' && sshG gt-homelab 'port 2200'"
check "...and through the container" jumps gt-homelab
check "its address typed directly also goes through the container, with its user" \
  bash -c "$(declare -f sshG jumps); G='$G'; jumps 10.20.30.40 && sshG 10.20.30.40 'user alice' && sshG 10.20.30.40 'port 2200'"
check "with private = [], other GT names connect directly" direct login-ice.pace.gatech.edu
check "...but the listed GT server still goes through the container" jumps ece-linlab19.ece.gatech.edu
check "a host without a user gets GTVPN_GT_USER from .env" sshG gt-linlab19 'user gtdefault'
check "container: x11 = false turns X11 off for that host" inG homelab 'forwardx11 no'
check "container: options reach the host's ssh config" bash -c "$(declare -f inG); inG homelab 'serveraliveinterval 60' && inG homelab 'compression yes'"
check "container: a host without a user gets GTVPN_GT_USER" inG linlab19 'user gtdefault'
rm -f gtvpn.local.toml

cat > gtvpn.local.toml <<'EOF'
[ssh]
direct = ["github.gatech.edu"]
EOF
./gtvpn hosts >/dev/null 2>&1
check "[ssh] direct: an exception connects directly" direct github.gatech.edu
check "[ssh] direct: the rest of the pattern still goes through the container" jumps other.gatech.edu
rm -f gtvpn.local.toml
./gtvpn hosts >/dev/null 2>&1

# ---- Mistakes are refused, and nothing changes -------------------------------------------------
cp gtvpn.toml gtvpn.toml.good
sed -i 's/^provider = .*/provider = "anyconnect"/' gtvpn.toml
check "an unknown VPN provider is refused" bash -c "./gtvpn hosts 2>&1 | grep -q 'anyconnect.*not supported yet'"
check "...and stops gtvpn up before anything starts" bash -c "./gtvpn up 2>&1 | grep -q 'nothing was changed'"
cp gtvpn.toml.good gtvpn.toml
refused "a port that is not a number" $'[[hosts]]\nname = "x"\nhostname = "x.gatech.edu"\nport = "22"' "port must be a number"
refused "an unknown key in [[hosts]]" $'[[hosts]]\nname = "x"\nhostname = "x.gatech.edu"\nhost = "y"' "unknown key 'host'"
refused "a host name with a wildcard" $'[[hosts]]\nname = "x*"\nhostname = "x.gatech.edu"' "no wildcards"
refused "the same name twice" $'[[hosts]]\nname = "linlab19"\nhostname = "x.gatech.edu"' "used twice"
refused "an ssh option that does not exist" $'[[hosts]]\nname = "x"\nhostname = "x.gatech.edu"\noptions = { NotAnOption = "yes" }' "host 'x'"
refused "an option that would break the blocks" $'[[hosts]]\nname = "x"\nhostname = "x.gatech.edu"\noptions = { Match = "all" }' "can't be set here"
refused "a TOML syntax error" 'this is not toml' "gtvpn.toml"
refused "an unknown section" $'[vpnn]\nportal = "x"' "unknown section [vpnn]"
cp gtvpn.toml.good gtvpn.toml
sed -i 's/^  "\*\.gatech\.edu",.*/  "*",/' gtvpn.toml
check "a pattern that would match the container itself is refused" bash -c "./gtvpn hosts 2>&1 | grep -q 'match the container itself'"
cp gtvpn.toml.good gtvpn.toml
printf 'Host oldbox\n    HostName old.gatech.edu\n' > hosts.conf
check "an old hosts.conf gets a note to move its hosts" bash -c "./gtvpn hosts 2>&1 | grep -q 'hosts.conf is no longer read.*oldbox'"
rm -f hosts.conf
check "after all that, the good file still works" ./gtvpn hosts
exit $FAILED
