# Fake GlobalProtect gateway for the sign-in test. It answers the gateway
# prelogin and login the way GT's gateways do: the password, then a Duo-style
# challenge (sent with HTTP 200, as GT does). Password "student"; each Duo
# code in CODES is accepted once, and any other code (or a reused one) is
# refused with HTTP 512.
import http.server, ssl, sys, urllib.parse

PRELOGIN = ('<?xml version="1.0" encoding="UTF-8" ?><prelogin-response><status>Success</status>'
            '<ccusername/><autosubmit>false</autosubmit><msg/><newmsg/>'
            '<authentication-message>Enter login credentials</authentication-message>'
            '<username-label>Username</username-label><password-label>Password</password-label>'
            '<panos-version>1</panos-version><region>US</region></prelogin-response>')
CHALLENGE = ('var respStatus = "Challenge";\n'
             'var respMsg = "Choose a secondary factor from (\'push\', \'phone\') or enter passcode:";\n'
             'thisForm.inputStr.value = "f00dfeed";\n')
ARGS = ['', 'AUTHCOOKIE0123456789abcdef', 'PERSIST0123456789abcdef0123456789abcdef', 'GW-N', 'gtuser',
        'RADIUS-Duo', 'vsys1', 'gatech', '', '', '', '', 'tunnel', '-1', '4100', '10.0.0.2',
        'empty', 'empty', '', '4', 'unknown']
LOGIN = ('<?xml version="1.0" encoding="utf-8"?><jnlp><application-desc>'
         + ''.join(f'<argument>{a}</argument>' for a in ARGS)
         + '</application-desc></jnlp>')
CODES = ('123456', '246810', '135791', '112358')
used = set()

class H(http.server.BaseHTTPRequestHandler):
    def reply(self, code, body, ctype):
        b = body.encode()
        self.send_response(code); self.send_header('Content-Type', ctype)
        self.send_header('Content-Length', str(len(b))); self.end_headers(); self.wfile.write(b)

    def do_POST(self):
        n = int(self.headers.get('Content-Length') or 0)
        f = urllib.parse.parse_qs(self.rfile.read(n).decode(), keep_blank_values=True)
        g = lambda k: (f.get(k) or [''])[0]
        path = self.path.split('?')[0]
        print('REQ', path, 'user=' + g('user'), 'inputStr=' + g('inputStr'), flush=True)
        if path == '/ssl-vpn/prelogin.esp':
            return self.reply(200, PRELOGIN, 'application/xml')
        if path == '/ssl-vpn/login.esp':
            if not g('inputStr'):
                if g('user') == 'gtuser' and g('passwd') == 'student':
                    return self.reply(200, CHALLENGE, "text/html")
            elif g('inputStr') == 'f00dfeed' and g('passwd') in CODES and g('passwd') not in used:
                used.add(g('passwd'))
                return self.reply(200, LOGIN, 'application/xml')
            return self.reply(512, 'Invalid username or password', 'text/html')
        self.reply(404, 'not here', 'text/plain')

    do_GET = do_POST
    def log_message(self, *a): pass

ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
ctx.load_cert_chain(sys.argv[1], sys.argv[2])
s = http.server.HTTPServer(('0.0.0.0', int(sys.argv[3]) if len(sys.argv) > 3 else 8443), H)
s.socket = ctx.wrap_socket(s.socket, server_side=True)
s.serve_forever()
