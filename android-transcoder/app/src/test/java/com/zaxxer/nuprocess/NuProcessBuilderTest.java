package com.zaxxer.nuprocess;

import org.junit.Test;

import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.util.Collections;
import java.util.List;
import java.util.concurrent.TimeUnit;

import static org.junit.Assert.assertEquals;

public final class NuProcessBuilderTest {
    @Test
    public void wantWriteWithoutPendingStdinDoesNotInventBytes() throws Exception {
        CountingHandler handler = new CountingHandler(false);
        NuProcessBuilder builder = new NuProcessBuilder(List.of("/usr/bin/wc", "-c"), Collections.emptyMap());
        builder.setProcessListener(handler);
        NuProcess process = builder.start();

        process.wantWrite();
        process.closeStdin(false);
        int exit = process.waitFor(5, TimeUnit.SECONDS);

        assertEquals(0, exit);
        assertEquals("0", handler.stdout.toString().trim());
    }

    @Test
    public void writeStdinPreservesOnlyProvidedBytes() throws Exception {
        CountingHandler handler = new CountingHandler(false);
        NuProcessBuilder builder = new NuProcessBuilder(List.of("/usr/bin/wc", "-c"), Collections.emptyMap());
        builder.setProcessListener(handler);
        NuProcess process = builder.start();

        process.writeStdin(ByteBuffer.wrap("abc".getBytes(StandardCharsets.UTF_8)));
        process.closeStdin(false);
        int exit = process.waitFor(5, TimeUnit.SECONDS);

        assertEquals(0, exit);
        assertEquals("3", handler.stdout.toString().trim());
    }

    @Test
    public void wantWritePullsHandlerStdinOnPumpThreadWithoutDroppingBytes() throws Exception {
        CountingHandler handler = new CountingHandler(150_000);
        NuProcessBuilder builder = new NuProcessBuilder(List.of("/usr/bin/wc", "-c"), Collections.emptyMap());
        builder.setProcessListener(handler);
        NuProcess process = builder.start();

        process.wantWrite();
        process.closeStdin(false);
        int exit = process.waitFor(5, TimeUnit.SECONDS);

        assertEquals(0, exit);
        assertEquals("150000", handler.stdout.toString().trim());
    }

    @Test
    public void wantWriteCanResumeAfterLaterHttpChunksArrive() throws Exception {
        CountingHandler handler = new CountingHandler(65_536);
        NuProcessBuilder builder = new NuProcessBuilder(List.of("/usr/bin/wc", "-c"), Collections.emptyMap());
        builder.setProcessListener(handler);
        NuProcess process = builder.start();

        process.wantWrite();
        Thread.sleep(100);
        handler.addStdinBytes(65_536);
        process.wantWrite();
        Thread.sleep(100);
        handler.addStdinBytes(65_536);
        process.wantWrite();
        process.closeStdin(false);
        int exit = process.waitFor(5, TimeUnit.SECONDS);

        assertEquals(0, exit);
        assertEquals("196608", handler.stdout.toString().trim());
    }

    private static final class CountingHandler implements NuProcessHandler {
        private final boolean hasMoreStdin;
        private int stdinBytesRemaining;
        private final StringBuilder stdout = new StringBuilder();

        CountingHandler(boolean hasMoreStdin) {
            this.hasMoreStdin = hasMoreStdin;
        }

        CountingHandler(int stdinBytesRemaining) {
            this.hasMoreStdin = true;
            this.stdinBytesRemaining = stdinBytesRemaining;
        }

        synchronized void addStdinBytes(int bytes) {
            stdinBytesRemaining += bytes;
        }

        @Override
        public void onPreStart(NuProcess nuProcess) {
        }

        @Override
        public void onStart(NuProcess nuProcess) {
        }

        @Override
        public void onStdout(ByteBuffer buffer, boolean closed) {
            if (buffer == null) {
                return;
            }
            byte[] bytes = new byte[buffer.remaining()];
            buffer.get(bytes);
            stdout.append(new String(bytes, StandardCharsets.UTF_8));
        }

        @Override
        public void onStderr(ByteBuffer buffer, boolean closed) {
        }

        @Override
        public boolean onStdinReady(ByteBuffer buffer) {
            synchronized (this) {
                if (stdinBytesRemaining <= 0) {
                    buffer.flip();
                    return false;
                }
                int count = Math.min(stdinBytesRemaining, buffer.remaining());
                for (int i = 0; i < count; i++) {
                    buffer.put((byte) 'x');
                }
                stdinBytesRemaining -= count;
                buffer.flip();
                return stdinBytesRemaining > 0;
            }
        }

        @Override
        public void onExit(int statusCode) {
        }
    }
}
