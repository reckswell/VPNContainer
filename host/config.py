#!/usr/bin/env python3
"""Read gtvpn.toml (and the optional, untracked gtvpn.local.toml) for ./gtvpn.

    config.py records   CONF [LOCAL]   settings for ./gtvpn, one per line:
                                         env <GTVPN_VAR> <value>
                                         prefix <VS Code name prefix>
                                         private <ssh pattern>
                                         direct <ssh pattern>
    config.py ssh-hosts CONF [LOCAL]   the [[hosts]] as OpenSSH config (for
                                       the container, where `ssh <name>` works)

Every value is checked here, so ./gtvpn can use the lines as they are. A
mistake is reported as "gtvpn.toml: <where>: <what>", with exit status 1.
Needs Python 3.11+ (tomllib; Ubuntu 24.04 has 3.12) or the tomli package.
"""
import fnmatch
import os
import re
import sys

try:
    import tomllib
except ModuleNotFoundError:  # Python < 3.11
    try:
        import tomli as tomllib
    except ModuleNotFoundError:
        sys.exit("gtvpn: reading gtvpn.toml needs Python 3.11 or newer "
                 "(Ubuntu 24.04 has it), or: sudo apt install python3-tomli")

# VPN types the container can run. Only GlobalProtect today; a new type is
# added here and in image/rootfs/usr/local/sbin/gtvpn-connect.
PROVIDERS = ("globalprotect",)

HOST_RE = r"[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?"
NAME_RE = r"[A-Za-z0-9][A-Za-z0-9._-]*"
TEXT_RE = r"[A-Za-z0-9 ._:/@+-]*"

# section -> key -> (environment variable or None, kind, extra)
SCHEMA = {
    "vpn": {
        "provider": ("GTVPN_PROVIDER", "choice", PROVIDERS),
        "portal": ("GTVPN_PORTAL", "hostport", None),
        "gateway": ("GTVPN_GATEWAY", "hostport", None),
        "skip_portal": ("GTVPN_SKIP_PORTAL", "bool", None),
        "gateway_name": ("GTVPN_GATEWAY_NAME", "text", None),
        "auth": ("GTVPN_AUTH", "choice", ("auto", "saml", "password")),
        "client": ("GTVPN_CLIENT", "choice", ("auto", "openconnect", "gpclient")),
        "username": ("GTVPN_USER", "name", None),
        "hip": ("GTVPN_HIP", "bool", None),
        "os": ("GTVPN_OS", "text", None),
        "client_version": ("GTVPN_CLIENT_VERSION", "text", None),
        "fix_openssl": ("GTVPN_FIX_OPENSSL", "bool", None),
    },
    "ssh": {
        "user": ("GTVPN_GT_USER", "name", None),
        "prefix": (None, "prefix", None),
        "private": (None, "patterns", None),
        "direct": (None, "patterns", None),
    },
    "checks": {
        "probe_targets": ("GTVPN_PROBE_TARGETS", "hostports", None),
        "probe_names": ("GTVPN_PROBE_NAMES", "hosts", None),
        "egress_url": ("GTVPN_EGRESS_URL", "url", None),
    },
    "direct_egress": {
        "enabled": ("GTVPN_DIRECT_EGRESS", "bool", None),
        "allow": ("GTVPN_DIRECT_ALLOW", "hosts", None),
        "no_proxy_extra": ("GTVPN_NO_PROXY_EXTRA", "noproxy", None),
    },
}
HOST_KEYS = ("name", "hostname", "user", "port", "x11", "options")
# ssh_config keywords that would break the generated blocks, and the ones a
# [[hosts]] field already sets.
NOT_AN_OPTION = {"host", "match", "include", "hostname", "user", "port"}


class ConfigError(Exception):
    pass


def full(regex, value):
    return isinstance(value, str) and re.fullmatch(regex, value) is not None


def check_value(where, kind, extra, v):
    """Return the value as an environment string (or a list for patterns)."""
    def bad(msg):
        raise ConfigError(f"{where}: {msg}")

    if kind == "bool":
        if not isinstance(v, bool):
            bad("must be true or false")
        return "1" if v else "0"
    if kind == "choice":
        if v not in extra:
            bad(f"{v!r} is not supported" + (" yet" if extra is PROVIDERS else "")
                + f" (use one of: {', '.join(extra)})")
        return v
    if kind == "hostport":
        if not full(HOST_RE + r"(?::[0-9]{1,5})?", v):
            bad("must be a host name, e.g. vpn.example.edu")
        return v
    if kind == "text":
        if not full(TEXT_RE, v):
            bad("may only contain letters, digits, spaces and . _ : / @ + -")
        return v
    if kind == "name":
        if not full(NAME_RE, v):
            bad("must be a plain user name (letters, digits, . _ -)")
        return v
    if kind == "prefix":
        if not full(r"[A-Za-z0-9._-]*", v):
            bad("may only contain letters, digits and . _ -")
        return v
    if kind == "url":
        if not full(r"https?://" + HOST_RE + r"(?::[0-9]{1,5})?(?:/[A-Za-z0-9._~/?=&%+-]*)?", v):
            bad("must be an http(s) URL")
        return v
    # The rest are lists.
    if not isinstance(v, list) or not all(isinstance(x, str) for x in v):
        bad('must be a list of strings, e.g. ["a", "b"]')
    if kind == "hosts":
        for x in v:
            if not full(HOST_RE, x):
                bad(f"{x!r} is not a host name")
        return " ".join(v)
    if kind == "hostports":
        for x in v:
            if not full(HOST_RE + r":[0-9]{1,5}", x):
                bad(f"{x!r} must be host:port, e.g. server.example.edu:22")
        return " ".join(v)
    if kind == "noproxy":
        for x in v:
            if not full(r"[A-Za-z0-9.*/:-]+", x):
                bad(f"{x!r} must be a host name, .domain or address/prefix")
        # gtvpn-entrypoint puts it in front of its own list.
        return "".join(x + "," for x in v)
    if kind == "patterns":
        for x in v:
            if not full(r"[A-Za-z0-9.*?_-]+", x):
                bad(f"{x!r}: a pattern is a host name or address with * or ?, "
                    "e.g. *.example.edu or 10.20.*")
            low = x.lower()
            for own in ("127.0.0.1", "localhost", "gtvpn"):
                if fnmatch.fnmatchcase(own, low):
                    bad(f"{x!r} would also match the container itself ({own})")
        return list(v)
    raise AssertionError(kind)


def option_value(where, v):
    if isinstance(v, bool):
        return "yes" if v else "no"
    if isinstance(v, int):
        return str(v)
    if not isinstance(v, str) or not v or not full(r"[^\x00-\x1f\x7f\"#]+", v):
        raise ConfigError(f"{where}: must be a word, a number or true/false "
                          "(no quotes, # or line breaks)")
    return f'"{v}"' if " " in v else v


def check_host(i, h):
    where = f"[[hosts]] #{i}"
    if not isinstance(h, dict):
        raise ConfigError(f"{where}: must be a table")
    if isinstance(h.get("name"), str):
        where += f" ({h['name']})"
    for k in h:
        if k not in HOST_KEYS:
            raise ConfigError(f"{where}: unknown key {k!r} (known: {', '.join(HOST_KEYS)})")
    name, hostname = h.get("name"), h.get("hostname")
    if not full(NAME_RE, name):
        raise ConfigError(f"{where}: name must be a plain name (letters, digits, . _ -), no wildcards")
    if name.lower() == "gtvpn":
        raise ConfigError(f"{where}: the name gtvpn is taken by the container itself")
    if not full(HOST_RE, hostname):
        raise ConfigError(f"{where}: hostname must be a host name or IP address")
    out = {"name": name, "hostname": hostname, "user": None, "port": None,
           "x11": True, "options": []}
    if "user" in h:
        out["user"] = check_value(f"{where} user", "name", None, h["user"])
    if "port" in h:
        p = h["port"]
        if isinstance(p, bool) or not isinstance(p, int) or not 1 <= p <= 65535:
            raise ConfigError(f"{where}: port must be a number from 1 to 65535")
        out["port"] = p
    if "x11" in h:
        if not isinstance(h["x11"], bool):
            raise ConfigError(f"{where}: x11 must be true or false")
        out["x11"] = h["x11"]
    opts = h.get("options", {})
    if not isinstance(opts, dict):
        raise ConfigError(f"{where}: options must be a table, e.g. {{ ServerAliveInterval = 60 }}")
    for k, v in opts.items():
        if not full(r"[A-Za-z][A-Za-z0-9]*", k) or k.lower() in NOT_AN_OPTION:
            raise ConfigError(f"{where}: options: {k!r} can't be set here"
                              + (" (use the field of that name)" if k.lower() in NOT_AN_OPTION else ""))
        out["options"].append((k, option_value(f"{where} options.{k}", v)))
    return out


def read(path):
    try:
        with open(path, "rb") as f:
            return tomllib.load(f)
    except tomllib.TOMLDecodeError as e:
        raise ConfigError(f"{os.path.basename(path)}: {e}") from None


def merge(base, local):
    """gtvpn.local.toml: its settings win, its hosts are added (same name: replaced)."""
    out = {k: (dict(v) if isinstance(v, dict) else v) for k, v in base.items()}
    for k, v in local.items():
        if k == "hosts" and isinstance(v, list) and isinstance(out.get("hosts", []), list):
            hosts = list(out.get("hosts", []))
            for h in v:
                n = h.get("name") if isinstance(h, dict) else None
                hosts = [x for x in hosts if not (isinstance(x, dict) and n is not None and x.get("name") == n)]
                hosts.append(h)
            out["hosts"] = hosts
        elif isinstance(v, dict) and isinstance(out.get(k), dict):
            out[k].update(v)
        else:
            out[k] = v
    return out


def load(conf, local):
    data = read(conf)
    label = os.path.basename(conf)
    if local and os.path.isfile(local):
        data = merge(data, read(local))
        label += " + " + os.path.basename(local)
    try:
        return settings(data)
    except ConfigError as e:
        raise ConfigError(f"{label}: {e}") from None


def settings(data):
    env, cfg = {}, {"prefix": "gt-", "private": [], "direct": [], "hosts": []}
    for section, body in data.items():
        if section == "hosts":
            continue
        if section not in SCHEMA:
            raise ConfigError(f"unknown section [{section}] (known: {', '.join(list(SCHEMA) + ['[hosts]'])})")
        if not isinstance(body, dict):
            raise ConfigError(f"{section} must be a section: [{section}]")
        for key, v in body.items():
            if key not in SCHEMA[section]:
                raise ConfigError(f"[{section}]: unknown key {key!r} (known: {', '.join(SCHEMA[section])})")
            var, kind, extra = SCHEMA[section][key]
            val = check_value(f"[{section}] {key}", kind, extra, v)
            if var is None:
                cfg[key] = val
            elif val != "":  # empty: not set here (.env or the default applies)
                env[var] = val
    hosts = data.get("hosts", [])
    if not isinstance(hosts, list):
        raise ConfigError("hosts must be written as [[hosts]] blocks")
    seen = set()
    for i, h in enumerate(hosts, 1):
        h = check_host(i, h)
        if h["name"].lower() in seen:
            raise ConfigError(f"[[hosts]] #{i}: the name {h['name']!r} is used twice")
        seen.add(h["name"].lower())
        for d in cfg["direct"]:
            if fnmatch.fnmatchcase(h["hostname"].lower(), d.lower()):
                raise ConfigError(f"[[hosts]] #{i} ({h['name']}): {h['hostname']} is listed, "
                                  f"but [ssh] direct {d!r} says to connect to it directly")
        cfg["hosts"].append(h)
    return env, cfg


def ssh_hosts(cfg):
    lines = []
    for h in cfg["hosts"]:
        lines += ["", f"# '{h['name']}' from gtvpn.toml", f"Host {h['name']}", f"    HostName {h['hostname']}"]
        if h["user"]:
            lines.append(f"    User {h['user']}")
        if h["port"]:
            lines.append(f"    Port {h['port']}")
        if not h["x11"]:
            lines += ["    ForwardX11 no", "    ForwardX11Trusted no"]
        lines += [f"    {k} {v}" for k, v in h["options"]]
    return "\n".join(lines).lstrip("\n")


def main(argv):
    if len(argv) not in (3, 4) or argv[1] not in ("records", "ssh-hosts"):
        sys.exit(__doc__)
    conf, local = argv[2], (argv[3] if len(argv) == 4 else None)
    try:
        env, cfg = load(conf, local)
    except ConfigError as e:
        print(e, file=sys.stderr)
        return 1
    except OSError as e:
        print(f"gtvpn: can't read {e.filename}: {e.strerror}", file=sys.stderr)
        return 1
    if argv[1] == "ssh-hosts":
        text = ssh_hosts(cfg)
        if text:
            print(text)
        return 0
    for var in sorted(env):
        print(f"env {var} {env[var]}")
    print(f"prefix {cfg['prefix']}")
    for p in cfg["private"]:
        print(f"private {p}")
    for p in cfg["direct"]:
        print(f"direct {p}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
