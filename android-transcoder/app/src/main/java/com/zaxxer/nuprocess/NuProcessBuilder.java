package com.zaxxer.nuprocess;

import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.ByteBuffer;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.Objects;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.LinkedBlockingQueue;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;

public final class NuProcessBuilder {
    private final List<String> command;
    private final Map<String, String> environment;
    private NuProcessHandler processListener;
    private Path cwd;

    public NuProcessBuilder(List<String> command, Map<String, String> environment) {
        this.command = new ArrayList<>(Objects.requireNonNull(command, "command"));
        this.environment = Objects.requireNonNull(environment, "environment");
    }

    public void setProcessListener(NuProcessHandler listener) {
        processListener = Objects.requireNonNull(listener, "listener");
    }

    public void setCwd(Path cwd) {
        this.cwd = cwd;
    }

    public NuProcess start() {
        if (processListener == null) {
            throw new IllegalArgumentException("NuProcessHandler not specified");
        }
        AndroidNuProcess process = new AndroidNuProcess(command, environment, cwd, processListener);
        return process.start() ? process : null;
    }

    public void run() {
        NuProcess process = start();
        if (process != null) {
            try {
                process.waitFor(0, TimeUnit.MILLISECONDS);
            } catch (InterruptedException ex) {
                Thread.currentThread().interrupt();
            }
        }
    }

    private static final class AndroidNuProcess implements NuProcess {
        private static final ByteBuffer CLOSE_STDIN = ByteBuffer.allocate(0);
        private static final ByteBuffer WANT_STDIN = ByteBuffer.allocate(0);
        private static final int BUFFER_SIZE = 64 * 1024;

        private final List<String> command;
        private final Map<String, String> environment;
        private final Path cwd;
        private volatile NuProcessHandler handler;
        private final LinkedBlockingQueue<ByteBuffer> pendingWrites = new LinkedBlockingQueue<>();
        private final CountDownLatch exitLatch = new CountDownLatch(1);
        private final AtomicBoolean running = new AtomicBoolean();
        private final AtomicBoolean stdinClosed = new AtomicBoolean();
        private volatile Process process;
        private volatile int exitCode = Integer.MIN_VALUE;

        AndroidNuProcess(List<String> command, Map<String, String> environment, Path cwd, NuProcessHandler handler) {
            this.command = command;
            this.environment = environment;
            this.cwd = cwd;
            this.handler = handler;
        }

        boolean start() {
            try {
                handler.onPreStart(this);
                ProcessBuilder builder = new ProcessBuilder(command);
                builder.environment().clear();
                builder.environment().putAll(environment);
                if (cwd != null) {
                    builder.directory(new File(cwd.toString()));
                }
                process = builder.start();
                running.set(true);
                handler.onStart(this);
                startDaemon("nuprocess-stdout", () -> drain(process.getInputStream(), true));
                startDaemon("nuprocess-stderr", () -> drain(process.getErrorStream(), false));
                startDaemon("nuprocess-stdin", this::pumpStdin);
                startDaemon("nuprocess-wait", this::waitForExit);
                return true;
            } catch (Throwable ex) {
                exitCode = Integer.MIN_VALUE;
                running.set(false);
                exitLatch.countDown();
                handler.onExit(exitCode);
                return false;
            }
        }

        private void drain(InputStream input, boolean stdout) {
            byte[] bytes = new byte[BUFFER_SIZE];
            try {
                int read;
                while ((read = input.read(bytes)) >= 0) {
                    ByteBuffer buffer = ByteBuffer.wrap(bytes, 0, read);
                    if (stdout) {
                        safeStdout(buffer, false);
                    } else {
                        safeStderr(buffer, false);
                    }
                }
            } catch (IOException ignored) {
            } finally {
                if (stdout) {
                    safeStdout(null, true);
                } else {
                    safeStderr(null, true);
                }
            }
        }

        private void pumpStdin() {
            byte[] bytes = new byte[BUFFER_SIZE];
            try (OutputStream output = process.getOutputStream()) {
                while (true) {
                    ByteBuffer next = pendingWrites.take();
                    if (next == CLOSE_STDIN) {
                        return;
                    }
                    if (next == WANT_STDIN) {
                        pumpHandlerStdin(output, bytes);
                        continue;
                    }
                    while (next.hasRemaining()) {
                        writeBuffer(output, next, bytes);
                    }
                    output.flush();
                }
            } catch (IOException ignored) {
            } catch (InterruptedException ex) {
                Thread.currentThread().interrupt();
            }
        }

        private void pumpHandlerStdin(OutputStream output, byte[] bytes) throws IOException {
            boolean more;
            do {
                ByteBuffer buffer = ByteBuffer.allocate(BUFFER_SIZE);
                more = handler.onStdinReady(buffer);
                while (buffer.hasRemaining()) {
                    writeBuffer(output, buffer, bytes);
                }
                output.flush();
            } while (more);
        }

        private static void writeBuffer(OutputStream output, ByteBuffer buffer, byte[] bytes) throws IOException {
            int length = Math.min(buffer.remaining(), bytes.length);
            buffer.get(bytes, 0, length);
            output.write(bytes, 0, length);
        }

        private void waitForExit() {
            try {
                exitCode = process.waitFor();
            } catch (InterruptedException ex) {
                Thread.currentThread().interrupt();
                exitCode = Integer.MIN_VALUE;
            } finally {
                running.set(false);
                exitLatch.countDown();
                safeExit(exitCode);
            }
        }

        private void safeStdout(ByteBuffer buffer, boolean closed) {
            try {
                handler.onStdout(buffer, closed);
            } catch (RuntimeException ignored) {
            }
        }

        private void safeStderr(ByteBuffer buffer, boolean closed) {
            try {
                handler.onStderr(buffer, closed);
            } catch (RuntimeException ignored) {
            }
        }

        private void safeExit(int code) {
            try {
                handler.onExit(code);
            } catch (RuntimeException ignored) {
            }
        }

        @Override
        public int getPID() {
            return -1;
        }

        @Override
        public int getPid() {
            return getPID();
        }

        @Override
        public boolean isRunning() {
            return running.get();
        }

        @Override
        public int waitFor(long timeout, TimeUnit unit) throws InterruptedException {
            if (timeout == 0) {
                exitLatch.await();
            } else if (!exitLatch.await(timeout, unit)) {
                return Integer.MIN_VALUE;
            }
            return exitCode;
        }

        @Override
        public void destroy(boolean force) {
            Process active = process;
            if (active == null) {
                return;
            }
            if (force) {
                active.destroyForcibly();
            } else {
                active.destroy();
            }
        }

        @Override
        public void wantWrite() {
            if (!stdinClosed.get()) {
                pendingWrites.offer(WANT_STDIN);
            }
        }

        @Override
        public void closeStdin(boolean force) {
            if (stdinClosed.compareAndSet(false, true)) {
                pendingWrites.offer(CLOSE_STDIN);
            }
        }

        @Override
        public void writeStdin(ByteBuffer buffer) {
            pendingWrites.offer(buffer.slice());
        }

        @Override
        public boolean hasPendingWrites() {
            return !pendingWrites.isEmpty();
        }

        @Override
        public void setProcessHandler(NuProcessHandler processHandler) {
            handler = processHandler;
        }

        private static void startDaemon(String name, Runnable runnable) {
            Thread thread = new Thread(runnable, name);
            thread.setDaemon(true);
            thread.start();
        }
    }
}
