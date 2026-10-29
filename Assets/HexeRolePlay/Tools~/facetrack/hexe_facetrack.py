"""Webcam face tracking for the Hexe role-play, streamed to Unity as ARKit blendshapes plus
head rotation over UDP. Unity records it while the visitor reads, and the replay film puts
their expressions and head movement on the avatar playing Hansel. No image ever leaves this
process - only the 52 weights and three angles.

UDP rather than HTTP because this is a continuous realtime signal: a dropped frame is fine,
the next one is 33 ms behind (the same reason VMC and iFacialMocap use UDP). A small HTTP
endpoint answers /health so start_services.ps1 can tell the tracker is up.

    python hexe_facetrack.py                     # default camera, UDP 39540, health on :8910
    python hexe_facetrack.py --camera 1 --preview

Payload, one JSON object per frame:

    {"t": 1234.56, "bs": {"eyeBlinkLeft": 0.02, ...}, "head": [pitch, yaw, roll]}
"""
import argparse
import json
import math
import socket
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import cv2
import mediapipe as mp
from mediapipe.tasks import python as mp_python
from mediapipe.tasks.python import vision

MODEL_PATH = Path(__file__).with_name("face_landmarker.task")

# Below this a channel is treated as zero and left out, which keeps a packet well inside
# one datagram without losing any visible expression.
EPSILON = 0.012

state = {"sent": 0, "face": False, "last_face": 0.0}


def head_angles(matrix):
    """Pitch, yaw, roll in degrees from MediaPipe's face transformation matrix."""
    r = matrix
    pitch = math.degrees(math.atan2(r[2][1], r[2][2]))
    yaw = math.degrees(math.atan2(-r[2][0], math.hypot(r[2][1], r[2][2])))
    roll = math.degrees(math.atan2(r[1][0], r[0][0]))
    return [round(pitch, 2), round(yaw, 2), round(roll, 2)]


class Health(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path != "/health":
            self.send_response(404)
            self.end_headers()
            return
        body = json.dumps({"status": "ok", "face": time.time() - state["last_face"] < 1.0}).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args):
        pass


def main():
    ap = argparse.ArgumentParser(description="Webcam -> ARKit blendshapes + head pose over UDP")
    ap.add_argument("--camera", type=int, default=0)
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=39540)
    ap.add_argument("--health-port", type=int, default=8910)
    ap.add_argument("--width", type=int, default=640)
    ap.add_argument("--height", type=int, default=480)
    ap.add_argument("--preview", action="store_true", help="show the camera feed")
    args = ap.parse_args()

    if not MODEL_PATH.exists():
        raise SystemExit(f"Model not found at {MODEL_PATH} - run setup.ps1.")

    server = ThreadingHTTPServer(("127.0.0.1", args.health_port), Health)
    threading.Thread(target=server.serve_forever, daemon=True).start()

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    addr = (args.host, args.port)

    def on_result(result, output_image, timestamp_ms):
        if not result.face_blendshapes:
            return
        payload = {
            c.category_name: round(c.score, 4)
            for c in result.face_blendshapes[0]
            if c.score > EPSILON and c.category_name != "_neutral"
        }
        packet = {"t": timestamp_ms / 1000.0, "bs": payload}
        if result.facial_transformation_matrixes:
            packet["head"] = head_angles(result.facial_transformation_matrixes[0])
        sock.sendto(json.dumps(packet).encode(), addr)
        state["sent"] += 1
        state["last_face"] = time.time()

    options = vision.FaceLandmarkerOptions(
        base_options=mp_python.BaseOptions(model_asset_path=str(MODEL_PATH)),
        output_face_blendshapes=True,
        output_facial_transformation_matrixes=True,
        running_mode=vision.RunningMode.LIVE_STREAM,
        num_faces=1,
        result_callback=on_result,
    )
    landmarker = vision.FaceLandmarker.create_from_options(options)

    cap = cv2.VideoCapture(args.camera, cv2.CAP_DSHOW)
    cap.set(cv2.CAP_PROP_FRAME_WIDTH, args.width)
    cap.set(cv2.CAP_PROP_FRAME_HEIGHT, args.height)
    if not cap.isOpened():
        raise SystemExit(f"Could not open camera {args.camera}.")

    print(f"Streaming to {args.host}:{args.port}, health on http://127.0.0.1:{args.health_port}/health")
    start = time.time()
    last_report = start
    try:
        while True:
            ok, frame = cap.read()
            if not ok:
                continue
            # Mirrored, so moving right on screen matches moving right in the room.
            frame = cv2.flip(frame, 1)
            rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
            landmarker.detect_async(mp.Image(image_format=mp.ImageFormat.SRGB, data=rgb), int((time.time() - start) * 1000))

            now = time.time()
            if now - last_report >= 5.0:
                rate = state["sent"] / (now - last_report)
                print(f"  {rate:5.1f} frames/s   face {'tracked' if now - state['last_face'] < 1 else 'NOT found'}")
                state["sent"] = 0
                last_report = now

            if args.preview:
                cv2.imshow("hexe facetrack (esc to quit)", frame)
                if cv2.waitKey(1) == 27:
                    break
    except KeyboardInterrupt:
        pass
    finally:
        cap.release()
        if args.preview:
            cv2.destroyAllWindows()
        landmarker.close()
        sock.close()
        server.shutdown()


if __name__ == "__main__":
    main()
