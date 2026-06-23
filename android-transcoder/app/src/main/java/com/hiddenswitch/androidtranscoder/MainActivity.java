package com.hiddenswitch.androidtranscoder;

import android.Manifest;
import android.app.Activity;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.os.PowerManager;
import android.provider.Settings;
import android.util.Log;
import android.view.Gravity;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Switch;
import android.widget.TextView;
import android.widget.Toast;

import com.google.android.material.bottomnavigation.BottomNavigationView;
import com.google.zxing.integration.android.IntentIntegrator;
import com.google.zxing.integration.android.IntentResult;

public class MainActivity extends Activity {
    private static final String TAG = "AndroidTranscoder";
    private static final int REQUEST_CAMERA_PERMISSION = 300;
    private static final int COLOR_BG = 0xfff3f6f8;
    private static final int COLOR_TEXT = 0xff102027;
    private static final int COLOR_MUTED = 0xff607d8b;
    private static final int COLOR_PRIMARY = 0xff006c67;
    private static final int COLOR_PRIMARY_DARK = 0xff004d4a;
    private static final int COLOR_CARD = 0xffffffff;
    private static final int COLOR_BORDER = 0xffd7e1e6;

    private LinearLayout content;
    private BottomNavigationView bottomNav;
    private TextView statusLine;
    private TextView pairingStatus;
    private TextView subtitleLine;
    private TextView detail;
    private TextView jobs;
    private Switch keepAwake;
    private boolean showingPairing = true;
    private boolean updatingBottomNav;
    private final Handler refreshHandler = new Handler(Looper.getMainLooper());
    private final Runnable refreshTick = new Runnable() {
        @Override
        public void run() {
            refresh();
            refreshHandler.postDelayed(this, 1000);
        }
    };

    @Override
    protected void onCreate(Bundle bundle) {
        super.onCreate(bundle);
        requestRuntimePermissions();
        requestBatteryOptimizationExemptionIfNeeded();
        startTranscoderService();
        buildLayout();
        applyAutomationIntent(getIntent());
        applyKeepAwakeWindowFlag();
        showPairing();
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
        refreshHandler.removeCallbacks(refreshTick);
        refreshHandler.postDelayed(refreshTick, 1000);
    }

    @Override
    protected void onPause() {
        refreshHandler.removeCallbacks(refreshTick);
        super.onPause();
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        IntentResult result = IntentIntegrator.parseActivityResult(requestCode, resultCode, data);
        if (result == null) {
            return;
        }
        String text = result.getContents();
        if (text == null || text.trim().isEmpty()) {
            toast("QR scan cancelled");
            return;
        }
        pairFromUrl(text.trim());
    }

    @Override
    public void onRequestPermissionsResult(int requestCode, String[] permissions, int[] grantResults) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == REQUEST_CAMERA_PERMISSION) {
            if (grantResults.length > 0 && grantResults[0] == PackageManager.PERMISSION_GRANTED) {
                openQrScanner();
            } else {
                toast("Camera permission is required to scan the Jellyfin QR");
            }
        }
    }

    private void buildLayout() {
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(0, 0, 0, 0);
        root.setBackgroundColor(COLOR_BG);

        TextView title = new TextView(this);
        title.setText("Android Transcoder");
        title.setTextColor(COLOR_TEXT);
        title.setTextSize(27);
        title.setTypeface(Typeface.DEFAULT_BOLD);
        title.setGravity(Gravity.CENTER);
        title.setPadding(dp(20), dp(52), dp(20), 0);
        root.addView(title);

        subtitleLine = new TextView(this);
        subtitleLine.setText("Hardware video engine for Jellyfin");
        subtitleLine.setTextColor(COLOR_MUTED);
        subtitleLine.setTextSize(15);
        subtitleLine.setGravity(Gravity.CENTER);
        subtitleLine.setPadding(dp(20), dp(3), dp(20), 0);
        root.addView(subtitleLine);

        statusLine = new TextView(this);
        statusLine.setTextSize(15);
        statusLine.setTextColor(COLOR_PRIMARY_DARK);
        statusLine.setGravity(Gravity.CENTER);
        statusLine.setTypeface(Typeface.DEFAULT_BOLD);
        statusLine.setPadding(dp(20), dp(8), dp(20), dp(8));
        LinearLayout.LayoutParams statusParams = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT);
        statusParams.setMargins(dp(20), dp(18), dp(20), dp(10));
        root.addView(statusLine, statusParams);

        ScrollView scroll = new ScrollView(this);
        content = new LinearLayout(this);
        content.setOrientation(LinearLayout.VERTICAL);
        content.setPadding(dp(20), dp(4), dp(20), dp(20));
        scroll.addView(content);
        root.addView(scroll, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                0,
                1));

        bottomNav = new BottomNavigationView(this);
        bottomNav.setBackgroundColor(0xffffffff);
        bottomNav.setItemIconTintList(null);
        bottomNav.getMenu().add(0, 1, 0, "Pairing").setIcon(android.R.drawable.ic_menu_camera);
        bottomNav.getMenu().add(0, 2, 1, "Status").setIcon(android.R.drawable.ic_menu_manage);
        bottomNav.setOnItemSelectedListener(item -> {
            if (updatingBottomNav) {
                return true;
            }
            if (item.getItemId() == 1) {
                showPairing();
                return true;
            }
            if (item.getItemId() == 2) {
                showStatus();
                return true;
            }
            return false;
        });
        root.addView(bottomNav);
        setContentView(root);
    }

    private void showPairing() {
        showingPairing = true;
        content.removeAllViews();
        selectBottomNav(1);

        content.addView(sectionLabel("PAIRING"));

        Button scan = new Button(this);
        scan.setText("Pair from QR");
        scan.setTextSize(20);
        scan.setTextColor(0xffffffff);
        scan.setAllCaps(false);
        scan.setCompoundDrawablesWithIntrinsicBounds(android.R.drawable.ic_menu_camera, 0, 0, 0);
        scan.setCompoundDrawablePadding(dp(12));
        scan.setGravity(Gravity.CENTER);
        scan.setPadding(dp(28), dp(20), dp(28), dp(20));
        scan.setBackground(rounded(COLOR_PRIMARY, COLOR_PRIMARY, dp(12)));
        scan.setOnClickListener(v -> openQrScanner());
        content.addView(scan, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT));

        TextView hint = bodyText("Scan the QR shown in a Jellyfin server's Android Transcoder settings. Each Jellyfin server owns its own connection to this phone.");
        hint.setTextColor(COLOR_MUTED);
        hint.setGravity(Gravity.CENTER);
        hint.setPadding(dp(8), dp(16), dp(8), dp(12));
        content.addView(hint);

        pairingStatus = bodyText(TranscoderService.pairingStatusForUi());
        pairingStatus.setGravity(Gravity.CENTER);
        pairingStatus.setTextColor(COLOR_MUTED);
        content.addView(pairingStatus);

        content.addView(sectionLabel("MANUAL SETUP URL"));

        detail = bodyText(AppConfig.setupUrl(this));
        detail.setTextColor(COLOR_PRIMARY_DARK);
        detail.setTypeface(Typeface.MONOSPACE, Typeface.BOLD);
        detail.setGravity(Gravity.CENTER);
        detail.setPadding(dp(16), dp(18), dp(16), dp(18));
        detail.setOnClickListener(v -> copySetupUrl());
        content.addView(card(detail, 0, 0, 0, 0));

        Button copy = new Button(this);
        copy.setText("Copy setup URL");
        copy.setAllCaps(false);
        copy.setTextColor(COLOR_TEXT);
        copy.setBackground(rounded(0xffffffff, COLOR_BORDER, dp(8)));
        copy.setOnClickListener(v -> copySetupUrl());
        content.addView(copy);

        Button reset = new Button(this);
        reset.setText("Reset setup code");
        reset.setAllCaps(false);
        reset.setTextColor(COLOR_TEXT);
        reset.setBackground(rounded(0xffffffff, COLOR_BORDER, dp(8)));
        reset.setOnClickListener(v -> {
            AppConfig.resetToken(this);
            refresh();
            toast("Setup code reset");
        });
        LinearLayout.LayoutParams resetParams = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT);
        resetParams.setMargins(0, dp(10), 0, 0);
        content.addView(reset, resetParams);
        refresh();
    }

    private void showStatus() {
        showingPairing = false;
        content.removeAllViews();
        selectBottomNav(2);

        content.addView(sectionLabel("SERVICE"));

        keepAwake = new Switch(this);
        keepAwake.setText("Keep awake");
        keepAwake.setTextSize(18);
        keepAwake.setTextColor(COLOR_TEXT);
        keepAwake.setChecked(AppConfig.keepAwake(this));
        keepAwake.setOnCheckedChangeListener((button, checked) -> {
            AppConfig.setKeepAwake(this, checked);
            applyKeepAwakeWindowFlag();
            Intent service = new Intent(this, TranscoderService.class);
            service.setAction(TranscoderService.ACTION_REFRESH_POWER);
            if (Build.VERSION.SDK_INT >= 26) {
                startForegroundService(service);
            } else {
                startService(service);
            }
            refresh();
        });
        content.addView(card(keepAwake, dp(16), dp(12), dp(16), dp(12)));

        detail = bodyText("");
        detail.setPadding(dp(16), dp(12), dp(16), dp(12));
        content.addView(card(detail, 0, 0, 0, 0));

        content.addView(sectionLabel("ACTIVE JOBS"));
        jobs = bodyText("");
        jobs.setTextIsSelectable(true);
        jobs.setPadding(dp(16), dp(12), dp(16), dp(12));
        content.addView(card(jobs, 0, 0, 0, 0));
        refresh();
    }

    private TextView bodyText(String text) {
        TextView view = new TextView(this);
        view.setText(text);
        view.setTextColor(COLOR_TEXT);
        view.setTextSize(16);
        view.setLineSpacing(0, 1.08f);
        view.setPadding(0, dp(10), 0, dp(18));
        return view;
    }

    private void selectBottomNav(int itemId) {
        if (bottomNav == null || bottomNav.getSelectedItemId() == itemId) {
            return;
        }
        updatingBottomNav = true;
        try {
            bottomNav.setSelectedItemId(itemId);
        } finally {
            updatingBottomNav = false;
        }
    }

    private void refresh() {
        boolean ready = TranscoderService.isRunning() && TranscoderService.isListening() && ffmpegInstalled();
        statusLine.setText(ready ? "STATUS: Ready" : "STATUS: Starting service");
        statusLine.setBackgroundColor(COLOR_BG);
        if (showingPairing) {
            if (detail != null) {
                detail.setText(AppConfig.setupUrl(this));
            }
            if (pairingStatus != null) {
                pairingStatus.setText(TranscoderService.pairingStatusForUi());
            }
            return;
        }
        if (keepAwake != null) {
            keepAwake.setChecked(AppConfig.keepAwake(this));
        }
        if (detail != null) {
            detail.setText(statusDetails());
        }
        if (jobs != null) {
            jobs.setText(TranscoderService.activeJobsForUi());
        }
    }

    private String pairingDetails() {
        StringBuilder builder = new StringBuilder();
        String paired = AppConfig.pairedJellyfinUrl(this);
        if (!paired.isEmpty()) {
            builder.append("Last paired: ").append(paired).append("\n\n");
        }
        builder.append("If QR pairing is not available, configure Jellyfin with these values.\n\n");
        builder.append("Token: ").append(AppConfig.token(this)).append("\n\n");
        for (String url : AppConfig.baseUrls()) {
            builder.append(url).append('\n');
        }
        return builder.toString();
    }

    private void copySetupUrl() {
        ClipboardManager clipboard = (ClipboardManager) getSystemService(Context.CLIPBOARD_SERVICE);
        clipboard.setPrimaryClip(ClipData.newPlainText("android-transcoder-setup-url", AppConfig.setupUrl(this)));
        toast("Setup URL copied");
    }

    private String statusDetails() {
        StringBuilder builder = new StringBuilder();
        builder.append("Service: ").append(TranscoderService.isRunning() ? "running" : "starting").append('\n');
        builder.append("Network: ").append(TranscoderService.isListening() ? "listening" : "starting").append('\n');
        if (!TranscoderService.listenError().isEmpty()) {
            builder.append("Error: ").append(TranscoderService.listenError()).append('\n');
        }
        builder.append("FFmpeg: ").append(ffmpegInstalled() ? "ready" : "missing").append('\n');
        builder.append(TranscoderService.statusSummaryForUi()).append('\n');
        return builder.toString();
    }

    private void openQrScanner() {
        if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[]{Manifest.permission.CAMERA}, REQUEST_CAMERA_PERMISSION);
            return;
        }
        IntentIntegrator integrator = new IntentIntegrator(this);
        integrator.setDesiredBarcodeFormats(IntentIntegrator.QR_CODE);
        integrator.setPrompt("Scan the Jellyfin pairing QR");
        integrator.setBeepEnabled(false);
        integrator.setOrientationLocked(false);
        integrator.initiateScan();
    }

    private void pairFromUrl(String pairUrl) {
        Uri uri;
        try {
            uri = Uri.parse(pairUrl);
        } catch (Exception ex) {
            uri = null;
        }
        if (uri == null ||
                (!"http".equals(uri.getScheme()) && !"https".equals(uri.getScheme())) ||
                uri.getHost() == null ||
                !uri.getPath().startsWith("/AndroidTranscoder/Pair/")) {
            TranscoderService.setPairingStatusForUi("Invalid QR code. Scan the QR code from the Jellyfin Android Transcoder page.");
            refresh();
            toast("Invalid Jellyfin pairing QR");
            return;
        }
        Intent service = new Intent(this, TranscoderService.class);
        service.putExtra("pairUrl", pairUrl);
        service.putExtra("startOnBoot", true);
        service.putExtra("keepAwake", true);
        if (Build.VERSION.SDK_INT >= 26) {
            startForegroundService(service);
        } else {
            startService(service);
        }
        AppConfig.setStartOnBoot(this, true);
        AppConfig.setKeepAwake(this, true);
        toast("Pairing with Jellyfin");
        refresh();
    }

    private void applyAutomationIntent(Intent intent) {
        if (intent == null) {
            return;
        }
        if (intent.hasExtra("token")) {
            AppConfig.setToken(this, intent.getStringExtra("token"));
        }
        if (intent.hasExtra("startOnBoot")) {
            AppConfig.setStartOnBoot(this, intent.getBooleanExtra("startOnBoot", false));
        }
        if (intent.hasExtra("keepAwake")) {
            AppConfig.setKeepAwake(this, intent.getBooleanExtra("keepAwake", false));
        }
        if (intent.hasExtra("pairUrl")) {
            pairFromUrl(intent.getStringExtra("pairUrl"));
            return;
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

    private void requestRuntimePermissions() {
        if (Build.VERSION.SDK_INT >= 33 &&
                checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS}, 100);
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

    private void toast(String message) {
        Toast.makeText(this, message, Toast.LENGTH_SHORT).show();
    }

    private TextView sectionLabel(String text) {
        TextView label = new TextView(this);
        label.setText(text);
        label.setTextColor(COLOR_MUTED);
        label.setTextSize(12);
        label.setTypeface(Typeface.DEFAULT_BOLD);
        label.setPadding(dp(2), dp(12), 0, dp(8));
        return label;
    }

    private LinearLayout card(android.view.View child, int left, int top, int right, int bottom) {
        LinearLayout wrapper = new LinearLayout(this);
        wrapper.setOrientation(LinearLayout.VERTICAL);
        wrapper.setBackground(rounded(COLOR_CARD, COLOR_BORDER, dp(10)));
        wrapper.setPadding(left, top, right, bottom);
        wrapper.addView(child, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT));
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT);
        params.setMargins(0, 0, 0, dp(10));
        wrapper.setLayoutParams(params);
        return wrapper;
    }

    private GradientDrawable rounded(int fill, int stroke, int radius) {
        GradientDrawable drawable = new GradientDrawable();
        drawable.setColor(fill);
        drawable.setCornerRadius(radius);
        drawable.setStroke(dp(1), stroke);
        return drawable;
    }

    private int dp(int value) {
        return (int) (value * getResources().getDisplayMetrics().density + 0.5f);
    }
}
