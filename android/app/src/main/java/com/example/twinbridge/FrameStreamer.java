package com.example.twinbridge;

import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.SurfaceTexture;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;
import android.view.TextureView;

import java.io.ByteArrayOutputStream;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.nio.ByteBuffer;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.RejectedExecutionException;
import java.util.concurrent.atomic.AtomicBoolean;

import dji.sdk.camera.VideoFeeder;
import dji.sdk.codec.DJICodecManager;

/**
 * Drone camera -> DJICodecManager -> TextureView (live view on the phone)
 *              -> 640x360 copy via getBitmap -> JPEG -> UDP (port 5006) -> python/live_detect_track.py.
 *
 * Packet: [frame_id: uint32][ts_ms: uint64][JPEG]  (big-endian, one datagram). See docs/protocol.md.
 *
 * VideoFeeder / sendDataToDecoder / cleanSurface / destroyCodec are used the same way as in the DJI sample
 * project's BaseCameraView and VideoFeedView. The YUV callback is deliberately not used: when enabled, DJI
 * stops drawing to the screen; copying from the screen keeps the view on the phone and avoids colour-format issues.
 */
public class FrameStreamer implements TextureView.SurfaceTextureListener {
    private static final String TAG = "FrameStreamer";
    private static final int OUT_WIDTH = 640, OUT_HEIGHT = 360; // TextureView 16:9 (activity_main.xml)
    private static final long INTERVAL_MS = 100;    // at most ~10 frames/s
    private static final long VIDEO_TIMEOUT_MS = 1000; // no video for this long: don't send (black) frames
    private static final int JPEG_QUALITY = 60;
    private static final int MAX_DATAGRAM = 65000;   // UDP limit is 65507; header is 12 bytes

    private final Context ctx;
    private final TextureView view;
    private final int port;
    private final ExecutorService io = Executors.newSingleThreadExecutor();
    private final Handler ui = new Handler(Looper.getMainLooper());
    private final AtomicBoolean encoding = new AtomicBoolean(false);
    // A single bitmap is reused: it is not refilled while 'encoding' is true
    private final Bitmap frame = Bitmap.createBitmap(OUT_WIDTH, OUT_HEIGHT, Bitmap.Config.ARGB_8888);
    private DatagramSocket socket;
    private InetAddress target;
    private volatile DJICodecManager codec; // read from the UI and video threads
    private volatile boolean closed = false;
    private volatile long lastVideoMs = 0;
    private boolean capturing = false;
    private int frameId = 0;

    private final VideoFeeder.VideoDataListener videoListener = (buf, size) -> {
        DJICodecManager c = codec;
        if (c != null) {
            c.sendDataToDecoder(buf, size);
            lastVideoMs = System.currentTimeMillis();
        }
    };

    private final Runnable captureTick = new Runnable() {
        @Override public void run() {
            if (!capturing) return;
            capture();
            ui.postDelayed(this, INTERVAL_MS);
        }
    };

    /** Create on the UI thread. */
    public FrameStreamer(Context ctx, TextureView surface, String host, int port) {
        this.ctx = ctx.getApplicationContext();
        this.view = surface;
        this.port = port;
        io.execute(() -> {
            try {
                target = InetAddress.getByName(host);
                socket = new DatagramSocket();
            } catch (Exception e) {
                Log.e(TAG, "Could not open socket", e);
            }
        });
        surface.setSurfaceTextureListener(this);
        if (surface.isAvailable()) {
            onSurfaceTextureAvailable(surface.getSurfaceTexture(), surface.getWidth(), surface.getHeight());
        }
    }

    /** Call on the UI thread once the product is connected. */
    public void attach() {
        detach();
        VideoFeeder.getInstance().getPrimaryVideoFeed().addVideoDataListener(videoListener);
        capturing = true;
        ui.postDelayed(captureTick, INTERVAL_MS);
    }

    public void detach() {
        capturing = false;
        ui.removeCallbacks(captureTick);
        try {
            VideoFeeder.getInstance().getPrimaryVideoFeed().removeVideoDataListener(videoListener);
        } catch (Exception ignored) {
        }
    }

    public void close() {
        closed = true;
        detach();
        releaseCodec();
        try {
            io.execute(() -> { if (socket != null) socket.close(); });
        } catch (RejectedExecutionException ignored) {
        }
        io.shutdown();
    }

    @Override
    public void onSurfaceTextureAvailable(SurfaceTexture surface, int width, int height) {
        if (codec != null || closed) return; // a closed streamer must not open a codec when the surface returns
        codec = new DJICodecManager(ctx, surface, width, height);
    }

    @Override
    public boolean onSurfaceTextureDestroyed(SurfaceTexture surface) {
        releaseCodec();
        return false;
    }

    @Override public void onSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height) { }
    @Override public void onSurfaceTextureUpdated(SurfaceTexture surface) { }

    private void releaseCodec() {
        DJICodecManager c = codec;
        codec = null;
        if (c != null) {
            c.cleanSurface();
            c.destroyCodec(); // stop the decoder thread (same as VideoFeedView in the sample project)
        }
    }

    /** On the UI thread: take a small copy of the on-screen video, hand encoding to the io thread. */
    private void capture() {
        long now = System.currentTimeMillis();
        if (closed || codec == null || !view.isAvailable()) return;
        if (now - lastVideoMs > VIDEO_TIMEOUT_MS) return;    // video not flowing: don't send black frames
        if (!encoding.compareAndSet(false, true)) return;     // previous frame still encoding: drop this one
        try {
            view.getBitmap(frame); // scaled to the bitmap size (640x360); UI widgets on top are not included
            int id = frameId++;
            io.execute(() -> {
                try {
                    send(id, now);
                } finally {
                    encoding.set(false);
                }
            });
        } catch (RejectedExecutionException e) {
            encoding.set(false);
        } catch (Exception e) {
            encoding.set(false);
            Log.e(TAG, "Could not grab frame", e);
        }
    }

    private void send(int id, long tsMs) {
        if (socket == null || target == null) return;
        try {
            ByteArrayOutputStream jpeg = new ByteArrayOutputStream(64 * 1024);
            frame.compress(Bitmap.CompressFormat.JPEG, JPEG_QUALITY, jpeg);
            if (jpeg.size() + 12 > MAX_DATAGRAM) { // detailed scene: retry once at lower quality
                jpeg.reset();
                frame.compress(Bitmap.CompressFormat.JPEG, JPEG_QUALITY - 25, jpeg);
                if (jpeg.size() + 12 > MAX_DATAGRAM) return;
            }
            ByteBuffer pkt = ByteBuffer.allocate(12 + jpeg.size()); // big-endian by default
            pkt.putInt(id).putLong(tsMs).put(jpeg.toByteArray());
            socket.send(new DatagramPacket(pkt.array(), pkt.position(), target, port));
        } catch (Exception e) {
            Log.e(TAG, "Could not send frame", e);
        }
    }
}
