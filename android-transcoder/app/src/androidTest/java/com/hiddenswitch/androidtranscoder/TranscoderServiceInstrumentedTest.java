package com.hiddenswitch.androidtranscoder;

import android.Manifest;
import android.content.Context;
import android.content.Intent;
import android.os.Build;
import android.os.ParcelFileDescriptor;

import androidx.test.ext.junit.runners.AndroidJUnit4;
import androidx.test.platform.app.InstrumentationRegistry;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.InputStream;
import java.io.OutputStream;
import java.lang.reflect.Method;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.HashMap;
import java.util.List;
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
        Thread.sleep(300);
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
        assertTrue(result.body.contains("\"jobs\":"));
        assertTrue(result.body.contains("\"jobIdleTimeoutMillis\":"));
        assertTrue(result.body.contains("\"jobMaxRuntimeMillis\":"));
    }

    @Test
    public void testPowerTogglesPersistAndAppearInConnectionMetadata() throws Exception {
        AppConfig.setStartOnBoot(context, true);
        AppConfig.setKeepAwake(context, true);

        assertTrue(AppConfig.startOnBoot(context));
        assertTrue(AppConfig.keepAwake(context));
        assertTrue(AppConfig.connectionJson(context).contains("\"keepAwake\": true"));

        Intent intent = new Intent(context, TranscoderService.class);
        intent.setAction(TranscoderService.ACTION_REFRESH_POWER);
        if (Build.VERSION.SDK_INT >= 26) {
            context.startForegroundService(intent);
        } else {
            context.startService(intent);
        }

        HttpResult result = request("GET", "/api/v1/status", null, null);
        assertTrue(result.body.contains("\"startOnBoot\":true"));
        assertTrue(result.body.contains("\"keepAwake\":true"));
    }

    @Test
    public void testSetupUrlUsesRandomFourDigitTokenAndResetChangesIt() {
        context.getSharedPreferences("android-transcoder", Context.MODE_PRIVATE)
                .edit()
                .remove("token")
                .apply();

        String first = AppConfig.token(context);
        assertTrue(first.matches("[0-9]{4}"));
        assertTrue(AppConfig.setupUrl(context).contains("?token=" + first));

        String reset = AppConfig.resetToken(context);
        assertTrue(reset.matches("[0-9]{4}"));
        assertFalse(first.equals(reset));
        assertTrue(AppConfig.setupUrl(context).contains("?token=" + reset));
    }

    @Test
    public void testBootReceiverStartsServiceOnlyWhenEnabled() throws Exception {
        context.stopService(new Intent(context, TranscoderService.class));
        waitForStopped();

        AppConfig.setStartOnBoot(context, false);
        new BootReceiver().onReceive(context, new Intent(Intent.ACTION_BOOT_COMPLETED));
        Thread.sleep(500);
        assertFalse(TranscoderService.isRunning());

        AppConfig.setStartOnBoot(context, true);
        new BootReceiver().onReceive(context, new Intent(Intent.ACTION_BOOT_COMPLETED));
        waitForStatus();
        assertTrue(TranscoderService.isRunning());
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
        Map<String, String> headers = jsonHeaders();
        HttpResult result = request("POST",
                "/api/v1/remoteprocesses",
                "Bearer " + AppConfig.token(context),
                headers,
                "{\"executable\":\"ffmpeg\",\"args\":[\"-version\"]}".getBytes(StandardCharsets.UTF_8));

        assertEquals(200, result.status);
        assertTrue(result.contentType, result.contentType.startsWith("application/json"));
        assertTrue(result.body, result.body.contains("\"stdinUrl\""));
        assertTrue(result.body, result.body.contains("\"filesUrl\""));

        String filesUrl = jsonString(result.body, "filesUrl");
        HttpResult files = request("GET",
                filesUrl,
                "Bearer " + AppConfig.token(context),
                null,
                null);
        assertEquals(200, files.status);
        assertTrue(files.contentType, files.contentType.startsWith("multipart/mixed"));
        assertTrue(files.body, files.body.contains("\"stdout\""));
        assertTrue(files.body, files.body.contains("ffmpeg version"));
    }

    @Test
    public void testRemoteProcessEndpointAcceptsParallelJellyfinJobs() throws Exception {
        Map<String, String> headers = jsonHeaders();
        byte[] requestBody = ("{\"executable\":\"ffmpeg\",\"args\":[\"-hide_banner\",\"-loglevel\",\"error\"," +
                "\"-i\",\"{input}\",\"-f\",\"null\",\"-\"]}").getBytes(StandardCharsets.UTF_8);

        HttpResult first = request("POST",
                "/api/v1/remoteprocesses",
                "Bearer " + AppConfig.token(context),
                headers,
                requestBody);
        assertEquals(first.body, 200, first.status);

        HttpResult second = request("POST",
                "/api/v1/remoteprocesses",
                "Bearer " + AppConfig.token(context),
                headers,
                requestBody);
        assertEquals(second.body, 200, second.status);
        assertTrue(second.body, second.body.contains("\"stdinUrl\""));
        assertTrue(second.body, second.body.contains("\"filesUrl\""));

        HttpResult status = request("GET", "/api/v1/status", null, null);
        assertEquals(200, status.status);
        assertTrue(status.body, status.body.contains("\"maxJobs\":255"));
        assertTrue(status.body, status.body.contains("\"activeJobs\":2"));

        String firstId = jsonString(first.body, "id");
        String secondId = jsonString(second.body, "id");
        HttpResult cancelFirst = request("DELETE",
                "/api/v1/remoteprocesses/" + firstId,
                "Bearer " + AppConfig.token(context),
                null,
                null);
        assertEquals(cancelFirst.body, 200, cancelFirst.status);
        assertTrue(cancelFirst.body, cancelFirst.body.contains("\"canceled\":true"));

        HttpResult afterCancelOne = request("GET", "/api/v1/status", null, null);
        assertEquals(200, afterCancelOne.status);
        assertTrue(afterCancelOne.body, afterCancelOne.body.contains("\"activeJobs\":1"));
        assertTrue(afterCancelOne.body, afterCancelOne.body.contains(secondId));
        assertFalse(afterCancelOne.body, afterCancelOne.body.contains(firstId));

        HttpResult cancelSecond = request("DELETE",
                "/api/v1/remoteprocesses/" + secondId,
                "Bearer " + AppConfig.token(context),
                null,
                null);
        assertEquals(cancelSecond.body, 200, cancelSecond.status);
        assertTrue(cancelSecond.body, cancelSecond.body.contains("\"canceled\":true"));
    }

    @Test
    public void testCancelRemoteProcessEndpointIsByIdOnly() throws Exception {
        HttpResult result = request("DELETE",
                "/api/v1/remoteprocesses/current",
                "Bearer " + AppConfig.token(context),
                null,
                null);

        assertEquals(404, result.status);
    }

    @Test
    @SuppressWarnings("unchecked")
    public void testOutputFileListingSkipsFfmpegTempFiles() throws Exception {
        File dir = new File(context.getCacheDir(), "hls-temp-file-test-" + System.nanoTime());
        assertTrue(dir.mkdirs());
        File finalSegment = new File(dir, "segment0.ts");
        File tempSegment = new File(dir, "segment1.ts.tmp");
        File tempPlaylist = new File(dir, "stream.m3u8.tmp");
        assertTrue(finalSegment.createNewFile());
        assertTrue(tempSegment.createNewFile());
        assertTrue(tempPlaylist.createNewFile());

        Method method = TranscoderService.class.getDeclaredMethod("listFiles", File.class);
        method.setAccessible(true);
        List<File> files = (List<File>) method.invoke(null, dir);

        assertTrue(files.contains(finalSegment));
        assertFalse(files.contains(tempSegment));
        assertTrue(files.contains(tempPlaylist));
    }

    @Test
    public void testProcessOutputCaptureIsBoundedForLongRunningFfmpegLogs() {
        StringBuffer buffer = new StringBuffer();
        int chunkLength = TranscoderService.androidLogChunkCharsForTest() * 4;
        String chunk = repeat("x", chunkLength);

        for (int i = 0; i < 64; i++) {
            TranscoderService.appendProcessTextForTest(buffer, chunk, true);
        }

        assertTrue(buffer.length() <= TranscoderService.processTextTailCharsForTest());
        assertEquals(
                repeat("x", Math.min(TranscoderService.processTextTailCharsForTest(), chunkLength * 64)),
                buffer.toString());
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

    private void waitForStopped() throws Exception {
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(10);
        while (System.nanoTime() < deadline) {
            if (!TranscoderService.isRunning()) {
                return;
            }
            Thread.sleep(100);
        }
        throw new AssertionError("Timed out waiting for Android transcoder service to stop");
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
            if (connection.getRequestProperty("Content-Type") == null) {
                connection.setRequestProperty("Content-Type", "video/x-matroska");
            }
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

    private static String jsonString(String json, String name) {
        String marker = "\"" + name + "\":\"";
        int start = json.indexOf(marker);
        if (start < 0) {
            throw new AssertionError("Missing JSON string `" + name + "` in " + json);
        }
        start += marker.length();
        int end = json.indexOf('"', start);
        if (end < 0) {
            throw new AssertionError("Unterminated JSON string `" + name + "` in " + json);
        }
        return json.substring(start, end).replace("\\/", "/");
    }

    private static String repeat(String value, int count) {
        StringBuilder builder = new StringBuilder(value.length() * count);
        for (int i = 0; i < count; i++) {
            builder.append(value);
        }
        return builder.toString();
    }

    private static Map<String, String> jsonHeaders() {
        Map<String, String> headers = new HashMap<>();
        headers.put("Content-Type", "application/json");
        return headers;
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
