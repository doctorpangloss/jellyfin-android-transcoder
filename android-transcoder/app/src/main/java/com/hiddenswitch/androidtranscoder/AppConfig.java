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

    private AppConfig() {
    }

    static String token(Context context) {
        SharedPreferences prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        String existing = prefs.getString(TOKEN, null);
        if (existing != null && !existing.isEmpty()) {
            return existing;
        }
        byte[] bytes = new byte[24];
        new SecureRandom().nextBytes(bytes);
        String token = toHex(bytes);
        prefs.edit().putString(TOKEN, token).apply();
        return token;
    }

    static String ffmpegPath(Context context) {
        return context.getApplicationInfo().nativeLibraryDir + "/libffmpeg.so";
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
            obj.put("name", "HiddenSwitch Android Transcoder");
            obj.put("baseUrl", urls.length() > 0 ? urls.getString(0) : "http://PHONE_IP:" + PORT);
            obj.put("allBaseUrls", urls);
            obj.put("token", token(context));
            obj.put("maxBitrate", 6000000);
            obj.put("targetCodec", "h264");
            obj.put("targetWidth", 1920);
            obj.put("targetHeight", 1080);
            return obj.toString(2);
        } catch (Exception ex) {
            return "{}";
        }
    }

    private static String toHex(byte[] bytes) {
        char[] chars = new char[bytes.length * 2];
        char[] alphabet = "0123456789abcdef".toCharArray();
        for (int i = 0; i < bytes.length; i++) {
            int v = bytes[i] & 0xff;
            chars[i * 2] = alphabet[v >>> 4];
            chars[i * 2 + 1] = alphabet[v & 0x0f];
        }
        return new String(chars);
    }
}
