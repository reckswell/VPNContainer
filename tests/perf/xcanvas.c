/* xcanvas: a Virtuoso-like layout canvas drawn with core X11 requests
 * (stippled XFillRectangles into a server-side pixmap, then XCopyArea),
 * the classic way EDA canvases draw. Same scene and keys as qtcanvas.py:
 * arrows pan, +/- zoom, a toggles a 180 fps continuous pan.
 * Build: cc -O2 -o xcanvas xcanvas.c -lX11 -lm */
#include <X11/Xlib.h>
#include <X11/Xutil.h>
#include <X11/keysym.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/select.h>
#include <time.h>
#include <math.h>

#define NL 10
typedef struct { float x, y, w, h; } R;
static R *sh[NL]; static int nsh[NL];
static const char *col[NL] = {"#2e8b57","#d2691e","#228b22","#dc143c","#ffffff","#1e90ff","#ffd700","#da70d6","#00ced1","#adff2f"};
static unsigned char stip[NL][8] = {
  {0x01,0x02,0x04,0x08,0x10,0x20,0x40,0x80},{0x80,0x40,0x20,0x10,0x08,0x04,0x02,0x01},
  {0x81,0x42,0x24,0x18,0x18,0x24,0x42,0x81},{0x55,0xaa,0x55,0xaa,0x55,0xaa,0x55,0xaa},
  {0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff},{0x11,0x00,0x44,0x00,0x11,0x00,0x44,0x00},
  {0x10,0x10,0xff,0x10,0x10,0x10,0x10,0x10},{0x01,0x02,0x04,0x08,0x10,0x20,0x40,0x80},
  {0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff},{0x80,0x40,0x20,0x10,0x08,0x04,0x02,0x01}};
static unsigned rnd_s = 1;
static float frand(float a, float b) { rnd_s = rnd_s * 1103515245u + 12345u; return a + (b - a) * ((rnd_s >> 8) & 0xffffff) / 16777216.0f; }

int main(int argc, char **argv) {
  int n = argc > 1 ? atoi(argv[1]) : 8000, W = 1600, H = 1000, i, l;
  for (l = 0; l < NL; l++) sh[l] = malloc(sizeof(R) * n);
  for (i = 0; i < n; i++) {
    l = (int)frand(0, NL); if (l >= NL) l = NL - 1;
    R r; r.x = frand(0, 4000); r.y = frand(0, 4000);
    if (l == 4 || l == 6 || l == 8) r.w = r.h = frand(2, 6);
    else if (frand(0, 1) < 0.5f) { r.w = frand(40, 400); r.h = frand(3, 12); }
    else { r.w = frand(3, 12); r.h = frand(40, 400); }
    sh[l][nsh[l]++] = r;
  }
  Display *d = XOpenDisplay(NULL); if (!d) { fprintf(stderr, "no display\n"); return 1; }
  int s = DefaultScreen(d);
  Window w = XCreateSimpleWindow(d, RootWindow(d, s), 0, 0, W, H, 0, 0, BlackPixel(d, s));
  XStoreName(d, w, "perf-xcanvas");
  XSelectInput(d, w, ExposureMask | KeyPressMask | StructureNotifyMask);
  XMapWindow(d, w);
  Pixmap pm = XCreatePixmap(d, w, W, H, DefaultDepth(d, s));
  GC gc[NL], bg = XCreateGC(d, pm, 0, NULL);
  XSetForeground(d, bg, BlackPixel(d, s));
  for (l = 0; l < NL; l++) {
    XColor c; XParseColor(d, DefaultColormap(d, s), col[l], &c); XAllocColor(d, DefaultColormap(d, s), &c);
    gc[l] = XCreateGC(d, pm, 0, NULL); XSetForeground(d, gc[l], c.pixel);
    Pixmap st = XCreateBitmapFromData(d, pm, (char *)stip[l], 8, 8);
    XSetStipple(d, gc[l], st); XSetFillStyle(d, gc[l], FillStippled);
  }
  XRectangle *buf = malloc(sizeof(XRectangle) * n);
  double ox = 1500, oy = 1500, sc = 1.2; int anim = 0, dirty = 1;
  for (;;) {
    while (XPending(d)) {
      XEvent e; XNextEvent(d, &e);
      if (e.type == Expose) dirty = 1;
      else if (e.type == ConfigureNotify && (e.xconfigure.width != W || e.xconfigure.height != H)) {
        W = e.xconfigure.width; H = e.xconfigure.height; XFreePixmap(d, pm);
        pm = XCreatePixmap(d, w, W, H, DefaultDepth(d, s)); dirty = 1;
      } else if (e.type == KeyPress) {
        KeySym k = XLookupKeysym(&e.xkey, 0); double st = W * 0.03 / sc;
        if (k == XK_Right) ox += st; else if (k == XK_Left) ox -= st;
        else if (k == XK_Up) oy -= st; else if (k == XK_Down) oy += st;
        else if (k == XK_plus || k == XK_equal) sc *= 1.15; else if (k == XK_minus) sc /= 1.15;
        else if (k == XK_a) anim = !anim; else continue;
        dirty = 1;
      }
    }
    if (anim) { ox += 4 / sc; dirty = 1; }
    if (dirty) {
      /* keep the view over the dense part of the layout while panning */
      ox = 500 + fmod(fmod(ox - 500, 2000) + 2000, 2000); oy = 500 + fmod(fmod(oy - 500, 2000) + 2000, 2000);
      XFillRectangle(d, pm, bg, 0, 0, W, H);
      double vx1 = ox + W / sc, vy1 = oy + H / sc;
      for (l = 0; l < NL; l++) {
        int m = 0;
        for (i = 0; i < nsh[l]; i++) {
          R *r = &sh[l][i];
          if (r->x > vx1 || r->y > vy1 || r->x + r->w < ox || r->y + r->h < oy) continue;
          int x = (int)((r->x - ox) * sc), y = (int)((r->y - oy) * sc);
          int ww = (int)(r->w * sc), hh = (int)(r->h * sc);
          buf[m].x = x < -32000 ? -32000 : x; buf[m].y = y < -32000 ? -32000 : y;
          buf[m].width = ww < 1 ? 1 : (ww > 65535 ? 65535 : ww); buf[m].height = hh < 1 ? 1 : (hh > 65535 ? 65535 : hh);
          m++;
        }
        XFillRectangles(d, pm, gc[l], buf, m);
        XDrawRectangles(d, pm, gc[l], buf, m);
      }
      XCopyArea(d, pm, w, bg, 0, 0, W, H, 0, 0);
      XFlush(d); dirty = 0;
    }
    if (anim) { XSync(d, False); struct timespec ts = {0, 5000000}; nanosleep(&ts, NULL); }
    else { fd_set f; FD_ZERO(&f); FD_SET(ConnectionNumber(d), &f); select(ConnectionNumber(d) + 1, &f, NULL, NULL, NULL); }
  }
}
