package com.example.twinbridge;

import android.Manifest;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.hardware.usb.UsbManager;
import android.os.Bundle;
import android.util.Log;
import android.view.TextureView;
import android.widget.Button;
import android.widget.EditText;
import android.widget.TextView;

import androidx.annotation.NonNull;
import androidx.appcompat.app.AppCompatActivity;
import androidx.core.app.ActivityCompat;
import androidx.core.content.ContextCompat;

import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.atomic.AtomicBoolean;

import dji.common.error.DJIError;
import dji.common.error.DJISDKError;
import dji.sdk.base.BaseComponent;
import dji.sdk.base.BaseProduct;
import dji.sdk.products.Aircraft;
import dji.sdk.sdkmanager.DJISDKInitEvent;
import dji.sdk.sdkmanager.DJISDKManager;

/**
 * Single screen: PC IP + Start. The SDK is registered here; once the product connects,
 * TelemetryStreamer (5005) and FrameStreamer (5006) attach to the drone.
 */
public class MainActivity extends AppCompatActivity {
    private static final String TAG = "TwinBridge";
    private static final int TELEMETRY_PORT = 5005;
    private static final int FRAME_PORT = 5006;
    private static final int REQ_PERMISSIONS = 1;
    private static final String[] PERMISSIONS = {
            Manifest.permission.ACCESS_FINE_LOCATION,
            Manifest.permission.ACCESS_COARSE_LOCATION,
            Manifest.permission.READ_PHONE_STATE,
            Manifest.permission.WRITE_EXTERNAL_STORAGE,
            Manifest.permission.READ_EXTERNAL_STORAGE,
    };

    private final AtomicBoolean registrationStarted = new AtomicBoolean(false);
    private EditText hostEdit;
    private Button startButton;
    private TextView statusText;
    private TextureView videoSurface;
    private SharedPreferences prefs;

    private TelemetryStreamer telemetry;
    private FrameStreamer frames;
    private String sdkStatus = "SDK: waiting for registration";

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_main);
        hostEdit = findViewById(R.id.host);
        startButton = findViewById(R.id.start);
        statusText = findViewById(R.id.status);
        videoSurface = findViewById(R.id.video_surface);

        prefs = getSharedPreferences("twin", MODE_PRIVATE);
        hostEdit.setText(prefs.getString("host", ""));
        startButton.setOnClickListener(v -> { if (telemetry == null) startStreaming(); else stopStreaming(); });
        refreshStatus();

        requestPermissionsThenRegister();
    }

    @Override
    protected void onDestroy() {
        stopStreaming();
        super.onDestroy();
    }

    // Tell the SDK when the remote controller is plugged in over USB (same as the sample project's MainActivity)
    @Override
    protected void onNewIntent(@NonNull Intent intent) {
        super.onNewIntent(intent);
        if (UsbManager.ACTION_USB_ACCESSORY_ATTACHED.equals(intent.getAction())) {
            Intent attached = new Intent();
            attached.setAction(DJISDKManager.USB_ACCESSORY_ATTACHED);
            sendBroadcast(attached);
        }
    }

    // ---------- Permissions + SDK registration ----------
    private void requestPermissionsThenRegister() {
        List<String> missing = new ArrayList<>();
        for (String p : PERMISSIONS) {
            if (ContextCompat.checkSelfPermission(this, p) != PackageManager.PERMISSION_GRANTED) missing.add(p);
        }
        if (missing.isEmpty()) registerSdk();
        else ActivityCompat.requestPermissions(this, missing.toArray(new String[0]), REQ_PERMISSIONS);
    }

    @Override
    public void onRequestPermissionsResult(int requestCode, @NonNull String[] permissions, @NonNull int[] results) {
        super.onRequestPermissionsResult(requestCode, permissions, results);
        // Android 13+ always denies the storage permissions, so register whatever the result.
        for (int i = 0; i < permissions.length; i++) {
            if (results[i] != PackageManager.PERMISSION_GRANTED) Log.w(TAG, "Permission denied: " + permissions[i]);
        }
        if (requestCode == REQ_PERMISSIONS) registerSdk();
    }

    private void registerSdk() {
        if (!registrationStarted.compareAndSet(false, true)) return;
        setSdkStatus("SDK: registering...");
        DJISDKManager.getInstance().registerApp(getApplicationContext(), new DJISDKManager.SDKManagerCallback() {
            @Override public void onRegister(DJIError e) {
                if (e == DJISDKError.REGISTRATION_SUCCESS) {
                    setSdkStatus("SDK: registered, waiting for drone");
                    DJISDKManager.getInstance().startConnectionToProduct();
                } else {
                    registrationStarted.set(false);
                    setSdkStatus("SDK registration error: " + (e == null ? "?" : e.getDescription()));
                }
            }
            @Override public void onProductDisconnect() {
                setSdkStatus("SDK: drone disconnected");
                runOnUiThread(() -> detachStreamers());
            }
            @Override public void onProductConnect(BaseProduct p) { onProduct(); }
            @Override public void onProductChanged(BaseProduct p) { onProduct(); }
            @Override public void onComponentChange(BaseProduct.ComponentKey k,
                                                    BaseComponent oldC, BaseComponent newC) {
                // The flight controller / battery may arrive after the product: re-attach
                if (newC != null) onProduct();
            }
            @Override public void onInitProcess(DJISDKInitEvent ev, int total) { }
            @Override public void onDatabaseDownloadProgress(long cur, long total) { }
        });
    }

    private void onProduct() {
        BaseProduct p = DJISDKManager.getInstance().getProduct();
        if (p == null) return;
        setSdkStatus("SDK: connected - " + (p.getModel() == null ? "?" : p.getModel().getDisplayName()));
        runOnUiThread(this::attachStreamers);
    }

    // ---------- Streaming ----------
    private void startStreaming() {
        String host = hostEdit.getText().toString().trim();
        if (host.isEmpty()) {
            hostEdit.setError("IP required");
            return;
        }
        prefs.edit().putString("host", host).apply();
        telemetry = new TelemetryStreamer(host, TELEMETRY_PORT);
        frames = new FrameStreamer(this, videoSurface, host, FRAME_PORT);
        attachStreamers();
        startButton.setText(R.string.stop);
        refreshStatus();
    }

    private void stopStreaming() {
        if (telemetry != null) telemetry.close();
        if (frames != null) frames.close();
        telemetry = null;
        frames = null;
        startButton.setText(R.string.start);
        refreshStatus();
    }

    private void attachStreamers() {
        BaseProduct p = DJISDKManager.getInstance().getProduct();
        if (!(p instanceof Aircraft) || telemetry == null) return;
        telemetry.attach((Aircraft) p);
        frames.attach();
    }

    private void detachStreamers() {
        if (telemetry != null) telemetry.detach();
        if (frames != null) frames.detach();
    }

    // ---------- Status line ----------
    private void setSdkStatus(String s) {
        Log.i(TAG, s);
        sdkStatus = s;
        runOnUiThread(this::refreshStatus);
    }

    private void refreshStatus() {
        String stream = telemetry == null ? "Stream: off"
                : "Stream: " + hostEdit.getText().toString().trim() + " :" + TELEMETRY_PORT + " (telemetry), :" + FRAME_PORT + " (frames)";
        statusText.setText(sdkStatus + "\n" + stream);
    }
}
