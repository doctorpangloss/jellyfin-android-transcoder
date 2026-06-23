package com.hiddenswitch.androidtranscoder;

import android.Manifest;
import android.app.Activity;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.graphics.Bitmap;
import android.graphics.Color;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.PowerManager;
import android.provider.Settings;
import android.util.Log;
import android.view.Gravity;
import android.view.View;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Switch;
import android.widget.TextView;
import android.widget.Toast;

import com.google.zxing.BarcodeFormat;
import com.google.zxing.MultiFormatWriter;
import com.google.zxing.common.BitMatrix;

import java.io.BufferedReader;
import java.io.InputStreamReader;

public class MainActivity extends Activity {
    private static final String TAG = "AndroidTranscoder";
    private TextView info;
    private TextView code;
    private TextView stats;
    private TextView jobs;
    private ImageView qr;
    private Switch keepAwake;

    @Override
    protected void onCreate(Bundle bundle) {
        super.onCreate(bundle);
        if (Build.VERSION.SDK_INT >= 33 &&
                checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS}, 100);
        }
        requestBatteryOptimizationExemptionIfNeeded();

        startTranscoderService();

        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(40, 40, 40, 40);

        TextView title = new TextView(this);
        title.setText("Android Transcoder");
        title.setTextSize(28);
        title.setGravity(Gravity.START);
        root.addView(title);

        TextView subtitle = new TextView(this);
        subtitle.setText("Scan or enter this code in Jellyfin");
        subtitle.setTextSize(16);
        subtitle.setPadding(0, 4, 0, 24);
        root.addView(subtitle);

        code = new TextView(this);
        code.setTextSize(42);
        code.setGravity(Gravity.CENTER);
        code.setTextIsSelectable(true);
        code.setPadding(0, 20, 0, 20);
        root.addView(code);

        qr = new ImageView(this);
        qr.setAdjustViewBounds(true);
        qr.setPadding(24, 24, 24, 24);
        root.addView(qr, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT));

        keepAwake = new Switch(this);
        keepAwake.setText("Keep awake");
        keepAwake.setChecked(AppConfig.keepAwake(this));
        keepAwake.setOnCheckedChangeListener((button, checked) -> {
            AppConfig.setKeepAwake(this, checked);
            applyKeepAwakeWindowFlag();
            Intent service = new Intent(this, TranscoderService.class);
            service.setAction(TranscoderService.ACTION_REFRESH_POWER);
            if (TranscoderService.isRunning()) {
                if (Build.VERSION.SDK_INT >= 26) {
                    startForegroundService(service);
                } else {
                    startService(service);
                }
            }
            refresh();
        });
        root.addView(keepAwake);

        Button copy = new Button(this);
        copy.setText("Copy pairing details");
        copy.setOnClickListener(v -> {
            ClipboardManager clipboard = (ClipboardManager) getSystemService(Context.CLIPBOARD_SERVICE);
            clipboard.setPrimaryClip(ClipData.newPlainText("android-transcoder-config", AppConfig.connectionJson(this)));
            Toast.makeText(this, "Copied", Toast.LENGTH_SHORT).show();
        });
        root.addView(copy);

        info = new TextView(this);
        info.setTextSize(15);
        info.setPadding(0, 24, 0, 24);
        root.addView(info);

        stats = new TextView(this);
        stats.setTextSize(16);
        stats.setPadding(0, 16, 0, 8);
        root.addView(stats);

        jobs = new TextView(this);
        jobs.setTextSize(15);
        jobs.setPadding(0, 8, 0, 0);
        root.addView(jobs);

        ScrollView scroll = new ScrollView(this);
        scroll.addView(root);
        setContentView(scroll);
        applyAutomationIntent(getIntent());
        applyKeepAwakeWindowFlag();
        refresh();
    }

    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        applyAutomationIntent(intent);
        refresh();
    }

    @Override
    protected void onResume() {
        super.onResume();
        refresh();
    }

    private void refresh() {
        keepAwake.setChecked(AppConfig.keepAwake(this));
        code.setText(AppConfig.token(this));

        StringBuilder builder = new StringBuilder();
        builder.append("Installed: yes\n");
        builder.append("Service: ").append(TranscoderService.isRunning() ? "running" : "starting").append("\n");
        builder.append("Network: ").append(TranscoderService.isListening() ? "listening" : "starting").append("\n");
        if (!TranscoderService.listenError().isEmpty()) {
            builder.append("Error: ").append(TranscoderService.listenError()).append("\n");
        }
        builder.append("FFmpeg: ").append(ffmpegInstalled() ? "ready" : "missing").append("\n");
        for (String url : AppConfig.baseUrls()) {
            builder.append(url).append("\n");
        }
        info.setText(builder.toString());
        stats.setText(TranscoderService.statusSummaryForUi());
        jobs.setText(TranscoderService.activeJobsForUi());
        qr.setImageBitmap(qrBitmap(AppConfig.connectionJson(this), 720));
    }

    private void applyAutomationIntent(Intent intent) {
        if (intent == null) {
            return;
        }
        if (intent.hasExtra("token")) {
            Log.i(TAG, "Applying automation token");
            AppConfig.setToken(this, intent.getStringExtra("token"));
        }
        if (intent.hasExtra("startOnBoot")) {
            AppConfig.setStartOnBoot(this, intent.getBooleanExtra("startOnBoot", false));
        }
        if (intent.hasExtra("keepAwake")) {
            AppConfig.setKeepAwake(this, intent.getBooleanExtra("keepAwake", false));
            applyKeepAwakeWindowFlag();
        }
        startTranscoderService();
    }

    private void startTranscoderService() {
        Intent service = new Intent(this, TranscoderService.class);
        if (Build.VERSION.SDK_INT >= 26) {
            startForegroundService(service);
        } else {
            startService(service);
        }
    }

    private void requestBatteryOptimizationExemptionIfNeeded() {
        if (Build.VERSION.SDK_INT < 23) {
            return;
        }
        try {
            PowerManager powerManager = (PowerManager) getSystemService(POWER_SERVICE);
            if (powerManager == null || powerManager.isIgnoringBatteryOptimizations(getPackageName())) {
                return;
            }
            Intent intent = new Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS);
            intent.setData(Uri.parse("package:" + getPackageName()));
            startActivity(intent);
        } catch (Exception ex) {
            Log.w(TAG, "Battery optimization exemption request failed", ex);
        }
    }

    private boolean ffmpegInstalled() {
        return new java.io.File(AppConfig.ffmpegPath(this)).canExecute();
    }

    private void applyKeepAwakeWindowFlag() {
        if (AppConfig.keepAwake(this)) {
            getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        } else {
            getWindow().clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        }
    }

    private String ffmpegVersion() {
        try {
            Process process = new ProcessBuilder(AppConfig.ffmpegPath(this), "-version").start();
            try (BufferedReader reader = new BufferedReader(new InputStreamReader(process.getInputStream()))) {
                String first = reader.readLine();
                process.destroy();
                return first == null ? "unknown" : first;
            }
        } catch (Exception ex) {
            return ex.getClass().getSimpleName() + ": " + ex.getMessage();
        }
    }

    private static Bitmap qrBitmap(String text, int size) {
        try {
            BitMatrix matrix = new MultiFormatWriter().encode(text, BarcodeFormat.QR_CODE, size, size);
            Bitmap bitmap = Bitmap.createBitmap(size, size, Bitmap.Config.RGB_565);
            for (int y = 0; y < size; y++) {
                for (int x = 0; x < size; x++) {
                    bitmap.setPixel(x, y, matrix.get(x, y) ? Color.BLACK : Color.WHITE);
                }
            }
            return bitmap;
        } catch (Exception ex) {
            Bitmap bitmap = Bitmap.createBitmap(size, size, Bitmap.Config.RGB_565);
            bitmap.eraseColor(Color.WHITE);
            return bitmap;
        }
    }
}
