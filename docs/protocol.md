# Packet protocol

All packets travel over UDP. Each datagram carries a single message.

```
Phone (android/)                             PC
 ├─ TelemetryStreamer ── JSON ── :5005 ──▶ Unity UdpTwin
 └─ FrameStreamer ────── JPEG ── :5006 ──▶ python/live_detect_track.py
                                                └─ JSON ── 127.0.0.1:5007 ──▶ Unity LiveDetections
```

| Port | Sender | Receiver | Network |
|------|--------|----------|---------|
| 5005 | Phone `TelemetryStreamer` (test: `python/telemetry_sender.py`) | Unity `UdpTwin` | Wi-Fi, needs an inbound firewall rule |
| 5006 | Phone `FrameStreamer` | `python/live_detect_track.py` | Wi-Fi, needs an inbound firewall rule |
| 5007 | `live_detect_track.py` | Unity `LiveDetections` | localhost only |

## Clock

`ts_ms` and `ts` are the phone's `System.currentTimeMillis()` (Unix epoch, ms). Telemetry and frames are stamped from the same clock, so Unity can map a detection to where the drone was at that moment via `ts` (`UdpTwin.TryGetPositionAt`).

`t` is only used for the HUD and logging: it is the seconds since the first packet and is not used for matching.

When testing without a drone, `telemetry_sender.py` and `live_detect_track.py --source <video|0>` both use the PC's clock, so matching still works.

## 1. Telemetry: phone → Unity (5005)

UTF-8 JSON, about 10 Hz:

```json
{"t": 12.3, "ts_ms": 1767225600123, "lat": 36.8969, "lon": 30.7133, "alt": 20.5,
 "heading": 91.5, "pitch": -8.0, "roll": 15.0, "speed": 4.2,
 "battery": 87, "sats": 15, "mode": "P-GPS", "message": ""}
```

| Field | Unit / meaning |
|-------|----------------|
| `t` | time since the first packet (s) |
| `ts_ms` | phone clock (ms). If `0` or missing, Unity puts detections at the drone's current position |
| `lat`, `lon` | WGS84 degrees. The first packet becomes the local origin in Unity; no packets are sent without a GPS fix (0,0 or NaN) |
| `alt` | height above the takeoff point (m) |
| `heading` | 0..360°, north = 0 |
| `pitch`, `roll` | degrees |
| `speed` | horizontal speed (m/s) |
| `battery` | %, `-1` = not known yet (HUD shows `--`) |
| `sats` | satellite count |
| `mode`, `message` | free text |

## 2. Frame: phone → Python (5006)

One datagram, big-endian:

```
[frame_id: uint32][ts_ms: uint64][JPEG bytes]
```

- 640×360, JPEG quality 60, at most ~10 frames/s. The frame is a downscaled copy of the video shown on the phone (`TextureView.getBitmap`). The live stream source is 720p.
- If the datagram exceeds 65000 bytes, quality drops to 35; if it still does not fit, the frame is skipped.
- No frames are sent if no video arrived in the last second, so black frames are not sent.
- Python always processes the newest frame; older frames are dropped.
- `ts_ms` is when the frame was grabbed from the screen, roughly 100-200 ms after the actual capture.

## 3. Detection: Python → Unity (5007)

UTF-8 JSON, one packet per processed frame. With no object, `objects` is sent as an empty list:

```json
{"frame_id": 42, "ts": 1767225600123, "w": 640, "h": 360,
 "objects": [{"id": 0, "label": "reference_object", "score": 0.91,
              "bbox": [0.41, 0.33, 0.12, 0.20]}]}
```

| Field | Meaning |
|-------|---------|
| `ts` | the frame's `ts_ms`, passed through unchanged |
| `bbox` | `[x, y, w, h]`, normalized to 0..1, from the top-left corner |
| `label` | Unity colours are in `DetectionMarkers.ColorFor`: `reference_object`, `person`, `vehicle`; others are white |

Marker behaviour in Unity: if the drone has not moved `minSpacing` (3 m) from the last marker with the same label, no new marker is created and the existing marker's score is updated. The banner only appears when the object shows up again.

## Replay files (not UDP)

- `FlightReplayer`: reads a DJI `*-aircraft.csv` or an Airdata CSV. Column names are in `FlightLog.cs`. Example: `unity/Assets/Data/test_flight.csv`.
- `DetectionMarkers`: `{"detections": [{"t", "label", "confidence"}]}`, where `t` is seconds from the start of the replay. Example: `unity/Assets/Data/detections.json` (fake data).
