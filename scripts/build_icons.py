"""Render the original vector brand mark into the Windows build assets."""
from __future__ import annotations

import os
from pathlib import Path
import struct

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")
from PySide6.QtCore import QByteArray, QBuffer, QIODevice, QRectF
from PySide6.QtGui import QGuiApplication, QImage, QPainter
from PySide6.QtSvg import QSvgRenderer


ROOT = Path(__file__).resolve().parent.parent


def main() -> None:
    app = QGuiApplication.instance() or QGuiApplication([])
    renderer = QSvgRenderer(str(ROOT / "assets" / "zhehao-icon.svg"))
    if not renderer.isValid():
        raise RuntimeError("Brand SVG could not be loaded.")
    pngs = []
    output = ROOT / "assets"
    for size in (16, 20, 24, 32, 40, 48, 64, 128, 256, 512):
        image = QImage(size, size, QImage.Format.Format_ARGB32)
        image.fill(0)
        painter = QPainter(image)
        renderer.render(painter, QRectF(0, 0, size, size))
        painter.end()
        array = QByteArray()
        buffer = QBuffer(array)
        buffer.open(QIODevice.OpenModeFlag.WriteOnly)
        image.save(buffer, "PNG")
        payload = bytes(array)
        (output / f"zhehao-{size}.png").write_bytes(payload)
        if size <= 256:
            pngs.append((size, payload))
    directory = bytearray(struct.pack("<HHH", 0, 1, len(pngs)))
    offset = 6 + 16 * len(pngs)
    body = bytearray()
    for size, payload in pngs:
        directory.extend(struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(payload), offset))
        body.extend(payload)
        offset += len(payload)
    (output / "zhehao.ico").write_bytes(directory + body)
    print("Created Zhehao SVG, PNG and ICO assets.")


if __name__ == "__main__":
    main()
