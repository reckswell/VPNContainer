/* x11lat: end-to-end responsiveness of a remote window, measured on the
 * LOCAL X display (the one you look at): inject a key with XTest, then
 * watch the screen change with XDamage. Works the same for ssh -X, xpra,
 * VNC viewers, ...: whatever paints the local screen is measured.
 *
 *   x11lat latency <title> <keysym> <trials>   time to first and to final pixels
 *   x11lat fps <title> <keysym> <hz> <secs>   key presses at <hz>: frames shown/s
 *   x11lat open <title> <keysym> <newtitle>   time until a new window is painted
 * Build: cc -O2 -o x11lat x11lat.c -lX11 -lXtst -lXdamage -lXfixes -lm */
#include <X11/Xlib.h>
#include <X11/Xutil.h>
#include <X11/extensions/XTest.h>
#include <X11/extensions/Xdamage.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/select.h>
#include <time.h>
#include <math.h>

static Display *d; static int dmg_ev, dmg_err; static Damage dmg; static double warea = 1;
static double now(void) { struct timespec t; clock_gettime(CLOCK_MONOTONIC, &t); return t.tv_sec * 1e3 + t.tv_nsec / 1e6; }
static Window find(Window w, const char *t) {
  Window r, p, *k; unsigned n, i; Window f = 0; XTextProperty tp;
  /* WM_NAME in any encoding (Qt sets it as UTF8_STRING) */
  XWindowAttributes wa;
  if (w != DefaultRootWindow(d) && (!XGetWindowAttributes(d, w, &wa) || wa.map_state != IsViewable)) return 0;
  if (XGetWMName(d, w, &tp) && tp.value) {
    char buf[512]; size_t len = tp.nitems < sizeof buf - 1 ? tp.nitems : sizeof buf - 1;
    memcpy(buf, tp.value, len); buf[len] = 0; XFree(tp.value);
    if (strstr(buf, t)) return w;
  }
  if (!XQueryTree(d, w, &r, &p, &k, &n)) return 0;
  for (i = 0; i < n && !f; i++) f = find(k[i], t);
  if (k) XFree(k);
  return f;
}
/* Collect damage until `quiet` ms pass with none (or `maxms` total).
 * Returns ms of first and last damage after t0 (-1 if none), area-weighted 90% time. */
static int collect(double t0, double quiet, double maxms, double *first, double *last, double *p90, double *frames_ts, int *nfr) {
  double area = 0, tlast = -1, tfirst = -1; static double ts[200000], ar[200000]; int n = 0;
  for (;;) {
    double tn = now(), wait = quiet - (tn - (tlast < 0 ? t0 : t0 + tlast));
    if (tlast < 0) wait = maxms - (tn - t0);
    if (wait <= 0 || tn - t0 > maxms) break;
    if (!XPending(d)) {
      fd_set f; FD_ZERO(&f); FD_SET(ConnectionNumber(d), &f);
      struct timeval tv = { (long)(wait / 1000), (long)(fmod(wait, 1000) * 1000) };
      if (select(ConnectionNumber(d) + 1, &f, NULL, NULL, &tv) <= 0) break;
    }
    while (XPending(d)) {
      XEvent e; XNextEvent(d, &e);
      if (e.type == dmg_ev + XDamageNotify) {
        XDamageNotifyEvent *de = (XDamageNotifyEvent *)&e; double t = now() - t0;
        double a = (double)de->area.width * de->area.height;
        if (tfirst < 0) tfirst = t; tlast = t; area += a;
        if (n < 200000) { ts[n] = t; ar[n] = a; n++; }
      }
    }
  }
  XDamageSubtract(d, dmg, None, None);
  *first = tfirst; *last = tlast; *p90 = -1;
  /* "full": when the changes so far cover 90% of the window's area (the
   * first complete frame), not counting later touch-ups such as xpra's
   * lossless refresh. Falls back to the last change. */
  double acc = 0; for (int i = 0; i < n; i++) { acc += ar[i]; if (acc >= 0.9 * warea) { *p90 = ts[i]; break; } }
  if (*p90 < 0) *p90 = tlast;
  if (frames_ts) { /* group into frames: a gap > 3 ms starts a new frame */
    int m = 0; for (int i = 0; i < n; i++) if (i == 0 || ts[i] - ts[i - 1] > 3.0) frames_ts[m++] = ts[i];
    *nfr = m;
  }
  return n;
}
static int cmp(const void *a, const void *b) { double x = *(double *)a, y = *(double *)b; return x < y ? -1 : x > y; }
static double pct(double *v, int n, double p) { if (!n) return -1; qsort(v, n, sizeof *v, cmp); int i = (int)ceil(p * n) - 1; if (i < 0) i = 0; return v[i]; }
static void key(KeyCode kc) { XTestFakeKeyEvent(d, kc, True, 0); XTestFakeKeyEvent(d, kc, False, 0); XFlush(d); }
static void focus(Window w) {
  XWindowAttributes a; XGetWindowAttributes(d, w, &a);
  int x, y; Window c; XTranslateCoordinates(d, w, DefaultRootWindow(d), a.width / 2, a.height / 2, &x, &y, &c);
  XTestFakeMotionEvent(d, DefaultScreen(d), x, y, 0);
  XSetInputFocus(d, w, RevertToPointerRoot, CurrentTime); XSync(d, False);
}

/* Windows come and go while we look (dialogs closing): ignore BadWindow. */
static int ignore_errors(Display *dd, XErrorEvent *e) { (void)dd; (void)e; return 0; }

int main(int argc, char **argv) {
  XSetErrorHandler(ignore_errors);
  if (argc < 4) { fprintf(stderr, "usage: see source\n"); return 2; }
  d = XOpenDisplay(NULL); if (!d) { fprintf(stderr, "no display\n"); return 1; }
  if (!XDamageQueryExtension(d, &dmg_ev, &dmg_err)) { fprintf(stderr, "no DAMAGE\n"); return 1; }
  Window w = 0; for (int i = 0; i < 300 && !(w = find(DefaultRootWindow(d), argv[2])); i++) { struct timespec t = {0, 100000000}; nanosleep(&t, NULL); }
  if (!w) { fprintf(stderr, "window '%s' not found\n", argv[2]); return 1; }
  KeyCode kc = XKeysymToKeycode(d, XStringToKeysym(argv[3]));
  dmg = XDamageCreate(d, DefaultRootWindow(d), XDamageReportRawRectangles);
  { XWindowAttributes a; XGetWindowAttributes(d, w, &a); warea = (double)a.width * a.height; }
  focus(w);
  double f, l, p, fr[100000]; int nf;
  collect(now(), 500, 5000, &f, &l, &p, NULL, NULL);   /* let the screen settle */
  if (!strcmp(argv[1], "latency")) {
    int n = atoi(argv[4]); double F[1000], L[1000], P[1000]; int k = 0, miss = 0;
    for (int i = 0; i < n && i < 1000; i++) {
      double t0 = now(); key(kc);
      collect(t0, 400, 10000, &f, &l, &p, NULL, NULL);
      if (f < 0) { miss++; continue; }
      F[k] = f; L[k] = l; P[k] = p; k++;
    }
    printf("first_ms_p50=%.1f first_ms_p90=%.1f full_ms_p50=%.1f full_ms_p90=%.1f last_ms_p50=%.1f n=%d missed=%d\n",
           pct(F, k, .5), pct(F, k, .9), pct(P, k, .5), pct(P, k, .9), pct(L, k, .5), k, miss);
  } else if (!strcmp(argv[1], "fps")) {
    double hz = atof(argv[4]), secs = atof(argv[5]), t0 = now(), next = t0; int sent = 0;
    static double ts[200000], ar[200000]; int n = 0; double lastin = 0;
    while (now() - t0 < secs * 1000) {
      double tn = now();
      if (tn >= next) { key(kc); sent++; lastin = tn - t0; next += 1000.0 / hz; }
      double wait = next - now(); if (wait < 0) wait = 0;
      fd_set fs; FD_ZERO(&fs); FD_SET(ConnectionNumber(d), &fs);
      struct timeval tv = {0, (long)(wait * 1000)};
      if (!XPending(d)) select(ConnectionNumber(d) + 1, &fs, NULL, NULL, &tv);
      while (XPending(d)) { XEvent e; XNextEvent(d, &e);
        if (e.type == dmg_ev + XDamageNotify && n < 200000) { XDamageNotifyEvent *de = (XDamageNotifyEvent *)&e; ts[n] = now() - t0; ar[n] = (double)de->area.width * de->area.height; n++; } }
    }
    collect(now(), 400, 10000, &f, &l, &p, NULL, NULL);
    double drain = l < 0 ? 0 : (now() - t0) - 400 - lastin; /* rough: backlog after the last key */
    /* frames: gap > 3 ms between damage events; skip the first 0.5 s */
    int m = 0; double gaps[200000]; int ng = 0, prev = -1; double prevt = 0;
    for (int i = 0; i < n; i++) if (i == 0 || ts[i] - ts[i - 1] > 3.0) { if (ts[i] > 500) { if (prev >= 0) gaps[ng++] = ts[i] - prevt; prevt = ts[i]; prev = i; m++; } }
    double span = secs * 1000 - 500;
    printf("keys_hz=%.0f frames_per_s=%.1f gap_ms_p50=%.1f gap_ms_p95=%.1f gap_ms_max=%.1f backlog_ms=%.0f\n",
           sent / secs, m / (span / 1000), pct(gaps, ng, .5), pct(gaps, ng, .95), pct(gaps, ng, 1.0), drain);
  } else if (!strcmp(argv[1], "open")) {
    int trials = argc > 5 ? atoi(argv[5]) : 5; double O[100]; int k = 0;
    KeyCode esc = XKeysymToKeycode(d, XStringToKeysym("Escape"));
    for (int i = 0; i < trials && i < 100; i++) {
      focus(w); collect(now(), 400, 3000, &f, &l, &p, NULL, NULL);
      double t0 = now(); key(kc);
      Window nw = 0; while (!(nw = find(DefaultRootWindow(d), argv[4])) && now() - t0 < 20000) { struct timespec t = {0, 1000000}; nanosleep(&t, NULL); }
      if (!nw) { printf("timeout\n"); return 1; }
      collect(t0, 400, 20000, &f, &l, &p, NULL, NULL);
      O[k++] = l;
      focus(nw); key(esc);
      while (find(DefaultRootWindow(d), argv[4]) && now() - t0 < 30000) { struct timespec t = {0, 5000000}; nanosleep(&t, NULL); }
    }
    printf("open_ms_p50=%.0f open_ms_max=%.0f n=%d\n", pct(O, k, .5), pct(O, k, 1.0), k);
  }
  return 0;
}
