# Interactive bash helpers for the vpncontainer container, sourced at the end of
# /etc/bash.bashrc (interactive shells only). They make it obvious when the
# GT tunnel is off, because then the kill-switch blocks everything,
# including name lookups ("Could not resolve hostname").
[ -n "${BASH_VERSION:-}" ] || return 0
case $- in *i*) ;; *) return 0 ;; esac
[ -z "${__VPNCONTAINER_SHELL:-}" ] || return 0
__VPNCONTAINER_SHELL=1

__vpncontainer_up() { [ -e /sys/class/net/tun0 ]; }
__vpncontainer_hint() {
  echo "vpncontainer: the GT tunnel is off, so GT servers can't be reached. Connect first, on your PC: vpncontainer connect" >&2
}
# Prompt marker: Ubuntu's PS1 shows ${debian_chroot}, so set it before each
# prompt: "(GT)dev@vpncontainer:~$" when connected, "(GT:off)dev@vpncontainer:~$" when not.
# shellcheck disable=SC2034  # read by PS1 in ~/.bashrc
__vpncontainer_prompt() { if __vpncontainer_up; then debian_chroot=GT; else debian_chroot=GT:off; fi; }
PROMPT_COMMAND="__vpncontainer_prompt${PROMPT_COMMAND:+; $PROMPT_COMMAND}"
# ssh while the tunnel is off: say why it is about to fail.
ssh() { __vpncontainer_up || __vpncontainer_hint; command ssh "$@"; }

# VS Code Dev Containers hands your computer's ssh-agent to the container
# (a /tmp/vscode-ssh-auth-*.sock socket). Keep your personal keys out of the
# GT side: terminals here do not use it.
case ${SSH_AUTH_SOCK:-} in /tmp/vscode-ssh-auth-*) unset SSH_AUTH_SOCK ;; esac

__vpncontainer_up || __vpncontainer_hint
