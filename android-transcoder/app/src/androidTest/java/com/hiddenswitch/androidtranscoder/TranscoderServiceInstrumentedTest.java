package com.hiddenswitch.androidtranscoder;

import android.Manifest;
import android.content.Context;
import android.content.Intent;
import android.os.Build;
import android.os.ParcelFileDescriptor;

import androidx.test.ext.junit.runners.AndroidJUnit4;
import androidx.test.platform.app.InstrumentationRegistry;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.TimeUnit;

import org.junit.After;
import org.junit.Before;
import org.junit.Test;
import org.junit.runner.RunWith;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertTrue;

@RunWith(AndroidJUnit4.class)
public final class TranscoderServiceInstrumentedTest {
    private Context context;

    @Before
    public void setUp() throws Exception {
        context = InstrumentationRegistry.getInstrumentation().getTargetContext();
        grantPostNotificationsIfNeeded();
        Intent intent = new Intent(context, TranscoderService.class);
        if (Build.VERSION.SDK_INT >= 26) {
            context.startForegroundService(intent);
        } else {
            context.startService(intent);
        }
        waitForStatus();
    }

    @After
    public void tearDown() {
        context.stopService(new Intent(context, TranscoderService.class));
    }

    @Test
    public void testStatusEndpointLooksLikeApkService() throws Exception {
        HttpResult result = request("GET", "/api/v1/status", null, null);

        assertEquals(200, result.status);
        assertTrue(result.body.contains("\"HiddenSwitch Android Transcoder\""));
        assertTrue(result.body.contains("\"hardware\":\"mediacodec-gles\""));
        assertTrue(result.body.contains("\"tokenRequired\":true"));
    }

    @Test
    public void testTranscodeEndpointRejectsMissingBearerToken() throws Exception {
        HttpResult result = request("POST",
                "/api/v1/transcode?codec=h264&width=1920&height=1080&bitrate=6000000",
                null,
                "fake-matroska".getBytes(StandardCharsets.UTF_8));

        assertEquals(401, result.status);
        assertEquals("unauthorized\n", result.body);
    }

    @Test
    public void testAuthorizedTranscodeEndpointAcceptsJellyfinStyleRequest() throws Exception {
        HttpResult result = request("POST",
                "/api/v1/transcode?codec=h264&width=1920&height=1080&bitrate=6000000&maxrate=6000000&bufsize=12000000&gop=120&toneMap=1",
                "Bearer " + AppConfig.token(context),
                "not-a-real-video".getBytes(StandardCharsets.UTF_8));

        assertEquals(200, result.status);
        assertEquals("video/MP2T", result.contentType);
    }

    private void waitForStatus() throws Exception {
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(10);
        Exception last = null;
        while (System.nanoTime() < deadline) {
            try {
                if (request("GET", "/api/v1/status", null, null).status == 200) {
                    return;
                }
            } catch (Exception ex) {
                last = ex;
            }
            Thread.sleep(100);
        }
        throw new AssertionError("Timed out waiting for Android transcoder service", last);
    }

    private static HttpResult request(String method, String path, String authorization, byte[] body) throws Exception {
        HttpURLConnection connection = (HttpURLConnection) new URL("http://127.0.0.1:" + AppConfig.PORT + path).openConnection();
        connection.setRequestMethod(method);
        connection.setConnectTimeout(3000);
        connection.setReadTimeout(15000);
        if (authorization != null) {
            connection.setRequestProperty("Authorization", authorization);
        }
        if (body != null) {
            connection.setDoOutput(true);
            connection.setRequestProperty("Content-Type", "video/x-matroska");
            connection.setFixedLengthStreamingMode(body.length);
            try (OutputStream out = connection.getOutputStream()) {
                out.write(body);
            }
        }

        int status = connection.getResponseCode();
        String contentType = connection.getContentType();
        InputStream stream = status >= 400 ? connection.getErrorStream() : connection.getInputStream();
        String responseBody = stream == null ? "" : readAll(stream);
        connection.disconnect();
        return new HttpResult(status, contentType, responseBody);
    }

    private static String readAll(InputStream stream) throws Exception {
        try (InputStream in = stream; ByteArrayOutputStream out = new ByteArrayOutputStream()) {
            byte[] buffer = new byte[8192];
            int read;
            while ((read = in.read(buffer)) >= 0) {
                out.write(buffer, 0, read);
            }
            return out.toString(StandardCharsets.UTF_8.name());
        }
    }

    private void grantPostNotificationsIfNeeded() throws Exception {
        if (Build.VERSION.SDK_INT < 33) {
            return;
        }
        String command = "pm grant " + context.getPackageName() + " " + Manifest.permission.POST_NOTIFICATIONS;
        try (ParcelFileDescriptor ignored = InstrumentationRegistry.getInstrumentation().getUiAutomation().executeShellCommand(command)) {
        }
    }

    private static final class HttpResult {
        final int status;
        final String contentType;
        final String body;

        HttpResult(int status, String contentType, String body) {
            this.status = status;
            this.contentType = contentType;
            this.body = body;
        }
    }
}
