# Stand-in for an allow-listed external API (e.g. api.anthropic.com) in the lab.
# run-lab.sh generates the self-signed cert next to this file.
import http.server, ssl
class H(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200); self.end_headers(); self.wfile.write(b"fake-api-ok\n")
s = http.server.HTTPServer(("0.0.0.0", 443), H)
ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER); ctx.load_cert_chain("/srv/c.pem", "/srv/k.pem")
s.socket = ctx.wrap_socket(s.socket, server_side=True); s.serve_forever()
