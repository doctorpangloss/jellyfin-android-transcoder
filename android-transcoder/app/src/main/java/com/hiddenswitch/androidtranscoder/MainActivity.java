package com.hiddenswitch.androidtranscoder;

import android.Manifest;
import android.app.Activity;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.os.Build;
import android.os.Bundle;
import android.util.Log;
import android.view.Gravity;
import android.view.View;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Switch;
import android.widget.TextView;
import android.widget.Toast;

import java.io.BufferedReader;
import java.io.InputStreamReader;

public class MainActivity extends Activity {
    private static final String TAG = "AndroidTranscoder";
    private TextView info;
    private TextView json;
    private Switch startOnBoot;
    private Switch keepAwake;

    @Override
    protected void onCreate(Bundle bundle) {
        super.onCreate(bundle);
        if (Build.VERSION.SDK_INT >= 33 &&
                checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS}, 100);
        }

        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(32, 32, 32, 32);

        TextView title = new TextView(this);
        title.setText("Android Transcoder");
        title.setTextSize(24);
        title.setGravity(Gravity.START);
        root.addView(title);

        Button start = new Button(this);
        start.setText("Start Service");
        start.setOnClickListener(v -> {
            Intent intent = new Intent(this, TranscoderService.class);
            if (Build.VERSION.SDK_INT >= 26) {
                startForegroundService(intent);
            } else {
                startService(intent);
            }
            refresh();
        });
        root.addView(start);

        Button stop = new Button(this);
        stop.setText("Stop Service");
        stop.setOnClickListener(v -> {
            stopService(new Intent(this, TranscoderService.class));
            refresh();
        });
        root.addView(stop);

        startOnBoot = new Switch(this);
        startOnBoot.setText("Start on boot");
        startOnBoot.setChecked(AppConfig.startOnBoot(this));
        startOnBoot.setOnCheckedChangeListener((button, checked) -> {
            AppConfig.setStartOnBoot(this, checked);
            refresh();
        });
        root.addView(startOnBoot);

        keepAwake = new Switch(this);
        keepAwake.setText("Keep screen and Wi-Fi awake");
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
        copy.setText("Copy Plugin JSON");
        copy.setOnClickListener(v -> {
            ClipboardManager clipboard = (ClipboardManager) getSystemService(Context.CLIPBOARD_SERVICE);
            clipboard.setPrimaryClip(ClipData.newPlainText("android-transcoder-config", json.getText()));
            Toast.makeText(this, "Copied", Toast.LENGTH_SHORT).show();
        });
        root.addView(copy);

        info = new TextView(this);
        info.setTextSize(14);
        info.setPadding(0, 24, 0, 24);
        root.addView(info);

        json = new TextView(this);
        json.setTextIsSelectable(true);
        json.setTextSize(13);
        root.addView(json);

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
        startOnBoot.setChecked(AppConfig.startOnBoot(this));
        keepAwake.setChecked(AppConfig.keepAwake(this));
        StringBuilder builder = new StringBuilder();
        builder.append("Service: ").append(TranscoderService.isRunning() ? "running" : "stopped").append("\n");
        builder.append("Port: ").append(AppConfig.PORT).append("\n");
        builder.append("Token: ").append(AppConfig.token(this)).append("\n");
        builder.append("Start on boot: ").append(AppConfig.startOnBoot(this) ? "enabled" : "disabled").append("\n");
        builder.append("Keep awake: ").append(AppConfig.keepAwake(this) ? "enabled" : "disabled").append("\n");
        builder.append("FFmpeg: ").append(ffmpegVersion()).append("\n");
        builder.append("URLs:\n");
        for (String url : AppConfig.baseUrls()) {
            builder.append("  ").append(url).append("\n");
        }
        info.setText(builder.toString());
        json.setText(AppConfig.connectionJson(this));
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
        if (intent.getBooleanExtra("startService", false)) {
            Log.i(TAG, "Starting service from activity automation intent");
            Intent service = new Intent(this, TranscoderService.class);
            if (Build.VERSION.SDK_INT >= 26) {
                startForegroundService(service);
            } else {
                startService(service);
            }
        }
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
}
