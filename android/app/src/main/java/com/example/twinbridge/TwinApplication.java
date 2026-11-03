package com.example.twinbridge;

import android.app.Application;
import android.content.Context;

import androidx.multidex.MultiDex;

public class TwinApplication extends Application {
    @Override
    protected void attachBaseContext(Context base) {
        super.attachBaseContext(base);
        MultiDex.install(this);
        // MSDK 4.x: loads the SDK classes, required before registerApp (same as DJISampleApplication in the sample project).
        com.cySdkyc.clx.Helper.install(this);
    }
}
