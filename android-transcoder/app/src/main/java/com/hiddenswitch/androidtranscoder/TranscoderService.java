package com.hiddenswitch.androidtranscoder;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Intent;
import android.net.wifi.WifiManager;
import android.os.Build;
import android.os.IBinder;
import android.os.PowerManager;
import android.util.Log;

import org.json.JSONObject;
import org.json.JSONArray;

import java.io.BufferedInputStream;
import java.io.BufferedOutputStream;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.ServerSocket;
import java.net.Socket;
import java.net.HttpURLConnection;
import java.net.URL;
import java.net.URLDecoder;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.ArrayList;
import java.util.Base64;
import java.util.Comparator;
import java.util.HashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.UUID;
import java.util.HashSet;
import java.util.Set;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicLong;
import java.util.concurrent.atomic.AtomicReference;

public class TranscoderService extends Service {
    public static final String ACTION_REFRESH_POWER = "com.hiddenswitch.androidtranscoder.REFRESH_POWER";
    private static final String CHANNEL = "transcoder";
    private static final String TAG = "AndroidTranscoder";
    private static final long JOB_IDLE_TIMEOUT_MS = TimeUnit.SECONDS.toMillis(90);
    private static final long JOB_MAX_RUNTIME_MS = TimeUnit.MINUTES.toMillis(30);
    private static final AtomicInteger ACTIVE_JOBS = new AtomicInteger();
    private static final AtomicInteger ACCEPTED_JOBS = new AtomicInteger();
    private static final AtomicInteger COMPLETED_JOBS = new AtomicInteger();
    private static final AtomicLong INPUT_BYTES = new AtomicLong();
    private static final AtomicReference<JobState> CURRENT_JOB = new AtomicReference<>();
    private static volatile boolean running;

    private ExecutorService executor;
    private ServerSocket server;
    private Thread acceptThread;
    private PowerManager.WakeLock wakeLock;
    private WifiManager.WifiLock wifiLock;

    static boolean isRunning() {
        return running;
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
        try {
            if (server != null) {
                server.close();
            }
        } catch (IOException ignored) {
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
        acceptThread = new Thread(() -> {
            try {
                server = new ServerSocket(AppConfig.PORT);
                Log.i(TAG, "Listening on " + AppConfig.PORT);
                while (!Thread.currentThread().isInterrupted()) {
                    Socket socket = server.accept();
                    executor.submit(() -> handle(socket));
                }
            } catch (IOException ex) {
                Log.e(TAG, "Server failed", ex);
            }
        }, "android-transcoder-http");
        acceptThread.start();
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

    private void handle(Socket socket) {
        try {
            socket.setSoTimeout(120000);
            BufferedInputStream in = new BufferedInputStream(socket.getInputStream());
            BufferedOutputStream out = new BufferedOutputStream(socket.getOutputStream());
            Request request = Request.read(in);
            if (request == null) {
                return;
            }
            if (request.path.equals("/api/v1/status") && request.method.equals("GET")) {
                writeJson(out, statusJson());
                return;
            }
            if (!authorized(request)) {
                writeText(out, 401, "unauthorized\n");
                return;
            }
            if (request.path.equals("/api/v1/remoteprocesses/current") && request.method.equals("DELETE")) {
                cancelCurrentJob(out);
                return;
            }
            if (request.path.equals("/api/v1/remoteprocesses") && request.method.equals("POST")) {
                remoteProcess(request, in, out);
                return;
            }
            writeText(out, 404, "not found\n");
        } catch (Exception ex) {
            Log.e(TAG, "Request failed", ex);
        } finally {
            try {
                socket.close();
            } catch (IOException ignored) {
            }
        }
    }

    private boolean authorized(Request request) {
        String auth = request.headers.get("authorization");
        return auth != null && auth.equals("Bearer " + AppConfig.token(this));
    }

    private JSONObject statusJson() throws Exception {
        reapStaleJob("status");
        JSONObject obj = new JSONObject();
        obj.put("name", "HiddenSwitch Android Transcoder");
        obj.put("version", "0.1.0");
        obj.put("activeJobs", ACTIVE_JOBS.get());
        obj.put("acceptedJobs", ACCEPTED_JOBS.get());
        obj.put("completedJobs", COMPLETED_JOBS.get());
        obj.put("inputBytes", INPUT_BYTES.get());
        obj.put("maxJobs", 1);
        obj.put("ffmpegPath", AppConfig.ffmpegPath(this));
        obj.put("tokenRequired", true);
        obj.put("startOnBoot", AppConfig.startOnBoot(this));
        obj.put("keepAwake", AppConfig.keepAwake(this));
        JSONArray jobs = new JSONArray();
        JobState job = CURRENT_JOB.get();
        if (job != null) {
            jobs.put(job.toJson());
        }
        obj.put("jobs", jobs);
        obj.put("jobIdleTimeoutMillis", JOB_IDLE_TIMEOUT_MS);
        obj.put("jobMaxRuntimeMillis", JOB_MAX_RUNTIME_MS);
        obj.put("capabilities", new JSONObject()
                .put("api", "remoteprocesses")
                .put("output", "multipart/mixed")
                .put("executables", new JSONArray().put("ffmpeg"))
                .put("hardware", "mediacodec-gles"));
        return obj;
    }

    private void remoteProcess(Request request, InputStream requestBody, OutputStream response) throws Exception {
        reapStaleJob("new request");
        JobState job = new JobState(UUID.randomUUID().toString(), new File(getCacheDir(), "remoteprocesses/" + UUID.randomUUID()));
        if (!CURRENT_JOB.compareAndSet(null, job)) {
            writeText(response, 429, "busy\n");
            return;
        }
        ACTIVE_JOBS.set(1);
        Process process = null;
        try {
            ACCEPTED_JOBS.incrementAndGet();
            File outputDir = new File(job.workDir, "out");
            if (!outputDir.mkdirs() && !outputDir.isDirectory()) {
                throw new IOException("Failed to create " + outputDir);
            }
            List<String> command = remoteProcessCommand(request, outputDir);
            process = new ProcessBuilder(command).redirectErrorStream(false).start();
            job.setProcess(process);
            Process child = process;
            StringBuffer stderrText = new StringBuffer();
            Thread stdin = new Thread(() -> pipeRequestBody(job, request, requestBody, child), "remoteprocess-stdin");
            Thread stdout = new Thread(() -> drainStream(child.getInputStream()), "remoteprocess-stdout");
            Thread stderr = new Thread(() -> logStream(child.getErrorStream(), stderrText), "remoteprocess-stderr");
            stdin.setDaemon(true);
            stdout.setDaemon(true);
            stderr.setDaemon(true);
            stdin.start();
            stdout.start();
            stderr.start();

            int exit = streamMultipartFiles(response, outputDir, child, stderrText, job);
            stdout.join(2000);
            stderr.join(2000);
            if (exit == 0) {
                COMPLETED_JOBS.incrementAndGet();
            }
        } finally {
            if (process != null) {
                process.destroyForcibly();
            }
            deleteRecursively(job.workDir);
            CURRENT_JOB.compareAndSet(job, null);
            ACTIVE_JOBS.set(0);
        }
    }

    private List<String> remoteProcessCommand(Request request, File outputDir) throws Exception {
        String executable = request.headers.getOrDefault("x-remote-executable", "");
        if (!"ffmpeg".equals(executable)) {
            throw new IOException("unsupported executable: " + executable);
        }
        JSONArray jsonArgs = new JSONArray(new String(Base64.getUrlDecoder().decode(
                request.headers.getOrDefault("x-remote-args", "")), StandardCharsets.UTF_8));
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
        try {
            JSONObject payload = new JSONObject(AppConfig.connectionJson(this));
            HttpURLConnection connection = (HttpURLConnection) new URL(pairUrl).openConnection();
            connection.setRequestMethod("POST");
            connection.setConnectTimeout(10000);
            connection.setReadTimeout(10000);
            connection.setDoOutput(true);
            connection.setRequestProperty("Content-Type", "application/json");
            byte[] body = payload.toString().getBytes(StandardCharsets.UTF_8);
            connection.setFixedLengthStreamingMode(body.length);
            try (OutputStream out = connection.getOutputStream()) {
                out.write(body);
            }
            int status = connection.getResponseCode();
            InputStream response = status >= 200 && status < 300 ? connection.getInputStream() : connection.getErrorStream();
            String text = response == null ? "" : readAll(response);
            if (status < 200 || status >= 300) {
                Log.w(TAG, "Pairing failed: HTTP " + status + " " + text);
                return;
            }
            JSONObject json = new JSONObject(text);
            String token = json.optString("token", "");
            if (!token.isEmpty()) {
                AppConfig.setToken(this, token);
            }
            Log.i(TAG, "Paired with Jellyfin at " + pairUrl);
        } catch (Exception ex) {
            Log.w(TAG, "Pairing failed", ex);
        }
    }

    private void pipeRequestBody(JobState job, Request request, InputStream in, Process process) {
        try (OutputStream stdin = process.getOutputStream()) {
            if ("chunked".equals(request.headers.get("transfer-encoding"))) {
                copyChunkedRequest(job, in, stdin);
            } else {
                long length = Long.parseLong(request.headers.getOrDefault("content-length", "0"));
                copyFixed(job, in, stdin, length);
            }
        } catch (Exception ignored) {
        }
    }

    private static void drainStream(InputStream in) {
        try {
            byte[] buffer = new byte[65536];
            while (in.read(buffer) >= 0) {
            }
        } catch (IOException ignored) {
        }
    }

    private static void writeJson(OutputStream out, JSONObject json) throws IOException {
        writeJson(out, 200, json);
    }

    private static void writeJson(OutputStream out, int status, JSONObject json) throws IOException {
        byte[] body = (json.toString() + "\n").getBytes(StandardCharsets.UTF_8);
        writeHeaders(out, status, "application/json", false, body.length);
        out.write(body);
        out.flush();
    }

    private static void writeText(OutputStream out, int status, String bodyText) throws IOException {
        byte[] body = bodyText.getBytes(StandardCharsets.UTF_8);
        writeHeaders(out, status, "text/plain", false, body.length);
        out.write(body);
        out.flush();
    }

    private static void writeHeaders(OutputStream out, int status, String contentType, boolean chunked) throws IOException {
        writeHeaders(out, status, contentType, chunked, -1);
    }

    private static void writeHeaders(OutputStream out, int status, String contentType, boolean chunked, long length) throws IOException {
        String reason = status == 200 ? "OK" : status == 401 ? "Unauthorized" : status == 429 ? "Too Many Requests" : "Error";
        StringBuilder headers = new StringBuilder();
        headers.append("HTTP/1.1 ").append(status).append(' ').append(reason).append("\r\n");
        headers.append("Content-Type: ").append(contentType).append("\r\n");
        if (chunked) {
            headers.append("Transfer-Encoding: chunked\r\n");
        } else {
            headers.append("Content-Length: ").append(length).append("\r\n");
        }
        headers.append("Connection: close\r\n\r\n");
        out.write(headers.toString().getBytes(StandardCharsets.US_ASCII));
        out.flush();
    }

    private void cancelCurrentJob(OutputStream out) throws Exception {
        JobState job = CURRENT_JOB.get();
        JSONObject response = new JSONObject();
        if (job == null) {
            response.put("canceled", false);
            response.put("reason", "no active job");
            writeJson(out, 404, response);
            return;
        }
        job.cancel("api");
        response.put("canceled", true);
        response.put("job", job.toJson());
        writeJson(out, response);
    }

    private static int streamMultipartFiles(OutputStream out, File outputDir, Process process, StringBuffer stderrText, JobState job) throws IOException, InterruptedException {
        String boundary = "jfat-" + UUID.randomUUID();
        writeHeaders(out, 200, "multipart/mixed; boundary=" + boundary, true);
        Map<String, FileSnapshot> observed = new HashMap<>();
        Set<String> sent = new HashSet<>();
        while (process.isAlive()) {
            enforceLiveness(job, process);
            streamStableFiles(out, outputDir, boundary, observed, sent, job);
            Thread.sleep(100);
        }
        if (!process.waitFor(120, TimeUnit.SECONDS)) {
            process.destroyForcibly();
            throw new IOException("remote process timed out");
        }
        int exitCode = process.exitValue();
        for (int i = 0; i < 5; i++) {
            streamStableFiles(out, outputDir, boundary, observed, sent, job);
            Thread.sleep(100);
        }
        writeExitPart(out, boundary, exitCode, stderrText.toString());
        return exitCode;
    }

    private static void enforceLiveness(JobState job, Process process) throws IOException {
        long age = job.ageMillis();
        long idle = job.idleMillis();
        if (age > JOB_MAX_RUNTIME_MS) {
            job.cancel("max-runtime");
            throw new IOException("remote process exceeded max runtime");
        }
        if (idle > JOB_IDLE_TIMEOUT_MS) {
            job.cancel("idle-timeout");
            throw new IOException("remote process idle timeout");
        }
        if (!process.isAlive()) {
            job.touchOutput();
        }
    }

    private void reapStaleJob(String reason) {
        JobState job = CURRENT_JOB.get();
        if (job == null) {
            return;
        }
        Process process = job.process;
        if ((process != null && !process.isAlive()) || job.ageMillis() > JOB_MAX_RUNTIME_MS || job.idleMillis() > JOB_IDLE_TIMEOUT_MS) {
            Log.w(TAG, "Reaping stale remote process " + job.id + " during " + reason);
            job.cancel("reaped-" + reason);
            if (CURRENT_JOB.compareAndSet(job, null)) {
                deleteRecursively(job.workDir);
                ACTIVE_JOBS.set(0);
            }
        }
    }

    private static void streamStableFiles(OutputStream out, File outputDir, String boundary, Map<String, FileSnapshot> observed, Set<String> sent, JobState job) throws IOException {
        for (File file : listFiles(outputDir)) {
            String relative = outputDir.toPath().relativize(file.toPath()).toString().replace(File.separatorChar, '/');
            FileSnapshot snapshot = new FileSnapshot(file.length(), file.lastModified());
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

    private static void writeFilePart(OutputStream out, String boundary, File outputDir, File file, String relative) throws IOException {
        ByteArrayOutputStream part = new ByteArrayOutputStream();
        part.write(("--" + boundary + "\r\n").getBytes(StandardCharsets.US_ASCII));
        part.write("Content-Type: application/octet-stream\r\n".getBytes(StandardCharsets.US_ASCII));
        part.write(("Content-Disposition: attachment; filename=\"" + relative + "\"\r\n").getBytes(StandardCharsets.US_ASCII));
        part.write(("X-Remote-Path: " + relative + "\r\n").getBytes(StandardCharsets.US_ASCII));
        part.write("X-Remote-Event: upsert\r\n".getBytes(StandardCharsets.US_ASCII));
        part.write(("Content-Length: " + file.length() + "\r\n\r\n").getBytes(StandardCharsets.US_ASCII));
        Files.copy(file.toPath(), part);
        part.write("\r\n".getBytes(StandardCharsets.US_ASCII));
        writeChunk(out, part.toByteArray());
    }

    private static void writeExitPart(OutputStream out, String boundary, int exitCode, String stderr) throws IOException {
        JSONObject json = new JSONObject();
        try {
            json.put("exitCode", exitCode);
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
        writeChunk(out, finalPart.toByteArray());
        out.write("0\r\n\r\n".getBytes(StandardCharsets.US_ASCII));
        out.flush();
    }

    private static void writeChunk(OutputStream out, byte[] bytes) throws IOException {
        out.write(Integer.toHexString(bytes.length).getBytes(StandardCharsets.US_ASCII));
        out.write("\r\n".getBytes(StandardCharsets.US_ASCII));
        out.write(bytes);
        out.write("\r\n".getBytes(StandardCharsets.US_ASCII));
        out.flush();
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
            } else if (!file.getName().endsWith(".tmp")) {
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
        volatile Process process;

        JobState(String id, File workDir) {
            this.id = id;
            this.workDir = workDir;
            this.startedAtMillis = System.currentTimeMillis();
            this.lastActivityMillis = startedAtMillis;
        }

        void setProcess(Process process) {
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
            Process activeProcess = process;
            if (activeProcess != null) {
                activeProcess.destroyForcibly();
            }
        }

        JSONObject toJson() throws Exception {
            Process activeProcess = process;
            return new JSONObject()
                    .put("id", id)
                    .put("ageMillis", ageMillis())
                    .put("idleMillis", idleMillis())
                    .put("inputBytes", inputBytes.get())
                    .put("outputFiles", outputFiles.get())
                    .put("processAlive", activeProcess != null && activeProcess.isAlive())
                    .put("cancelReason", cancelReason);
        }
    }

    private static void pipeChunked(InputStream in, OutputStream out) throws IOException {
        byte[] buffer = new byte[65536];
        int read;
        while ((read = in.read(buffer)) >= 0) {
            if (read == 0) {
                continue;
            }
            out.write(Integer.toHexString(read).getBytes(StandardCharsets.US_ASCII));
            out.write("\r\n".getBytes(StandardCharsets.US_ASCII));
            out.write(buffer, 0, read);
            out.write("\r\n".getBytes(StandardCharsets.US_ASCII));
            out.flush();
        }
        out.write("0\r\n\r\n".getBytes(StandardCharsets.US_ASCII));
        out.flush();
    }

    private static void copyChunkedRequest(JobState job, InputStream in, OutputStream out) throws IOException {
        while (true) {
            String sizeLine = readLine(in);
            if (sizeLine == null) {
                return;
            }
            int semicolon = sizeLine.indexOf(';');
            String hex = semicolon >= 0 ? sizeLine.substring(0, semicolon) : sizeLine;
            int size = Integer.parseInt(hex.trim(), 16);
            if (size == 0) {
                readLine(in);
                return;
            }
            copyFixed(job, in, out, size);
            readLine(in);
        }
    }

    private static void copyFixed(JobState job, InputStream in, OutputStream out, long length) throws IOException {
        byte[] buffer = new byte[65536];
        long remaining = length;
        while (remaining > 0) {
            int read = in.read(buffer, 0, (int) Math.min(buffer.length, remaining));
            if (read < 0) {
                break;
            }
            out.write(buffer, 0, read);
            INPUT_BYTES.addAndGet(read);
            job.addInputBytes(read);
            remaining -= read;
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

    private static void logStream(InputStream in, StringBuffer stderrText) {
        try {
            ByteArrayOutputStream line = new ByteArrayOutputStream();
            int c;
            while ((c = in.read()) >= 0) {
                if (c == '\n') {
                    String text = line.toString(StandardCharsets.UTF_8.name());
                    stderrText.append(text).append('\n');
                    Log.w(TAG, text);
                    line.reset();
                } else {
                    line.write(c);
                }
            }
            if (line.size() > 0) {
                String text = line.toString(StandardCharsets.UTF_8.name());
                stderrText.append(text).append('\n');
                Log.w(TAG, text);
            }
        } catch (IOException ex) {
            Log.w(TAG, "Failed reading ffmpeg stderr", ex);
        }
    }

    private static String readLine(InputStream in) throws IOException {
        ByteArrayOutputStream bytes = new ByteArrayOutputStream();
        int c;
        while ((c = in.read()) >= 0) {
            if (c == '\r') {
                int next = in.read();
                if (next == '\n') {
                    break;
                }
                bytes.write(c);
                if (next >= 0) {
                    bytes.write(next);
                }
            } else if (c == '\n') {
                break;
            } else {
                bytes.write(c);
            }
        }
        if (c < 0 && bytes.size() == 0) {
            return null;
        }
        return bytes.toString(StandardCharsets.US_ASCII.name());
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
        final String method;
        final String path;
        final Map<String, String> query;
        final Map<String, String> headers;

        Request(String method, String path, Map<String, String> query, Map<String, String> headers) {
            this.method = method;
            this.path = path;
            this.query = query;
            this.headers = headers;
        }

        static Request read(InputStream in) throws IOException {
            String requestLine = readLine(in);
            if (requestLine == null || requestLine.isEmpty()) {
                return null;
            }
            String[] parts = requestLine.split(" ");
            if (parts.length < 2) {
                return null;
            }
            Map<String, String> headers = new HashMap<>();
            String line;
            while ((line = readLine(in)) != null && !line.isEmpty()) {
                int colon = line.indexOf(':');
                if (colon > 0) {
                    headers.put(line.substring(0, colon).trim().toLowerCase(Locale.ROOT),
                            line.substring(colon + 1).trim());
                }
            }
            String target = parts[1];
            int question = target.indexOf('?');
            String path = question >= 0 ? target.substring(0, question) : target;
            Map<String, String> query = question >= 0 ? parseQuery(target.substring(question + 1)) : new HashMap<>();
            return new Request(parts[0], path, query, headers);
        }

        private static Map<String, String> parseQuery(String queryString) throws IOException {
            Map<String, String> result = new HashMap<>();
            for (String part : queryString.split("&")) {
                if (part.isEmpty()) {
                    continue;
                }
                int equals = part.indexOf('=');
                String key = equals >= 0 ? part.substring(0, equals) : part;
                String value = equals >= 0 ? part.substring(equals + 1) : "";
                result.put(URLDecoder.decode(key, "UTF-8"), URLDecoder.decode(value, "UTF-8"));
            }
            return result;
        }
    }
}
