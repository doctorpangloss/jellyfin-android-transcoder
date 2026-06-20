package com.hiddenswitch.androidtranscoder;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.os.Build;
import android.util.Log;

public final class BootReceiver extends BroadcastReceiver {
    private static final String TAG = "AndroidTranscoder";

    @Override
    public void onReceive(Context context, Intent intent) {
        if (!Intent.ACTION_BOOT_COMPLETED.equals(intent.getAction()) || !AppConfig.startOnBoot(context)) {
            return;
        }

        Log.i(TAG, "Starting service after boot");
        Intent service = new Intent(context, TranscoderService.class);
        if (Build.VERSION.SDK_INT >= 26) {
            context.startForegroundService(service);
        } else {
            context.startService(service);
        }
    }
}
