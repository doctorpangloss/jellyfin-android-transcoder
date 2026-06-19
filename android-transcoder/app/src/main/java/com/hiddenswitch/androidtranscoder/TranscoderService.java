package com.hiddenswitch.androidtranscoder;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Intent;
import android.os.Build;
import android.os.IBinder;

import org.json.JSONObject;

import java.io.BufferedInputStream;
import java.io.BufferedOutputStream;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.ServerSocket;
import java.net.Socket;
import java.net.URLDecoder;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.atomic.AtomicInteger;

public class TranscoderService extends Service {
    private static final String CHANNEL = "transcoder";
    private static final AtomicInteger ACTIVE_JOBS = new AtomicInteger();
    private static volatile boolean running;

    private ExecutorService executor;
    private ServerSocket server;
    private Thread acceptThread;

    static boolean isRunning() {
        return running;
    }

    @Override
    public void onCreate() {
        super.onCreate();
        createChannel();
        startForeground(1, notification("Listening on :" + AppConfig.PORT));
        executor = Executors.newCachedThreadPool();
        startServer();
        running = true;
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
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
                while (!Thread.currentThread().isInterrupted()) {
                    Socket socket = server.accept();
                    executor.submit(() -> handle(socket));
                }
            } catch (IOException ignored) {
            }
        }, "android-transcoder-http");
        acceptThread.start();
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
            if (request.path.equals("/api/v1/transcode") && request.method.equals("POST")) {
                transcode(request, in, out);
                return;
            }
            writeText(out, 404, "not found\n");
        } catch (Exception ignored) {
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
        JSONObject obj = new JSONObject();
        obj.put("name", "HiddenSwitch Android Transcoder");
        obj.put("version", "0.1.0");
        obj.put("activeJobs", ACTIVE_JOBS.get());
        obj.put("maxJobs", 1);
        obj.put("ffmpegPath", AppConfig.ffmpegPath(this));
        obj.put("tokenRequired", true);
        obj.put("capabilities", new JSONObject()
                .put("input", "matroska")
                .put("output", "mpegts")
                .put("videoCodec", "h264")
                .put("hardware", "mediacodec-gles"));
        return obj;
    }

    private void transcode(Request request, InputStream requestBody, OutputStream response) throws Exception {
        if (!ACTIVE_JOBS.compareAndSet(0, 1)) {
            writeText(response, 429, "busy\n");
            return;
        }
        Process process = null;
        try {
            List<String> command = ffmpegCommand(request.query);
            process = new ProcessBuilder(command).redirectErrorStream(false).start();
            Process ffmpeg = process;
            Thread stdin = new Thread(() -> pipeRequestBody(request, requestBody, ffmpeg), "ffmpeg-stdin");
            Thread stderr = new Thread(() -> drain(ffmpeg.getErrorStream()), "ffmpeg-stderr");
            stdin.start();
            stderr.start();

            writeHeaders(response, 200, "video/MP2T", true);
            pipeChunked(ffmpeg.getInputStream(), response);
            stdin.join();
            stderr.join();
            int exit = ffmpeg.waitFor();
            if (exit != 0) {
                throw new IOException("ffmpeg exited " + exit);
            }
        } finally {
            if (process != null) {
                process.destroyForcibly();
            }
            ACTIVE_JOBS.set(0);
        }
    }

    private List<String> ffmpegCommand(Map<String, String> query) {
        String width = query.getOrDefault("width", "1920");
        String height = query.getOrDefault("height", "1080");
        String bitrate = query.getOrDefault("bitrate", "6000000");
        String maxrate = query.getOrDefault("maxrate", bitrate);
        String bufsize = query.getOrDefault("bufsize", "12000000");
        String gop = query.getOrDefault("gop", "120");
        String toneMap = query.getOrDefault("toneMap", "0");

        List<String> args = new ArrayList<>();
        args.add(AppConfig.ffmpegPath(this));
        args.add("-hide_banner");
        args.add("-loglevel");
        args.add("warning");
        args.add("-init_hw_device");
        args.add("mediacodec=mc,create_window=1,surface_processor=1");
        args.add("-hwaccel");
        args.add("mediacodec");
        args.add("-hwaccel_device");
        args.add("mc");
        args.add("-hwaccel_output_format");
        args.add("mediacodec");
        args.add("-i");
        args.add("pipe:0");
        args.add("-map");
        args.add("0:v:0");
        args.add("-c:v");
        args.add("h264_mediacodec");
        args.add("-pix_fmt");
        args.add("mediacodec");
        args.add("-output_width");
        args.add(width);
        args.add("-output_height");
        args.add(height);
        args.add("-surface_tonemap");
        args.add(toneMap);
        args.add("-b:v");
        args.add(bitrate);
        args.add("-maxrate");
        args.add(maxrate);
        args.add("-bufsize");
        args.add(bufsize);
        args.add("-bitrate_mode");
        args.add("cbr");
        args.add("-g");
        args.add(gop);
        args.add("-an");
        args.add("-sn");
        args.add("-dn");
        args.add("-f");
        args.add("mpegts");
        args.add("pipe:1");
        return args;
    }

    private void pipeRequestBody(Request request, InputStream in, Process process) {
        try (OutputStream stdin = process.getOutputStream()) {
            if ("chunked".equals(request.headers.get("transfer-encoding"))) {
                copyChunkedRequest(in, stdin);
            } else {
                long length = Long.parseLong(request.headers.getOrDefault("content-length", "0"));
                copyFixed(in, stdin, length);
            }
        } catch (Exception ignored) {
        }
    }

    private static void writeJson(OutputStream out, JSONObject json) throws IOException {
        byte[] body = (json.toString() + "\n").getBytes(StandardCharsets.UTF_8);
        writeHeaders(out, 200, "application/json", false, body.length);
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

    private static void copyChunkedRequest(InputStream in, OutputStream out) throws IOException {
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
            copyFixed(in, out, size);
            readLine(in);
        }
    }

    private static void copyFixed(InputStream in, OutputStream out, long length) throws IOException {
        byte[] buffer = new byte[65536];
        long remaining = length;
        while (remaining > 0) {
            int read = in.read(buffer, 0, (int) Math.min(buffer.length, remaining));
            if (read < 0) {
                break;
            }
            out.write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static void drain(InputStream in) {
        try {
            byte[] buffer = new byte[8192];
            while (in.read(buffer) >= 0) {
            }
        } catch (IOException ignored) {
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
