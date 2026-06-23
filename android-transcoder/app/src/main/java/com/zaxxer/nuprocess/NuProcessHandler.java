package com.zaxxer.nuprocess;

import java.nio.ByteBuffer;

public interface NuProcessHandler {
    void onPreStart(NuProcess nuProcess);
    void onStart(NuProcess nuProcess);
    void onStdout(ByteBuffer buffer, boolean closed);
    void onStderr(ByteBuffer buffer, boolean closed);
    boolean onStdinReady(ByteBuffer buffer);
    void onExit(int statusCode);
}
