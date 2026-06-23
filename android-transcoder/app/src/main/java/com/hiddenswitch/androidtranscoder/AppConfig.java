package com.hiddenswitch.androidtranscoder;

import android.content.Context;
import android.content.SharedPreferences;

import org.json.JSONArray;
import org.json.JSONObject;

import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.NetworkInterface;
import java.security.SecureRandom;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

final class AppConfig {
    static final int PORT = 8098;
    private static final String PREFS = "android-transcoder";
    private static final String TOKEN = "token";
    private static final String START_ON_BOOT = "startOnBoot";
    private static final String KEEP_AWAKE = "keepAwake";
    private static final String PAIRED_JELLYFIN_URL = "pairedJellyfinUrl";

    private AppConfig() {
    }

    static String token(Context context) {
        SharedPreferences prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        String existing = prefs.getString(TOKEN, null);
        if (isShortCode(existing)) {
            return existing;
        }
        String token = String.format("%04d", new SecureRandom().nextInt(10000));
        prefs.edit().putString(TOKEN, token).apply();
        return token;
    }

    static void setToken(Context context, String token) {
        if (!isShortCode(token)) {
            return;
        }
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                .edit()
                .putString(TOKEN, token)
                .apply();
    }

    static String resetToken(Context context) {
        String existing = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                .getString(TOKEN, null);
        String token;
        do {
            token = String.format("%04d", new SecureRandom().nextInt(10000));
        } while (token.equals(existing));
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                .edit()
                .putString(TOKEN, token)
                .apply();
        return token;
    }

    static boolean startOnBoot(Context context) {
        return context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                .getBoolean(START_ON_BOOT, true);
    }

    static void setStartOnBoot(Context context, boolean enabled) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                .edit()
                .putBoolean(START_ON_BOOT, enabled)
                .apply();
    }

    static boolean keepAwake(Context context) {
        return context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                .getBoolean(KEEP_AWAKE, true);
    }

    static void setKeepAwake(Context context, boolean enabled) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                .edit()
                .putBoolean(KEEP_AWAKE, enabled)
                .apply();
    }

    static String ffmpegPath(Context context) {
        return context.getApplicationInfo().nativeLibraryDir + "/libffmpeg.so";
    }

    static String pairedJellyfinUrl(Context context) {
        return context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                .getString(PAIRED_JELLYFIN_URL, "");
    }

    static void setPairedJellyfinUrl(Context context, String url) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                .edit()
                .putString(PAIRED_JELLYFIN_URL, url == null ? "" : url)
                .apply();
    }

    static List<String> baseUrls() {
        List<String> urls = new ArrayList<>();
        try {
            List<NetworkInterface> interfaces = Collections.list(NetworkInterface.getNetworkInterfaces());
            for (NetworkInterface iface : interfaces) {
                if (!iface.isUp() || iface.isLoopback()) {
                    continue;
                }
                for (InetAddress address : Collections.list(iface.getInetAddresses())) {
                    if (address instanceof Inet4Address && !address.isLoopbackAddress()) {
                        urls.add("http://" + address.getHostAddress() + ":" + PORT);
                    }
                }
            }
        } catch (Exception ignored) {
        }
        Collections.sort(urls);
        return urls;
    }

    static String connectionJson(Context context) {
        try {
            JSONObject obj = new JSONObject();
            JSONArray urls = new JSONArray();
            for (String url : baseUrls()) {
                urls.put(url);
            }
            obj.put("name", "Android Transcoder");
            obj.put("baseUrl", urls.length() > 0 ? urls.getString(0) : "http://PHONE_IP:" + PORT);
            obj.put("allBaseUrls", urls);
            obj.put("token", token(context));
            obj.put("maxBitrate", 6000000);
            obj.put("targetCodec", "h264");
            obj.put("targetWidth", 1920);
            obj.put("targetHeight", 1080);
            obj.put("keepAwake", keepAwake(context));
            return obj.toString(2);
        } catch (Exception ex) {
            return "{}";
        }
    }

    static String setupUrl(Context context) {
        List<String> urls = baseUrls();
        String baseUrl = urls.isEmpty() ? "http://PHONE_IP:" + PORT : urls.get(0);
        return baseUrl + "/?token=" + token(context);
    }

    private static boolean isShortCode(String token) {
        return token != null && token.matches("[0-9]{4}");
    }
}
