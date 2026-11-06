#!/usr/bin/env python3
"""A Virtuoso-like layout canvas for remote-display benchmarks (PyQt5).

Draws a large, layered, hatch-filled layout (like a Virtuoso layout window)
and redraws all of it on every pan/zoom, the way a layout editor does.
Keys: arrows pan by 3% of the view, +/- zoom, d opens a 40-field form
(like a Virtuoso property form), Escape closes it, a toggles a 180 fps
continuous pan (for frame-rate tests).
Qt5 paints client-side (raster) and ships the result as images, which is
the case remote X11 handles worst. The layout is pre-rendered once per zoom
level, so a pan costs the app ~2 ms and the benchmark measures the display
path, not this script.
"""
import random
import sys

from PyQt5.QtCore import QRectF, Qt, QTimer
from PyQt5.QtGui import QBrush, QColor, QImage, QPainter, QPen
from PyQt5.QtWidgets import (QApplication, QDialog, QFormLayout, QLineEdit,
                             QComboBox, QDialogButtonBox, QScrollArea, QWidget,
                             QVBoxLayout)

LAYERS = [  # name, colour, Qt brush pattern (Virtuoso-style stipples)
    ("nwell", "#2e8b57", Qt.BDiagPattern), ("pdiff", "#d2691e", Qt.FDiagPattern),
    ("ndiff", "#228b22", Qt.DiagCrossPattern), ("poly", "#dc143c", Qt.Dense5Pattern),
    ("cont", "#ffffff", Qt.SolidPattern), ("metal1", "#1e90ff", Qt.Dense6Pattern),
    ("via1", "#ffd700", Qt.CrossPattern), ("metal2", "#da70d6", Qt.BDiagPattern),
    ("via2", "#00ced1", Qt.SolidPattern), ("metal3", "#adff2f", Qt.FDiagPattern),
]


def make_layout(n, seed=1):
    rnd = random.Random(seed)
    shapes = [[] for _ in LAYERS]
    for _ in range(n):
        li = rnd.randrange(len(LAYERS))
        x, y = rnd.uniform(0, 4000), rnd.uniform(0, 4000)
        if li in (4, 6, 8):
            w = h = rnd.uniform(2, 6)
        elif rnd.random() < 0.5:
            w, h = rnd.uniform(40, 400), rnd.uniform(3, 12)
        else:
            w, h = rnd.uniform(3, 12), rnd.uniform(40, 400)
        shapes[li].append(QRectF(x, y, w, h))
    return shapes


class Canvas(QWidget):
    def __init__(self, n):
        super().__init__()
        self.setWindowTitle("perf-canvas")
        self.setFocusPolicy(Qt.StrongFocus)
        self.shapes = make_layout(n)
        self.ox, self.oy, self.scale = 1500.0, 1500.0, 1.2
        self.brushes = [QBrush(QColor(c), p) for _, c, p in LAYERS]
        self.pens = [QPen(QColor(c), 0) for _, c, _ in LAYERS]
        self.timer = QTimer(self, interval=5, timeout=self.step)
        self.dialog = None
        self.cache, self.cache_scale = None, None

    def render_cache(self):
        # the region panning wraps over: 500..2500 units plus one view
        w = int((2000 + 1700 / self.scale) * self.scale)
        h = int((2000 + 1100 / self.scale) * self.scale)
        img = QImage(w, h, QImage.Format_RGB32)
        p = QPainter(img)
        p.fillRect(0, 0, w, h, Qt.black)
        p.scale(self.scale, self.scale)
        p.translate(-500, -500)
        view = QRectF(500, 500, w / self.scale, h / self.scale)
        for li, rects in enumerate(self.shapes):
            p.setPen(self.pens[li]); p.setBrush(self.brushes[li])
            p.drawRects([r for r in rects if r.intersects(view)])
        p.setPen(QPen(QColor("#555555"), 0))
        for x in range(500, int(view.right()), 50):
            for y in range(500, int(view.bottom()), 50):
                p.drawPoint(x, y)
        p.end()
        self.cache, self.cache_scale = img, self.scale

    def wrap(self):
        # keep the view over the dense part of the layout while panning
        self.ox = 500 + (self.ox - 500) % 2000
        self.oy = 500 + (self.oy - 500) % 2000

    def step(self):
        self.ox += 4 / self.scale
        self.wrap()
        self.update()

    def keyPressEvent(self, e):
        k, s = e.key(), self.width() * 0.03 / self.scale
        if k == Qt.Key_Right: self.ox += s
        elif k == Qt.Key_Left: self.ox -= s
        elif k == Qt.Key_Up: self.oy -= s
        elif k == Qt.Key_Down: self.oy += s
        elif k in (Qt.Key_Plus, Qt.Key_Equal): self.scale *= 1.15
        elif k == Qt.Key_Minus: self.scale /= 1.15
        elif k == Qt.Key_A:
            self.timer.stop() if self.timer.isActive() else self.timer.start()
            return
        elif k == Qt.Key_D:
            self.open_form()
            return
        else:
            return super().keyPressEvent(e)
        self.wrap()
        self.update()

    def open_form(self):
        d = QDialog(self)
        d.setAttribute(Qt.WA_DeleteOnClose)
        d.setWindowTitle("perf-form")
        inner, form = QWidget(), QFormLayout()
        for i in range(40):
            if i % 4 == 3:
                c = QComboBox(); c.addItems(["none", "M1", "M2", "M3", "poly"]); form.addRow(f"Parameter {i}", c)
            else:
                form.addRow(f"Parameter {i}", QLineEdit(f"{i * 0.18:.2f}u"))
        inner.setLayout(form)
        sa = QScrollArea(); sa.setWidget(inner); sa.setWidgetResizable(True)
        lay = QVBoxLayout(d); lay.addWidget(sa)
        bb = QDialogButtonBox(QDialogButtonBox.Ok | QDialogButtonBox.Cancel)
        bb.accepted.connect(d.accept); bb.rejected.connect(d.reject); lay.addWidget(bb)
        d.resize(520, 700)
        d.show()
        self.dialog = d

    def paintEvent(self, _):
        if self.cache_scale != self.scale:
            self.render_cache()
        p = QPainter(self)
        sx, sy = int((self.ox - 500) * self.scale), int((self.oy - 500) * self.scale)
        p.drawImage(0, 0, self.cache, sx, sy, self.width(), self.height())


if __name__ == "__main__":
    n = int(sys.argv[1]) if len(sys.argv) > 1 else 30000
    app = QApplication(sys.argv)
    w = Canvas(n)
    w.resize(1600, 1000)
    w.show()
    sys.exit(app.exec_())
