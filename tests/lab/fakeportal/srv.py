# Fake GlobalProtect portal for testing the SSO hand-off (prelogin only).
# Every request gets a SAML "REDIRECT" prelogin response pointing at a fake IdP.
import http.server, ssl, base64
URL=base64.b64encode(b"https://idp.example.edu/sso?SAMLRequest=abc").decode()
BODY=f"""<?xml version="1.0" encoding="UTF-8" ?><prelogin-response><status>Success</status><ccusername/><autosubmit>false</autosubmit><msg/><newmsg/><authentication-message>Enter login credentials</authentication-message><username-label>Username</username-label><password-label>Password</password-label><panos-version>1</panos-version><saml-default-browser>yes</saml-default-browser><saml-auth-status>0</saml-auth-status><saml-auth-method>REDIRECT</saml-auth-method><saml-request>{URL}</saml-request><region>US</region></prelogin-response>"""
class H(http.server.BaseHTTPRequestHandler):
    def do_any(self):
        print("REQ", self.command, self.path, flush=True)
        self.send_response(200); self.send_header("Content-Type","application/xml"); self.end_headers(); self.wfile.write(BODY.encode())
    do_GET=do_POST=do_any
s=http.server.HTTPServer(("0.0.0.0",443),H)
ctx=ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER); ctx.load_cert_chain("/srv/c.pem","/srv/k.pem"); s.socket=ctx.wrap_socket(s.socket,server_side=True)
s.serve_forever()
