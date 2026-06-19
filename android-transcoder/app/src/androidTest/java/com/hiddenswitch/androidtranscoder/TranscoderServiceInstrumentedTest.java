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
import java.util.HashMap;
import java.util.Map;
import java.util.Base64;
import java.util.concurrent.TimeUnit;

import org.junit.After;
import org.junit.Before;
import org.junit.Test;
import org.junit.runner.RunWith;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
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
    public void testBundledFfmpegExecutesFromNormalAppContext() throws Exception {
        ExecResult result = execFfmpegVersion();

        assertEquals(result.stderr, 0, result.exitCode);
        assertTrue(result.stdout, result.stdout.startsWith("ffmpeg version"));
        assertFalse(result.stdout, result.stdout.contains("CANNOT LINK EXECUTABLE"));
    }

    @Test
    public void testBundledFfmpegDoesNotDependOnLdLibraryPath() throws Exception {
        ExecResult result = execFfmpegVersion();

        assertEquals(result.stderr, 0, result.exitCode);
        assertTrue(result.stdout, result.stdout.startsWith("ffmpeg version"));
        assertFalse(result.environment.containsKey("LD_LIBRARY_PATH"));
    }

    @Test
    public void testRemoteProcessEndpointRejectsMissingBearerToken() throws Exception {
        HttpResult result = request("POST",
                "/api/v1/remoteprocesses",
                null,
                null,
                "fake-matroska".getBytes(StandardCharsets.UTF_8));

        assertEquals(401, result.status);
        assertEquals("unauthorized\n", result.body);
    }

    @Test
    public void testAuthorizedRemoteProcessEndpointAcceptsProcessRequest() throws Exception {
        Map<String, String> headers = new HashMap<>();
        headers.put("X-Remote-Executable", "ffmpeg");
        headers.put("X-Remote-Args", Base64.getUrlEncoder().withoutPadding().encodeToString(("[\"-version\"]").getBytes(StandardCharsets.UTF_8)));
        HttpResult result = request("POST",
                "/api/v1/remoteprocesses",
                "Bearer " + AppConfig.token(context),
                headers,
                new byte[0]);

        assertEquals(200, result.status);
        assertTrue(result.contentType, result.contentType.startsWith("multipart/mixed"));
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

    private static HttpResult request(String method, String path, String authorization, Map<String, String> headers, byte[] body) throws Exception {
        HttpURLConnection connection = (HttpURLConnection) new URL("http://127.0.0.1:" + AppConfig.PORT + path).openConnection();
        connection.setRequestMethod(method);
        connection.setConnectTimeout(3000);
        connection.setReadTimeout(15000);
        if (authorization != null) {
            connection.setRequestProperty("Authorization", authorization);
        }
        if (headers != null) {
            for (Map.Entry<String, String> header : headers.entrySet()) {
                connection.setRequestProperty(header.getKey(), header.getValue());
            }
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

    private static HttpResult request(String method, String path, String authorization, byte[] body) throws Exception {
        return request(method, path, authorization, null, body);
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

    private ExecResult execFfmpegVersion() throws Exception {
        ProcessBuilder builder = new ProcessBuilder(AppConfig.ffmpegPath(context), "-version");
        builder.environment().remove("LD_LIBRARY_PATH");
        Process process = builder.start();
        String stdout;
        String stderr;
        try (InputStream out = process.getInputStream(); InputStream err = process.getErrorStream()) {
            stdout = readAll(out);
            stderr = readAll(err);
        }
        int exit = process.waitFor();
        return new ExecResult(exit, stdout, stderr, new HashMap<>(builder.environment()));
    }

    private static final class ExecResult {
        final int exitCode;
        final String stdout;
        final String stderr;
        final Map<String, String> environment;

        ExecResult(int exitCode, String stdout, String stderr, Map<String, String> environment) {
            this.exitCode = exitCode;
            this.stdout = stdout;
            this.stderr = stderr;
            this.environment = environment;
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
