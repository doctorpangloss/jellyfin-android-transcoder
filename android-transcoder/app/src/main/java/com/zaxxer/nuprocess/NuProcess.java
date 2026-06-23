package com.zaxxer.nuprocess;

import java.nio.ByteBuffer;
import java.util.concurrent.TimeUnit;

public interface NuProcess {
    int getPID();
    int getPid();
    boolean isRunning();
    int waitFor(long timeout, TimeUnit unit) throws InterruptedException;
    void destroy(boolean force);
    void wantWrite();
    void closeStdin(boolean force);
    void writeStdin(ByteBuffer buffer);
    boolean hasPendingWrites();
    void setProcessHandler(NuProcessHandler processHandler);
}
