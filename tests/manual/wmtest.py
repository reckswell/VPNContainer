# A small window-manager test for gtvpn's viewer (gtvpn vnc <host> windows).
# Run it on the GT server in the session, without installing anything:
#   ssh <host> "DISPLAY=:N python3 -" < tests/manual/wmtest.py
# (it needs python3 with PyQt5, as on ece-linlab19). It opens a main window
# with a menu: a dialog (no maximize or minimize button on Windows), a splash
# screen (no frame), and a window that opens 8 s later (switch to another
# program first: its taskbar button flashes). Double-click the word in the
# text field to select it: your Windows clipboard must not change. Its
# windows have a red-orange icon and a taskbar button of their own
# ("gtvpn-wmtest"), apart from other programs in the session.
import sys, time
from PyQt5 import QtWidgets, QtCore, QtGui
def log(*a): print(time.strftime("%H:%M:%S"), *a, flush=True)
app = QtWidgets.QApplication(sys.argv); app.setApplicationName("gtvpn-wmtest")
ic = QtGui.QPixmap(48, 48); ic.fill(QtGui.QColor("#f53")); app.setWindowIcon(QtGui.QIcon(ic))
w = QtWidgets.QMainWindow(); w.setWindowTitle("gtvpn-test-main"); w.setGeometry(300, 200, 700, 400)
mb = w.menuBar().addMenu("File")
def dlg():
    d = QtWidgets.QDialog(w); d.setWindowTitle("gtvpn-test-dialog"); d.resize(320, 150)
    l = QtWidgets.QVBoxLayout(d); l.addWidget(QtWidgets.QLabel("a dialog"))
    b = QtWidgets.QPushButton("OK"); b.clicked.connect(d.accept); l.addWidget(b)
    d.finished.connect(lambda r: log("dialog closed", r)); d.show(); log("dialog opened")
def splash():
    pm = QtGui.QPixmap(400, 200); pm.fill(QtGui.QColor("#37f"))
    s = QtWidgets.QSplashScreen(pm); s.showMessage("gtvpn-test-splash", QtCore.Qt.AlignCenter, QtCore.Qt.white)
    s.show(); log("splash shown"); QtCore.QTimer.singleShot(6000, lambda: (s.close(), log("splash closed")))
def later():
    log("second window in 8 s: switch to another program now")
    def show():
        t = QtWidgets.QWidget(); t.setWindowTitle("gtvpn-test-later"); t.resize(360, 160); t.show(); w._later = t; log("second window opened")
    QtCore.QTimer.singleShot(8000, show)
mb.addAction("Open dialog", dlg); mb.addAction("Splash", splash); mb.addAction("Window in 8 s", later); mb.addAction("Quit", app.quit)
c = QtWidgets.QWidget(); w.setCentralWidget(c); v = QtWidgets.QVBoxLayout(c)
e = QtWidgets.QLineEdit("gtvpnselectiontest" + str(int(time.time()))); v.addWidget(e)
v.addWidget(QtWidgets.QLabel("Double-click the word above to select it (X's PRIMARY selection)."))
cb = QtWidgets.QApplication.clipboard()
# Only lengths and the test's own marker are printed: what you copied in
# Windows also reaches these selections.
def sel():
    t = cb.text(QtGui.QClipboard.Selection)
    log("PRIMARY changed:", "the test word" if t == e.text() else "other text (length %d)" % len(t))
cb.selectionChanged.connect(sel)
cb.dataChanged.connect(lambda: log("CLIPBOARD changed (length", len(cb.text()), ")"))
w.show(); QtCore.QTimer.singleShot(1500, lambda: log("ready", w.x(), w.y(), w.width(), w.height()))
app.exec_(); log("exit")
