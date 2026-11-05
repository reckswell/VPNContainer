# Interactive bash helpers for the gtvpn container, sourced at the end of
# /etc/bash.bashrc (interactive shells only). They make it obvious when the
# GT tunnel is off, because then the kill-switch blocks everything,
# including name lookups ("Could not resolve hostname").
[ -n "${BASH_VERSION:-}" ] || return 0
case $- in *i*) ;; *) return 0 ;; esac
[ -z "${__GTVPN_SHELL:-}" ] || return 0
__GTVPN_SHELL=1

__gtvpn_up() { [ -e /sys/class/net/tun0 ]; }
__gtvpn_hint() {
  echo "gtvpn: the GT tunnel is off, so GT servers can't be reached. Connect first, on your PC: gtvpn connect" >&2
}
# Prompt marker: Ubuntu's PS1 shows ${debian_chroot}, so set it before each
# prompt: "(GT)dev@gtvpn:~$" when connected, "(GT:off)dev@gtvpn:~$" when not.
# shellcheck disable=SC2034  # read by PS1 in ~/.bashrc
__gtvpn_prompt() { if __gtvpn_up; then debian_chroot=GT; else debian_chroot=GT:off; fi; }
PROMPT_COMMAND="__gtvpn_prompt${PROMPT_COMMAND:+; $PROMPT_COMMAND}"
# ssh while the tunnel is off: say why it is about to fail.
ssh() { __gtvpn_up || __gtvpn_hint; command ssh "$@"; }

__gtvpn_up || __gtvpn_hint
