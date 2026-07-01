package com.hiddenswitch.androidtranscoder;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Intent;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.net.wifi.WifiManager;
import android.os.Build;
import android.os.IBinder;
import android.os.PowerManager;
import android.util.Log;

import io.vertx.core.Vertx;
import io.vertx.core.buffer.Buffer;
import io.vertx.core.http.HttpServer;
import io.vertx.core.http.HttpServerRequest;
import io.vertx.core.http.HttpServerResponse;
import io.vertx.ext.web.Router;
import io.vertx.ext.web.RoutingContext;

import org.json.JSONObject;
import org.json.JSONArray;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.ArrayList;
import java.util.Base64;
import java.util.Comparator;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.UUID;
import java.util.HashSet;
import java.util.Set;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicLong;
import java.util.concurrent.atomic.AtomicReference;

public class TranscoderService extends Service {
    public static final String ACTION_REFRESH_POWER = "com.hiddenswitch.androidtranscoder.REFRESH_POWER";
    private static final String CHANNEL = "transcoder";
    private static final String TAG = "AndroidTranscoder";
    private static final long JOB_IDLE_TIMEOUT_MS = TimeUnit.SECONDS.toMillis(90);
    private static final long JOB_MAX_RUNTIME_MS = TimeUnit.MINUTES.toMillis(30);
    private static final int MAX_JOBS = 255;
    private static final int PROCESS_TEXT_TAIL_CHARS = 64 * 1024;
    private static final int ANDROID_LOG_CHUNK_CHARS = 3500;
    private static final long STDOUT_SUBSCRIBER_GRACE_MS = TimeUnit.SECONDS.toMillis(3);
    private static final AtomicInteger ACTIVE_JOBS = new AtomicInteger();
    private static final AtomicInteger ACCEPTED_JOBS = new AtomicInteger();
    private static final AtomicInteger COMPLETED_JOBS = new AtomicInteger();
    private static final AtomicLong INPUT_BYTES = new AtomicLong();
    private static final ConcurrentHashMap<String, JobState> JOBS = new ConcurrentHashMap<>();
    private static final AtomicReference<String> LAST_JOB_JSON = new AtomicReference<>("");
    private static volatile boolean running;
    private static volatile boolean listening;
    private static volatile String listenError = "";
    private static volatile String pairingStatus = "No pairing attempt yet";

    private ExecutorService executor;
    private Vertx vertx;
    private HttpServer httpServer;
    private PowerManager.WakeLock wakeLock;
    private WifiManager.WifiLock wifiLock;

    static boolean isRunning() {
        return running;
    }

    static boolean isListening() {
        return listening;
    }

    static String listenError() {
        return listenError;
    }

    static void setPairingStatusForUi(String message) {
        pairingStatus = message == null || message.isEmpty() ? "No pairing attempt yet" : message;
    }

    static String pairingStatusForUi() {
        return pairingStatus;
    }

    static String statusSummaryForUi() {
        long totalInput = INPUT_BYTES.get();
        return "Jobs " + JOBS.size() + "/" + MAX_JOBS
                + "  Accepted " + ACCEPTED_JOBS.get()
                + "  Completed " + COMPLETED_JOBS.get()
                + "  Input " + humanBytes(totalInput);
    }

    static String activeJobsForUi() {
        if (JOBS.isEmpty()) {
            return "Idle";
        }
        StringBuilder builder = new StringBuilder();
        for (JobState job : JOBS.values()) {
            if (builder.length() > 0) {
                builder.append('\n');
            }
            builder.append(job.id, 0, Math.min(8, job.id.length()))
                    .append("  ")
                    .append(humanBytes(job.inputBytes.get()))
                    .append(" in  ")
                    .append(job.outputFiles.get())
                    .append(" files");
        }
        return builder.toString();
    }

    private static String humanBytes(long bytes) {
        if (bytes >= 1024L * 1024L * 1024L) {
            return String.format(java.util.Locale.US, "%.1f GB", bytes / (1024.0 * 1024.0 * 1024.0));
        }
        if (bytes >= 1024L * 1024L) {
            return String.format(java.util.Locale.US, "%.1f MB", bytes / (1024.0 * 1024.0));
        }
        if (bytes >= 1024L) {
            return String.format(java.util.Locale.US, "%.1f KB", bytes / 1024.0);
        }
        return bytes + " B";
    }

    @Override
    public void onCreate() {
        super.onCreate();
        Log.i(TAG, "TranscoderService onCreate");
        createChannel();
        startForeground(1, notification("Listening on :" + AppConfig.PORT));
        executor = Executors.newCachedThreadPool();
        updatePowerLocks();
        startServer();
        running = true;
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        Log.i(TAG, "TranscoderService onStartCommand");
        if (intent != null && intent.hasExtra("token")) {
            AppConfig.setToken(this, intent.getStringExtra("token"));
        }
        if (intent != null && intent.hasExtra("startOnBoot")) {
            AppConfig.setStartOnBoot(this, intent.getBooleanExtra("startOnBoot", false));
        }
        if (intent != null && intent.hasExtra("keepAwake")) {
            AppConfig.setKeepAwake(this, intent.getBooleanExtra("keepAwake", false));
        }
        if (intent != null && intent.hasExtra("pairUrl")) {
            String pairUrl = intent.getStringExtra("pairUrl");
            if (pairUrl != null && !pairUrl.isEmpty()) {
                executor.submit(() -> pairWithJellyfin(pairUrl));
            }
        }
        updatePowerLocks();
        return START_STICKY;
    }

    @Override
    public void onDestroy() {
        running = false;
        if (httpServer != null) {
            httpServer.close();
        }
        listening = false;
        if (vertx != null) {
            vertx.close();
        }
        if (executor != null) {
            executor.shutdownNow();
        }
        releasePowerLocks();
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    private void startServer() {
        vertx = Vertx.vertx();
        Router router = Router.router(vertx);

        router.get("/api/v1/status").handler(this::handleStatus);
        router.route("/api/v1/*").handler(this::requireAuthorization);
        router.deleteWithRegex("/api/v1/remoteprocesses/[^/]+").handler(this::handleCancelRemoteProcess);
        router.post("/api/v1/remoteprocesses").handler(this::handleRemoteProcess);
        router.putWithRegex("/api/v1/remoteprocesses/[^/]+/stdin").handler(this::handleRemoteProcessStdin);
        router.getWithRegex("/api/v1/remoteprocesses/[^/]+/files").handler(this::handleRemoteProcessFiles);
        router.getWithRegex("/api/v1/remoteprocesses/[^/]+/stdout").handler(this::handleRemoteProcessStdout);
        router.route().handler(ctx -> ctx.response().setStatusCode(404).end("not found\n"));

        httpServer = vertx.createHttpServer().requestHandler(router);
        httpServer.listen(AppConfig.PORT).onComplete(result -> {
                    if (result.succeeded()) {
                        listening = true;
                        listenError = "";
                        Log.i(TAG, "Vert.x Web listening on " + AppConfig.PORT);
                    } else {
                        listening = false;
                        listenError = result.cause() == null ? "unknown" : result.cause().getMessage();
                        Log.e(TAG, "Vert.x Web server failed", result.cause());
                    }
                });
    }

    private void updatePowerLocks() {
        if (AppConfig.keepAwake(this)) {
            acquirePowerLocks();
        } else {
            releasePowerLocks();
        }
    }

    private void acquirePowerLocks() {
        if (wakeLock == null) {
            PowerManager powerManager = (PowerManager) getSystemService(POWER_SERVICE);
            wakeLock = powerManager.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "AndroidTranscoder:Service");
            wakeLock.setReferenceCounted(false);
        }
        if (!wakeLock.isHeld()) {
            wakeLock.acquire();
        }

        if (wifiLock == null) {
            WifiManager wifiManager = (WifiManager) getApplicationContext().getSystemService(WIFI_SERVICE);
            wifiLock = wifiManager.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "AndroidTranscoder:Wifi");
            wifiLock.setReferenceCounted(false);
        }
        if (!wifiLock.isHeld()) {
            wifiLock.acquire();
        }
    }

    private void releasePowerLocks() {
        if (wakeLock != null && wakeLock.isHeld()) {
            wakeLock.release();
        }
        if (wifiLock != null && wifiLock.isHeld()) {
            wifiLock.release();
        }
    }

    private void handleStatus(RoutingContext ctx) {
        try {
            writeJson(ctx.response(), statusJson());
        } catch (Exception ex) {
            fail(ctx, ex);
        }
    }

    private void requireAuthorization(RoutingContext ctx) {
        String auth = ctx.request().getHeader("authorization");
        if (auth == null || !auth.equals("Bearer " + AppConfig.token(this))) {
            writeText(ctx.response(), 401, "unauthorized\n");
            return;
        }
        ctx.next();
    }

    private void handleCancelRemoteProcess(RoutingContext ctx) {
        try {
            cancelRemoteProcess(Request.from(ctx), ctx.response());
        } catch (Exception ex) {
            fail(ctx, ex);
        }
    }

    private void handleRemoteProcess(RoutingContext ctx) {
        ctx.request().bodyHandler(body -> {
            try {
                startRemoteProcess(Request.from(ctx, body.toString(StandardCharsets.UTF_8)), ctx.response());
            } catch (Exception ex) {
                fail(ctx, ex);
            }
        });
        ctx.request().exceptionHandler(ex -> fail(ctx, new IOException(ex)));
    }

    private void handleRemoteProcessStdin(RoutingContext ctx) {
        try {
            streamRequestToProcessStdin(Request.from(ctx), ctx);
        } catch (Exception ex) {
            fail(ctx, ex);
        }
    }

    private void handleRemoteProcessFiles(RoutingContext ctx) {
        try {
            streamRemoteProcessFiles(Request.from(ctx), ctx.response());
        } catch (Exception ex) {
            fail(ctx, ex);
        }
    }

    private void handleRemoteProcessStdout(RoutingContext ctx) {
        try {
            streamRemoteProcessStdout(Request.from(ctx), ctx.response());
        } catch (Exception ex) {
            fail(ctx, ex);
        }
    }

    private void fail(RoutingContext ctx, Exception ex) {
        Log.e(TAG, "Request failed", ex);
        if (!ctx.response().ended()) {
            writeText(ctx.response(), 500, "error\n");
        }
    }

    private JSONObject statusJson() throws Exception {
        reapStaleJob("status");
        JSONObject obj = new JSONObject();
        obj.put("name", "HiddenSwitch Android Transcoder");
        obj.put("version", appVersion());
        ACTIVE_JOBS.set(JOBS.size());
        obj.put("activeJobs", JOBS.size());
        obj.put("acceptedJobs", ACCEPTED_JOBS.get());
        obj.put("completedJobs", COMPLETED_JOBS.get());
        obj.put("inputBytes", INPUT_BYTES.get());
        obj.put("maxJobs", MAX_JOBS);
        obj.put("ffmpegPath", AppConfig.ffmpegPath(this));
        obj.put("tokenRequired", true);
        obj.put("startOnBoot", AppConfig.startOnBoot(this));
        obj.put("keepAwake", AppConfig.keepAwake(this));
        JSONArray jobs = new JSONArray();
        for (JobState job : JOBS.values()) {
            jobs.put(job.toJson());
        }
        obj.put("jobs", jobs);
        String lastJob = LAST_JOB_JSON.get();
        if (lastJob != null && !lastJob.isEmpty()) {
            obj.put("lastJob", new JSONObject(lastJob));
        }
        obj.put("jobIdleTimeoutMillis", JOB_IDLE_TIMEOUT_MS);
        obj.put("jobMaxRuntimeMillis", JOB_MAX_RUNTIME_MS);
        obj.put("capabilities", new JSONObject()
                .put("api", "remoteprocesses")
                .put("output", "multipart/mixed")
                .put("executables", new JSONArray().put("ffmpeg"))
                .put("hardware", "mediacodec-gles"));
        return obj;
    }

    private String appVersion() {
        try {
            PackageInfo info = getPackageManager().getPackageInfo(getPackageName(), 0);
            return info.versionName == null ? "unknown" : info.versionName;
        } catch (PackageManager.NameNotFoundException e) {
            return "unknown";
        }
    }

    private void startRemoteProcess(Request request, HttpServerResponse response) throws Exception {
        reapStaleJob("new request");
        JobState job = new JobState(UUID.randomUUID().toString(), new File(getCacheDir(), "remoteprocesses/" + UUID.randomUUID()));
        if (JOBS.size() >= MAX_JOBS) {
            writeText(response, 429, "busy\n");
            return;
        }
        JOBS.put(job.id, job);
        ACTIVE_JOBS.set(JOBS.size());
        try {
            ACCEPTED_JOBS.incrementAndGet();
            File outputDir = new File(job.workDir, "out");
            if (!outputDir.mkdirs() && !outputDir.isDirectory()) {
                throw new IOException("Failed to create " + outputDir);
            }
            job.outputDir = outputDir;
            List<String> command = remoteProcessCommand(request, outputDir);
            io.reactiverse.childprocess.Process.create(vertx, command.get(0), command.subList(1, command.size()))
                    .startHandler(process -> {
                        job.setProcess(process);
                        process.stdout().handler(buffer -> streamProcessStdout(job, buffer));
                        process.stdout().exceptionHandler(ex -> appendProcessText(job.stderrText, "stdout read failed: " + ex + "\n", true));
                        process.stderr().handler(buffer -> appendProcessOutput(job, buffer, true));
                        process.stderr().exceptionHandler(ex -> appendProcessText(job.stderrText, "stderr read failed: " + ex + "\n", true));
                        process.exitHandler(code -> {
                            job.exitCode = code;
                            if (code == 0) {
                                COMPLETED_JOBS.incrementAndGet();
                            }
                            job.processExited = true;
                            job.touchOutput();
                            try {
                                LAST_JOB_JSON.set(job.toJson(code, "").toString());
                            } catch (Exception ignored) {
                            }
                            closeStdoutSubscribers(job);
                        });
                        try {
                            JSONObject body = new JSONObject()
                                    .put("id", job.id)
                                    .put("stdinUrl", "/api/v1/remoteprocesses/" + job.id + "/stdin")
                                    .put("filesUrl", "/api/v1/remoteprocesses/" + job.id + "/files")
                                    .put("stdoutUrl", "/api/v1/remoteprocesses/" + job.id + "/stdout");
                            writeJson(response, body);
                        } catch (Exception ex) {
                            failStart(job, response, ex);
                        }
                    })
                    .start()
                    .onFailure(ex -> failStart(job, response, ex));
        } catch (Exception ex) {
            failStart(job, response, ex);
        }
    }

    private void failStart(JobState job, HttpServerResponse response, Throwable ex) {
        try {
            LAST_JOB_JSON.set(job.toJson(-1, ex.getClass().getSimpleName() + ": " + ex.getMessage()).toString());
        } catch (Exception ignored) {
        }
        deleteRecursively(job.workDir);
        JOBS.remove(job.id, job);
        ACTIVE_JOBS.set(JOBS.size());
        if (!response.ended()) {
            writeText(response, 500, "error\n");
        }
    }

    private void streamRequestToProcessStdin(Request request, RoutingContext ctx) {
        JobState job = jobFromPath(request.path, "/stdin");
        if (job == null || job.process == null) {
            writeText(ctx.response(), 404, "job not found\n");
            return;
        }
        HttpServerRequest httpRequest = ctx.request();
        httpRequest.handler(buffer -> {
            httpRequest.pause();
            job.process.stdin().write(buffer).onComplete(write -> {
                if (write.succeeded()) {
                    INPUT_BYTES.addAndGet(buffer.length());
                    job.addInputBytes(buffer.length());
                    httpRequest.resume();
                    return;
                }
                job.cancel("stdin-error");
                if (!ctx.response().ended()) {
                    writeText(ctx.response(), 500, "stdin error\n");
                }
            });
        });
        httpRequest.exceptionHandler(ex -> {
            job.cancel("stdin-error");
            if (!ctx.response().ended()) {
                writeText(ctx.response(), 500, "stdin error\n");
            }
        });
        httpRequest.endHandler(ignored -> job.process.stdin().close().onComplete(close -> {
            if (close.succeeded()) {
                if (!ctx.response().ended()) {
                    try {
                        writeJson(ctx.response(), new JSONObject().put("id", job.id).put("inputBytes", job.inputBytes.get()));
                    } catch (Exception ex) {
                        writeText(ctx.response(), 500, "error\n");
                    }
                }
            } else {
                job.cancel("stdin-close-error");
                if (!ctx.response().ended()) {
                    writeText(ctx.response(), 500, "stdin close error\n");
                }
            }
        }));
        httpRequest.resume();
    }

    private void streamRemoteProcessFiles(Request request, HttpServerResponse response) throws Exception {
        JobState job = jobFromPath(request.path, "/files");
        if (job == null || job.process == null || job.outputDir == null) {
            writeText(response, 404, "job not found\n");
            return;
        }
        String boundary = "jfat-" + UUID.randomUUID();
        response.setStatusCode(200)
                .putHeader("Content-Type", "multipart/mixed; boundary=" + boundary)
                .setChunked(true);
        writeReadyPart(response, boundary);
        Map<String, FileSnapshot> observed = new HashMap<>();
        Set<String> sent = new HashSet<>();
        streamMultipartTick(response, boundary, observed, sent, job, 0);
    }

    private void streamRemoteProcessStdout(Request request, HttpServerResponse response) {
        JobState job = jobFromPath(request.path, "/stdout");
        if (job == null || job.process == null) {
            writeText(response, 404, "job not found\n");
            return;
        }
        response.setStatusCode(200)
                .putHeader("Content-Type", "application/octet-stream")
                .setChunked(true);
        response.write(Buffer.buffer());
        synchronized (job.stdoutSubscribers) {
            job.stdoutSubscribers.add(response);
        }
        response.exceptionHandler(ex -> removeStdoutSubscriber(job, response, true));
        response.closeHandler(ignored -> removeStdoutSubscriber(job, response, true));
        if (job.processExited) {
            removeStdoutSubscriber(job, response, false);
            if (!response.ended()) {
                response.end();
            }
        }
    }

    private JobState jobFromPath(String path, String suffix) {
        String prefix = "/api/v1/remoteprocesses/";
        if (!path.startsWith(prefix) || !path.endsWith(suffix)) {
            return null;
        }
        String id = path.substring(prefix.length(), path.length() - suffix.length());
        return JOBS.get(id);
    }

    private List<String> remoteProcessCommand(Request request, File outputDir) throws Exception {
        String executable = "";
        JSONArray jsonArgs = null;
        if (request.body != null && !request.body.trim().isEmpty()) {
            JSONObject body = new JSONObject(request.body);
            executable = body.optString("executable", "");
            jsonArgs = body.optJSONArray("args");
        }
        if (executable.isEmpty()) {
            executable = request.headers.getOrDefault("x-remote-executable", "");
        }
        if (!"ffmpeg".equals(executable)) {
            throw new IOException("unsupported executable: " + executable);
        }
        if (jsonArgs == null) {
            jsonArgs = new JSONArray(new String(Base64.getUrlDecoder().decode(
                    request.headers.getOrDefault("x-remote-args", "")), StandardCharsets.UTF_8));
        }
        List<String> command = new ArrayList<>();
        command.add(AppConfig.ffmpegPath(this));
        for (int i = 0; i < jsonArgs.length(); i++) {
            command.add(jsonArgs.getString(i)
                    .replace("{input}", "pipe:0")
                    .replace("{outputRoot}", outputDir.getAbsolutePath()));
        }
        return command;
    }

    private void pairWithJellyfin(String pairUrl) {
        pairingStatus = "Pairing with Jellyfin...";
        try {
            JSONObject payload = new JSONObject(AppConfig.connectionJson(this));
            byte[] body = payload.toString().getBytes(StandardCharsets.UTF_8);
            HttpURLConnection connection = (HttpURLConnection) new URL(pairUrl).openConnection();
            connection.setInstanceFollowRedirects(false);
            connection.setRequestMethod("POST");
            connection.setConnectTimeout(10000);
            connection.setReadTimeout(10000);
            connection.setDoOutput(true);
            connection.setRequestProperty("Content-Type", "application/json; charset=utf-8");
            connection.setRequestProperty("Accept", "application/json");
            connection.setFixedLengthStreamingMode(body.length);
            try (OutputStream out = connection.getOutputStream()) {
                out.write(body);
            }
            int status = connection.getResponseCode();
            String location = connection.getHeaderField("Location");
            InputStream response = status >= 200 && status < 300 ? connection.getInputStream() : connection.getErrorStream();
            String text = response == null ? "" : readAll(response);
            if (status < 200 || status >= 300) {
                Log.w(TAG, "Pairing failed: HTTP " + status + " " + text);
                pairingStatus = pairingFailureMessage(status, text, location);
                connection.disconnect();
                return;
            }
            JSONObject json = new JSONObject(text);
            String token = json.optString("token", "");
            if (!token.isEmpty()) {
                AppConfig.setToken(this, token);
            }
            AppConfig.setPairedJellyfinUrl(this, pairUrl);
            pairingStatus = "Paired with Jellyfin";
            Log.i(TAG, "Paired with Jellyfin at " + pairUrl);
            connection.disconnect();
        } catch (Exception ex) {
            pairingStatus = "Jellyfin server could not be reached. Open the Android Transcoder page using a Jellyfin URL this phone can visit, then scan that QR code.";
            Log.w(TAG, "Pairing failed", ex);
        }
    }

    private static String pairingFailureMessage(int status, String body, String location) {
        String error = "";
        try {
            JSONObject json = new JSONObject(body);
            error = json.optString("error", "");
        } catch (Exception ignored) {
        }
        if ("pairing_code_invalid_or_expired".equals(error)) {
            return "Jellyfin rejected this QR code because it expired or was replaced. Refresh the Jellyfin Android Transcoder page and scan the new QR.";
        }
        if (status >= 300 && status < 400) {
            String target = location == null || location.trim().isEmpty() ? "another URL" : location.trim();
            return "Jellyfin redirected the QR request to " + target + ". Open the Android Transcoder page at the final phone-reachable Jellyfin URL, then scan again.";
        }
        if (error.isEmpty()) {
            return "Jellyfin was reached, but rejected pairing: HTTP " + status;
        }
        return "Jellyfin rejected pairing: " + error + " (HTTP " + status + ")";
    }

    private static void writeJson(HttpServerResponse response, JSONObject json) {
        writeJson(response, 200, json);
    }

    private static void writeJson(HttpServerResponse response, int status, JSONObject json) {
        response.setStatusCode(status)
                .putHeader("Content-Type", "application/json")
                .end(json.toString() + "\n");
    }

    private static void writeText(HttpServerResponse response, int status, String bodyText) {
        response.setStatusCode(status)
                .putHeader("Content-Type", "text/plain")
                .end(bodyText);
    }

    private void cancelRemoteProcess(Request request, HttpServerResponse out) throws Exception {
        JobState job = jobFromPath(request.path, "");
        JSONObject response = new JSONObject();
        if (job == null) {
            response.put("canceled", false);
            response.put("reason", "job not found");
            writeJson(out, 404, response);
            return;
        }
        if (JOBS.remove(job.id, job)) {
            ACTIVE_JOBS.set(JOBS.size());
            executor.execute(() -> {
                job.cancel("api");
                deleteRecursively(job.workDir);
            });
            response.put("canceled", true);
            response.put("job", job.toJson());
            writeJson(out, response);
            return;
        }
        response.put("canceled", false);
        response.put("reason", "job already finished");
        writeJson(out, 404, response);
    }

    private void streamMultipartTick(HttpServerResponse out, String boundary, Map<String, FileSnapshot> observed, Set<String> sent, JobState job, int finalFlushes) {
        try {
            enforceLiveness(job);
            streamStableFiles(out, job.outputDir, boundary, observed, sent, job);
            if (job.processExited) {
                if (finalFlushes >= 5) {
                    writeExitPart(out, boundary, job.exitCode, job.stdoutText.toString(), job.stderrText.toString());
                    LAST_JOB_JSON.set(job.toJson(job.exitCode, "").toString());
                    deleteRecursively(job.workDir);
                    JOBS.remove(job.id, job);
                    ACTIVE_JOBS.set(JOBS.size());
                    return;
                }
                vertx.setTimer(100, ignored -> streamMultipartTick(out, boundary, observed, sent, job, finalFlushes + 1));
                return;
            }
            vertx.setTimer(100, ignored -> streamMultipartTick(out, boundary, observed, sent, job, 0));
        } catch (Exception ex) {
            try {
                LAST_JOB_JSON.set(job.toJson(job.exitCode, ex.getClass().getSimpleName() + ": " + ex.getMessage()).toString());
            } catch (Exception ignored) {
            }
            executor.execute(() -> {
                job.cancel("files-error");
                deleteRecursively(job.workDir);
            });
            JOBS.remove(job.id, job);
            ACTIVE_JOBS.set(JOBS.size());
            if (!out.ended()) {
                out.end();
            }
        }
    }

    private void enforceLiveness(JobState job) throws IOException {
        long age = job.ageMillis();
        long idle = job.idleMillis();
        if (age > JOB_MAX_RUNTIME_MS) {
            executor.execute(() -> job.cancel("max-runtime"));
            throw new IOException("remote process exceeded max runtime");
        }
        if (idle > JOB_IDLE_TIMEOUT_MS) {
            executor.execute(() -> job.cancel("idle-timeout"));
            throw new IOException("remote process idle timeout");
        }
    }

    private void reapStaleJob(String reason) {
        for (JobState job : JOBS.values()) {
            io.reactiverse.childprocess.Process process = job.process;
            if ((process != null && job.processExited) || job.ageMillis() > JOB_MAX_RUNTIME_MS || job.idleMillis() > JOB_IDLE_TIMEOUT_MS) {
                Log.w(TAG, "Reaping stale remote process " + job.id + " during " + reason);
                if (JOBS.remove(job.id, job)) {
                    ACTIVE_JOBS.set(JOBS.size());
                    executor.execute(() -> {
                        job.cancel("reaped-" + reason);
                        deleteRecursively(job.workDir);
                    });
                }
            }
        }
    }

    private static void streamStableFiles(HttpServerResponse out, File outputDir, String boundary, Map<String, FileSnapshot> observed, Set<String> sent, JobState job) throws IOException {
        for (File file : listFiles(outputDir)) {
            String relative = outputDir.toPath().relativize(file.toPath()).toString().replace(File.separatorChar, '/');
            FileSnapshot snapshot = new FileSnapshot(file.length(), file.lastModified());
            if (snapshot.length == 0) {
                continue;
            }
            FileSnapshot previous = observed.put(relative, snapshot);
            if (!snapshot.equals(previous)) {
                continue;
            }
            String signature = relative + ":" + snapshot.length + ":" + snapshot.lastModified;
            if (!sent.add(signature)) {
                continue;
            }
            writeFilePart(out, boundary, outputDir, file, relative);
            job.addOutputFile();
        }
    }

    private static void writeFilePart(HttpServerResponse out, String boundary, File outputDir, File file, String relative) throws IOException {
        ByteArrayOutputStream part = new ByteArrayOutputStream();
        part.write(("--" + boundary + "\r\n").getBytes(StandardCharsets.US_ASCII));
        part.write("Content-Type: application/octet-stream\r\n".getBytes(StandardCharsets.US_ASCII));
        part.write(("Content-Disposition: attachment; filename=\"" + relative + "\"\r\n").getBytes(StandardCharsets.US_ASCII));
        part.write(("X-Remote-Path: " + relative + "\r\n").getBytes(StandardCharsets.US_ASCII));
        part.write("X-Remote-Event: upsert\r\n".getBytes(StandardCharsets.US_ASCII));
        part.write(("Content-Length: " + file.length() + "\r\n\r\n").getBytes(StandardCharsets.US_ASCII));
        Files.copy(file.toPath(), part);
        part.write("\r\n".getBytes(StandardCharsets.US_ASCII));
        out.write(Buffer.buffer(part.toByteArray()));
    }

    private static void writeReadyPart(HttpServerResponse out, String boundary) throws IOException {
        ByteArrayOutputStream part = new ByteArrayOutputStream();
        part.write(("--" + boundary + "\r\n").getBytes(StandardCharsets.US_ASCII));
        part.write("Content-Type: application/octet-stream\r\n".getBytes(StandardCharsets.US_ASCII));
        part.write("X-Remote-Event: ready\r\n".getBytes(StandardCharsets.US_ASCII));
        part.write("Content-Length: 0\r\n\r\n\r\n".getBytes(StandardCharsets.US_ASCII));
        out.write(Buffer.buffer(part.toByteArray()));
    }

    private static void writeExitPart(HttpServerResponse out, String boundary, int exitCode, String stdout, String stderr) throws IOException {
        JSONObject json = new JSONObject();
        try {
            json.put("exitCode", exitCode);
            json.put("stdout", stdout);
            json.put("stderr", stderr);
        } catch (Exception ignored) {
        }
        byte[] body = json.toString().getBytes(StandardCharsets.UTF_8);
        ByteArrayOutputStream finalPart = new ByteArrayOutputStream();
        finalPart.write(("--" + boundary + "\r\n").getBytes(StandardCharsets.US_ASCII));
        finalPart.write("Content-Type: application/json\r\n".getBytes(StandardCharsets.US_ASCII));
        finalPart.write("X-Remote-Event: exit\r\n".getBytes(StandardCharsets.US_ASCII));
        finalPart.write(("Content-Length: " + body.length + "\r\n\r\n").getBytes(StandardCharsets.US_ASCII));
        finalPart.write(body);
        finalPart.write(("\r\n--" + boundary + "--\r\n").getBytes(StandardCharsets.US_ASCII));
        out.end(Buffer.buffer(finalPart.toByteArray()));
    }

    private static List<File> listFiles(File directory) {
        List<File> result = new ArrayList<>();
        File[] files = directory.listFiles();
        if (files == null) {
            return result;
        }
        for (File file : files) {
            if (file.isDirectory()) {
                result.addAll(listFiles(file));
            } else if (!file.getName().endsWith(".tmp") || file.getName().endsWith(".m3u8.tmp")) {
                result.add(file);
            }
        }
        result.sort(Comparator.comparing(File::getAbsolutePath));
        return result;
    }

    private static void deleteRecursively(File file) {
        if (file == null || !file.exists()) {
            return;
        }
        if (file.isDirectory()) {
            File[] children = file.listFiles();
            if (children != null) {
                for (File child : children) {
                    deleteRecursively(child);
                }
            }
        }
        //noinspection ResultOfMethodCallIgnored
        file.delete();
    }

    private static final class FileSnapshot {
        final long length;
        final long lastModified;

        FileSnapshot(long length, long lastModified) {
            this.length = length;
            this.lastModified = lastModified;
        }

        @Override
        public boolean equals(Object obj) {
            if (!(obj instanceof FileSnapshot)) {
                return false;
            }
            FileSnapshot other = (FileSnapshot) obj;
            return length == other.length && lastModified == other.lastModified;
        }

        @Override
        public int hashCode() {
            return (int) (length ^ (length >>> 32) ^ lastModified ^ (lastModified >>> 32));
        }
    }

    private static final class JobState {
        final String id;
        final File workDir;
        final long startedAtMillis;
        final AtomicLong inputBytes = new AtomicLong();
        final AtomicLong outputFiles = new AtomicLong();
        volatile long lastActivityMillis;
        volatile String cancelReason = "";
        volatile io.reactiverse.childprocess.Process process;
        volatile File outputDir;
        volatile boolean processExited;
        volatile int exitCode = -1;
        final List<HttpServerResponse> stdoutSubscribers = new ArrayList<>();
        final AtomicLong stdoutBytes = new AtomicLong();
        final StringBuffer stdoutText = new StringBuffer();
        final StringBuffer stderrText = new StringBuffer();

        JobState(String id, File workDir) {
            this.id = id;
            this.workDir = workDir;
            this.startedAtMillis = System.currentTimeMillis();
            this.lastActivityMillis = startedAtMillis;
        }

        void setProcess(io.reactiverse.childprocess.Process process) {
            this.process = process;
            touch();
        }

        void addInputBytes(long bytes) {
            inputBytes.addAndGet(bytes);
            touch();
        }

        void addOutputFile() {
            outputFiles.incrementAndGet();
            touch();
        }

        void touchOutput() {
            touch();
        }

        private void touch() {
            lastActivityMillis = System.currentTimeMillis();
        }

        long ageMillis() {
            return Math.max(0, System.currentTimeMillis() - startedAtMillis);
        }

        long idleMillis() {
            return Math.max(0, System.currentTimeMillis() - lastActivityMillis);
        }

        void cancel(String reason) {
            cancelReason = reason;
            io.reactiverse.childprocess.Process activeProcess = process;
            if (activeProcess != null) {
                activeProcess.kill(true);
            }
        }

        void scheduleCancelIfStdoutStillOrphaned(String reason) {
            Thread cancelThread = new Thread(() -> {
                try {
                    Thread.sleep(STDOUT_SUBSCRIBER_GRACE_MS);
                } catch (InterruptedException ignored) {
                    Thread.currentThread().interrupt();
                    return;
                }

                synchronized (stdoutSubscribers) {
                    if (!stdoutSubscribers.isEmpty() || processExited) {
                        return;
                    }
                }
                cancel(reason);
            }, "jfat-stdout-orphan-cancel");
            cancelThread.setDaemon(true);
            cancelThread.start();
        }

        JSONObject toJson() throws Exception {
            return toJson(-1, "");
        }

        JSONObject toJson(int exitCode, String failure) throws Exception {
            io.reactiverse.childprocess.Process activeProcess = process;
            String stdout;
            String stderr;
            synchronized (stdoutText) {
                stdout = stdoutText.toString();
            }
            synchronized (stderrText) {
                stderr = stderrText.toString();
            }
            JSONObject json = new JSONObject()
                    .put("id", id)
                    .put("ageMillis", ageMillis())
                    .put("idleMillis", idleMillis())
                    .put("inputBytes", inputBytes.get())
                    .put("stdoutBytes", stdoutBytes.get())
                    .put("outputFiles", outputFiles.get())
                    .put("processAlive", activeProcess != null && !processExited)
                    .put("cancelReason", cancelReason);
            if (exitCode >= 0) {
                json.put("exitCode", exitCode);
            }
            if (!failure.isEmpty()) {
                json.put("failure", failure);
            }
            if (!stdout.isEmpty()) {
                json.put("stdoutTail", tail(stdout));
            }
            if (!stderr.isEmpty()) {
                json.put("stderrTail", tail(stderr));
            }
            return json;
        }

        private static String tail(String text) {
            return text.length() <= 4096 ? text : text.substring(text.length() - 4096);
        }
    }

    private static String readAll(InputStream in) throws IOException {
        ByteArrayOutputStream bytes = new ByteArrayOutputStream();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = in.read(buffer)) >= 0) {
            bytes.write(buffer, 0, read);
        }
        return bytes.toString(StandardCharsets.UTF_8.name());
    }

    private static void appendProcessOutput(JobState job, Buffer buffer, boolean stderr) {
        String text = buffer.toString(StandardCharsets.UTF_8);
        appendProcessText(stderr ? job.stderrText : job.stdoutText, text, stderr);
        job.touchOutput();
    }

    private static void streamProcessStdout(JobState job, Buffer buffer) {
        job.stdoutBytes.addAndGet(buffer.length());
        job.touchOutput();
        List<HttpServerResponse> subscribers;
        synchronized (job.stdoutSubscribers) {
            subscribers = new ArrayList<>(job.stdoutSubscribers);
        }
        for (HttpServerResponse subscriber : subscribers) {
            subscriber.write(buffer.copy()).onFailure(ex -> removeStdoutSubscriber(job, subscriber, true));
        }
    }

    private static void closeStdoutSubscribers(JobState job) {
        List<HttpServerResponse> subscribers;
        synchronized (job.stdoutSubscribers) {
            subscribers = new ArrayList<>(job.stdoutSubscribers);
            job.stdoutSubscribers.clear();
        }
        for (HttpServerResponse subscriber : subscribers) {
            if (!subscriber.ended()) {
                subscriber.end();
            }
        }
    }

    private static void removeStdoutSubscriber(JobState job, HttpServerResponse subscriber, boolean cancelIfOrphaned) {
        boolean shouldCancel = false;
        synchronized (job.stdoutSubscribers) {
            job.stdoutSubscribers.remove(subscriber);
            shouldCancel = cancelIfOrphaned && job.stdoutSubscribers.isEmpty() && !job.processExited;
        }
        if (shouldCancel) {
            job.scheduleCancelIfStdoutStillOrphaned("stdout-disconnected");
        }
    }

    static long stdoutSubscriberGraceMillisForTest() {
        return STDOUT_SUBSCRIBER_GRACE_MS;
    }

    private static void appendProcessText(StringBuffer textBuffer, String text, boolean stderr) {
        synchronized (textBuffer) {
            textBuffer.append(text);
            int overflow = textBuffer.length() - PROCESS_TEXT_TAIL_CHARS;
            if (overflow > 0) {
                textBuffer.delete(0, overflow);
            }
        }
        logProcessText(text, stderr);
    }

    private static void logProcessText(String text, boolean stderr) {
        if (text.length() <= ANDROID_LOG_CHUNK_CHARS) {
            if (stderr) {
                Log.w(TAG, text);
            } else {
                Log.i(TAG, text);
            }
            return;
        }
        for (int offset = 0; offset < text.length(); offset += ANDROID_LOG_CHUNK_CHARS) {
            String chunk = text.substring(offset, Math.min(text.length(), offset + ANDROID_LOG_CHUNK_CHARS));
            if (stderr) {
                Log.w(TAG, chunk);
            } else {
                Log.i(TAG, chunk);
            }
        }
    }

    static void appendProcessTextForTest(StringBuffer textBuffer, String text, boolean stderr) {
        appendProcessText(textBuffer, text, stderr);
    }

    static int processTextTailCharsForTest() {
        return PROCESS_TEXT_TAIL_CHARS;
    }

    static int androidLogChunkCharsForTest() {
        return ANDROID_LOG_CHUNK_CHARS;
    }

    private void createChannel() {
        if (Build.VERSION.SDK_INT >= 26) {
            NotificationChannel channel = new NotificationChannel(CHANNEL, "Transcoder", NotificationManager.IMPORTANCE_LOW);
            NotificationManager manager = getSystemService(NotificationManager.class);
            manager.createNotificationChannel(channel);
        }
    }

    private Notification notification(String text) {
        Notification.Builder builder = Build.VERSION.SDK_INT >= 26
                ? new Notification.Builder(this, CHANNEL)
                : new Notification.Builder(this);
        return builder
                .setContentTitle("Android Transcoder")
                .setContentText(text)
                .setSmallIcon(android.R.drawable.stat_sys_upload)
                .build();
    }

    private static final class Request {
        final String path;
        final Map<String, String> headers;
        final String body;

        Request(String path, Map<String, String> headers, String body) {
            this.path = path;
            this.headers = headers;
            this.body = body;
        }

        static Request from(RoutingContext ctx) {
            return from(ctx, "");
        }

        static Request from(RoutingContext ctx, String body) {
            Map<String, String> headers = new HashMap<>();
            for (Map.Entry<String, String> header : ctx.request().headers()) {
                headers.put(header.getKey().toLowerCase(), header.getValue());
            }
            return new Request(ctx.normalizedPath(), headers, body);
        }
    }

}
