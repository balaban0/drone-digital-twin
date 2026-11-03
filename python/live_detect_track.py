"""
Live frame stream -> reference object detection + tracking -> UDP JSON to Unity.

Frame source (--source):
  udp        : JPEG frames from the phone (FRAME_PORT)
  <file>     : video file (test without a drone)
  0          : webcam (test without a drone)

Frame packet (phone -> PC), one UDP datagram:
  [frame_id: uint32][timestamp_ms: uint64][JPEG bytes]   (big-endian)
  JPEG should stay under ~60 KB (640x360 at quality ~60 is usually enough).

Result packet (PC -> Unity):
  {"frame_id", "ts", "w", "h", "objects": [{"id","label","score","bbox":[x,y,w,h]}]}
  bbox normalized to 0..1 (top-left corner, width, height).

Requires: pip install opencv-contrib-python numpy
"""
import argparse
import json
import socket
import struct
import threading
import time
from dataclasses import dataclass

import cv2
import numpy as np

FRAME_PORT = 5006
UNITY_ADDR = ("127.0.0.1", 5007)  # address/port Unity LiveDetections listens on
DETECT_EVERY_N = 15              # periodic re-detection while tracking (frames)
MAX_MISSES = 3                   # drop the track after this many failed re-detections in a row
MIN_SCORE = 0.5
SHOW = True

HEADER = struct.Struct("!IQ")


@dataclass
class Frame:
    frame_id: int
    ts_ms: int
    image: np.ndarray


# ---------------- Frame sources ----------------
class UdpFrameSource:
    """Always keeps the NEWEST frame; if processing falls behind, old frames are dropped (no lag build-up)."""

    def __init__(self, port):
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.sock.bind(("0.0.0.0", port))
        self.sock.settimeout(0.5)
        self._latest = None
        self._lock = threading.Lock()
        threading.Thread(target=self._run, daemon=True).start()

    def _run(self):
        while True:
            try:
                data, _ = self.sock.recvfrom(65535)
            except socket.timeout:
                continue
            if len(data) <= HEADER.size:
                continue
            fid, ts = HEADER.unpack_from(data)
            img = cv2.imdecode(
                np.frombuffer(data, dtype=np.uint8, offset=HEADER.size), cv2.IMREAD_COLOR)
            if img is None:
                continue
            with self._lock:
                self._latest = Frame(fid, ts, img)

    def read(self):
        with self._lock:
            f, self._latest = self._latest, None
        return f


class VideoFileSource:
    def __init__(self, src):
        self.cap = cv2.VideoCapture(src)
        self.delay = 1.0 / (self.cap.get(cv2.CAP_PROP_FPS) or 30)
        self.fid = 0
        self._next = time.time()

    def read(self):
        now = time.time()
        if now < self._next:
            return None
        ok, img = self.cap.read()
        if not ok:
            raise SystemExit("Video ended / could not be opened.")
        self._next = now + self.delay
        self.fid += 1
        return Frame(self.fid, int(now * 1000), img)


# ---------------- Detection ----------------
class ReferenceDetector:
    """Reference object detection for live mode. Not included in this repository."""

    def __init__(self, reference_image_path):
        self.reference_image_path = reference_image_path

    def detect(self, image):
        """-> [(x, y, w, h, score), ...]  (in pixels)"""
        raise NotImplementedError("Plug your detection code in here.")


class ManualDetector:
    """For testing: pick the object by hand in the first frame, mimics a detection."""

    def __init__(self):
        self.done = False

    def detect(self, image):
        if self.done:
            return []
        win = "first frame: select the object, ENTER"
        x, y, w, h = cv2.selectROI(win, image, False)
        cv2.destroyWindow(win)
        self.done = True
        return [(x, y, w, h, 1.0)] if w > 0 and h > 0 else []


def make_tracker():
    if hasattr(cv2, "TrackerCSRT_create"):
        return cv2.TrackerCSRT_create()
    if hasattr(cv2, "legacy") and hasattr(cv2.legacy, "TrackerCSRT_create"):
        return cv2.legacy.TrackerCSRT_create()
    raise RuntimeError("CSRT needs opencv-contrib-python.")


# ---------------- Main loop ----------------
def main(source, detector):
    out = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    tracker, box, score = None, None, 0.0
    since_detect, misses = 0, 0

    while True:
        frame = source.read()
        if frame is None:
            time.sleep(0.002)
            continue
        img = frame.image
        h, w = img.shape[:2]

        if tracker is not None:
            ok, b = tracker.update(img)
            if ok:
                box = b
                since_detect += 1
            else:
                tracker = None

        if tracker is None or since_detect >= DETECT_EVERY_N:
            dets = [d for d in detector.detect(img) if d[4] >= MIN_SCORE]
            if dets:
                x, y, bw, bh, score = max(dets, key=lambda d: d[4])
                tracker = make_tracker()
                tracker.init(img, (int(x), int(y), int(bw), int(bh)))
                box = (x, y, bw, bh)
                misses = 0
            elif tracker is not None:
                misses += 1
                if misses >= MAX_MISSES:
                    tracker = None  # may be drifting, let it go
            since_detect = 0

        objects = []
        if tracker is not None and box is not None:
            x, y, bw, bh = box
            objects.append({
                "id": 0, "label": "reference_object", "score": float(score),
                "bbox": [x / w, y / h, bw / w, bh / h],
            })

        # Send the empty list too, so Unity clears the old marker
        pkt = {"frame_id": frame.frame_id, "ts": frame.ts_ms, "w": w, "h": h, "objects": objects}
        out.sendto(json.dumps(pkt).encode("utf-8"), UNITY_ADDR)

        if SHOW:
            if objects:
                x, y, bw, bh = [int(v) for v in box]
                cv2.rectangle(img, (x, y), (x + bw, y + bh), (0, 255, 0), 2)
            cv2.imshow("live", img)
            if cv2.waitKey(1) & 0xFF == ord("q"):
                break


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--source", default="udp", help="udp | video file | 0 (webcam)")
    ap.add_argument("--manual", action="store_true", help="pick the object by hand in the first frame (test)")
    ap.add_argument("--reference", default="reference.jpg")
    args = ap.parse_args()

    if args.source == "udp":
        src = UdpFrameSource(FRAME_PORT)
    else:
        src = VideoFileSource(int(args.source) if args.source.isdigit() else args.source)

    det = ManualDetector() if args.manual else ReferenceDetector(args.reference)
    main(src, det)
