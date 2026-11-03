package com.example.twinbridge;

import android.util.Log;

import org.json.JSONObject;

import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.RejectedExecutionException;

import dji.common.flightcontroller.Attitude;
import dji.common.flightcontroller.FlightControllerState;
import dji.common.flightcontroller.LocationCoordinate3D;
import dji.sdk.battery.Battery;
import dji.sdk.flightcontroller.FlightController;
import dji.sdk.products.Aircraft;

/**
 * DJI Mobile SDK 4 (Phantom 3 Professional) -> UDP JSON -> Unity UdpTwin (port 5005).
 *
 * Packet: {t, ts_ms, lat, lon, alt, heading, pitch, roll, speed, battery, sats, mode, message}
 * Details: docs/protocol.md. SDK registration happens in MainActivity; this class only
 * listens to the callbacks of the connected Aircraft.
 */
public class TelemetryStreamer {
    private static final String TAG = "TelemetryStreamer";
    private static final long MIN_INTERVAL_MS = 100; // the SDK delivers at most ~10 Hz

    private final ExecutorService io = Executors.newSingleThreadExecutor();
    private final int port;
    private DatagramSocket socket;
    private InetAddress target;
    private Aircraft aircraft;

    private long startMs = -1;
    private long lastSendMs = 0;
    private volatile int batteryPercent = -1; // -1 = unknown until the first battery callback

    /** host = the PC's local IP (e.g. "192.168.1.20"). */
    public TelemetryStreamer(String host, int port) {
        this.port = port;
        io.execute(() -> {
            try {
                target = InetAddress.getByName(host);
                socket = new DatagramSocket();
            } catch (Exception e) {
                Log.e(TAG, "Could not open socket", e);
            }
        });
    }

    public void attach(Aircraft aircraft) {
        detach();
        this.aircraft = aircraft;
        Battery battery = aircraft.getBattery();
        if (battery != null) {
            battery.setStateCallback(s -> batteryPercent = s.getChargeRemainingInPercent());
        }
        FlightController fc = aircraft.getFlightController();
        if (fc != null) fc.setStateCallback(this::onFlightState);
    }

    public void detach() {
        if (aircraft == null) return;
        if (aircraft.getBattery() != null) aircraft.getBattery().setStateCallback(null);
        if (aircraft.getFlightController() != null) aircraft.getFlightController().setStateCallback(null);
        aircraft = null;
    }

    public void close() {
        detach();
        io.execute(() -> { if (socket != null) socket.close(); });
        io.shutdown();
    }

    private void onFlightState(FlightControllerState st) {
        long now = System.currentTimeMillis();
        if (now - lastSendMs < MIN_INTERVAL_MS) return;

        LocationCoordinate3D loc = st.getAircraftLocation();
        if (loc == null) return;
        double lat = loc.getLatitude();
        double lon = loc.getLongitude();
        double alt = loc.getAltitude(); // relative to the takeoff point (m)

        // Do NOT send without a GPS fix: UdpTwin makes the first packet the local origin,
        // and an invalid first packet such as (0,0) breaks the scene.
        if (Double.isNaN(lat) || Double.isNaN(lon) || (lat == 0 && lon == 0)) return;
        if (Double.isNaN(alt)) alt = 0;

        lastSendMs = now;
        if (startMs < 0) startMs = now;

        Attitude att = st.getAttitude();
        double heading = ((att.yaw % 360) + 360) % 360;           // -180..180 -> 0..360
        double speed = Math.hypot(st.getVelocityX(), st.getVelocityY()); // horizontal speed (m/s)

        try {
            JSONObject j = new JSONObject();
            j.put("t", (now - startMs) / 1000.0);
            j.put("ts_ms", now); // same clock as the ts in FrameStreamer's frame header
            j.put("lat", lat);
            j.put("lon", lon);
            j.put("alt", alt);
            j.put("heading", heading);
            j.put("pitch", att.pitch);
            j.put("roll", att.roll);
            j.put("speed", speed);
            j.put("battery", batteryPercent);
            j.put("sats", st.getSatelliteCount());
            j.put("mode", st.getFlightModeString());
            j.put("message", ""); // put warning texts here if needed
            send(j.toString());
        } catch (Exception e) {
            Log.e(TAG, "JSON error", e);
        }
    }

    private void send(String json) {
        if (io.isShutdown()) return;
        try {
            io.execute(() -> {
                if (socket == null || target == null) return;
                try {
                    byte[] b = json.getBytes(StandardCharsets.UTF_8);
                    socket.send(new DatagramPacket(b, b.length, target, port));
                } catch (Exception e) {
                    Log.e(TAG, "Could not send UDP", e);
                }
            });
        } catch (RejectedExecutionException ignored) {
            // close() raced with an SDK callback: drop the packet
        }
    }
}
