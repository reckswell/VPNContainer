#!/usr/bin/env bash
# Start the two-container benchmark lab with an emulated link.
#   RTT_MS (default 6) and MBIT (default 113) reproduce what a PC measured to
#   ece-linlab19 through the GT tunnel: a 7 ms X11 round trip, 113 Mbit/s.
set -euo pipefail
cd "$(dirname "$0")"
XENV=${XENV:?path to the xpra conda env}
RTT_MS=${RTT_MS:-6}; MBIT=${MBIT:-113}
docker rm -f perf-gthost perf-client >/dev/null 2>&1 || true
docker network rm perf-net >/dev/null 2>&1 || true
docker network create --subnet 172.31.77.0/24 perf-net >/dev/null
for n in gthost client; do
  docker run -d --init --name perf-$n --hostname $n --network perf-net \
    -v "$XENV:$XENV:ro" --shm-size 512m perf/lab sleep infinity >/dev/null
done
# The kernel here has no netem, so the link is emulated in user space: ssh
# from the client goes through linkemu (RTT_MS, MBIT) to the GT stand-in.
docker cp linkemu.py perf-client:/usr/local/bin/linkemu.py
docker exec -d perf-client python3 /usr/local/bin/linkemu.py 2200 gthost 22 "$RTT_MS" "$MBIT"
# gthost: sshd + key for dev@client
docker exec perf-client su dev -c "ssh-keygen -q -t ed25519 -N '' -f ~/.ssh/id_ed25519"
docker exec perf-gthost bash -c 'mkdir -p ~gtuser/.ssh && chmod 700 ~gtuser/.ssh && chown gtuser: ~gtuser/.ssh; /usr/sbin/sshd'
docker exec perf-client cat /home/dev/.ssh/id_ed25519.pub | docker exec -i perf-gthost bash -c 'cat > ~gtuser/.ssh/authorized_keys; chown gtuser: ~gtuser/.ssh/authorized_keys; chmod 600 ~gtuser/.ssh/authorized_keys'
docker exec perf-client su dev -c "printf 'Host gthost\n  HostName 127.0.0.1\n  Port 2200\n  HostKeyAlias gthost\n  User gtuser\n  StrictHostKeyChecking accept-new\n  ControlMaster auto\n  ControlPath ~/.ssh/cm-%%C\n  ControlPersist 1h\n' > ~/.ssh/config"
# client: the "desktop" X server (stands in for WSLg / VcXsrv on Windows)
docker exec -d perf-client su dev -c 'Xvfb :1 -screen 0 2560x1440x24 -nolisten tcp -auth /home/dev/.xa >/dev/null 2>&1'
docker exec perf-client su dev -c 'xauth -f ~/.xa add :1 . $(od -An -N16 -tx1 /dev/urandom | tr -d " \n"); cp ~/.xa ~/.Xauthority'
sleep 1
docker exec perf-client su dev -c 'DISPLAY=:1 xdpyinfo | grep -E "dimensions|DAMAGE|XTEST" | head -3; ssh gthost true && echo ssh-ok'
docker exec perf-client su dev -c 'for i in 1 2 3 4 5; do s=$(date +%s%N); ssh gthost true; e=$(date +%s%N); echo "ssh round trip over the open connection: $(( (e-s)/1000000 )) ms"; done' | tail -2
