"""Streams a DJI Airdata CSV over UDP as JSON, at the log's real-time rate.
Usage: python telemetry_sender.py Oren1.csv [--host 127.0.0.1] [--port 5005] [--speed 1] [--loop]
"""
import argparse, csv, json, socket, time

FT, MPH = 0.3048, 0.44704

def f(row, key, default=0.0):
    try: return float(row[key].strip())
    except (KeyError, ValueError): return default

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("csv"); ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=5005)
    ap.add_argument("--speed", type=float, default=1.0)
    ap.add_argument("--loop", action="store_true")
    a = ap.parse_args()

    with open(a.csv, encoding="utf-8-sig", newline="") as fh:
        rows = [{k.strip(): v for k, v in r.items()} for r in csv.DictReader(fh)]
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    addr = (a.host, a.port)
    print(f"{len(rows)} samples -> {addr}, speed x{a.speed}")

    while True:
        t0 = f(rows[0], "time(millisecond)") / 1000.0
        start = time.perf_counter()
        for r in rows:
            t = f(r, "time(millisecond)") / 1000.0 - t0
            delay = t / a.speed - (time.perf_counter() - start)
            if delay > 0: time.sleep(delay)
            msg = {
                "t": t, "ts_ms": int(time.time() * 1000),  # like the real phone: clock at send time
                "lat": f(r, "latitude"), "lon": f(r, "longitude"),
                "alt": f(r, "height_above_takeoff(feet)") * FT,
                "heading": f(r, "compass_heading(degrees)"),
                "pitch": f(r, "pitch(degrees)"), "roll": f(r, "roll(degrees)"),
                "speed": f(r, "speed(mph)") * MPH,
                "battery": f(r, "battery_percent"), "sats": int(f(r, "satellites")),
                "mode": r.get("flycState", ""), "message": r.get("message", ""),
            }
            sock.sendto(json.dumps(msg).encode("utf-8"), addr)
        if not a.loop: break
        print("loop")

if __name__ == "__main__":
    main()
