using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;

namespace Core.Bus;

// Background-thread file logger for high-volume bus traffic.
//
// Worker threads (IPC pipe, DPID scheduler, $36 multi-frame ingest, etc.)
// call Write() with pre-formatted log lines; a dedicated writer thread
// drains the queue in batches and flushes to a buffered StreamWriter. The
// UI thread is never involved - this is the path the user takes when the
// textbox-based bus log can't keep up (multi-thousand-frame $36 downloads,
// sustained $AA Fast streaming, etc.).
//
// Path is set at Start() time. Stop() flushes pending writes, writes a
// trailer line with the line count, and closes the file. Dispose calls Stop.
public sealed class FileLogSink : IDisposable
{
    private readonly ConcurrentQueue<string> queue = new();
    private readonly AutoResetEvent dataAvailable = new(false);
    private Thread? writerThread;
    private volatile bool running;
    private StreamWriter? stream;
    private long bytesWritten;
    private long linesWritten;

    /// <summary>Path of the currently-open log file, or null when stopped.</summary>
    public string? CurrentPath { get; private set; }

    /// <summary>True while a writer thread is active.</summary>
    public bool IsRunning => running;

    /// <summary>Cumulative bytes written to the current file since Start().</summary>
    public long BytesWritten => Interlocked.Read(ref bytesWritten);

    /// <summary>Cumulative lines written to the current file since Start().</summary>
    public long LinesWritten => Interlocked.Read(ref linesWritten);

    /// <summary>Default directory: %LOCALAPPDATA%\GmEcuSimulator\logs.</summary>
    public static string DefaultDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "GmEcuSimulator", "logs");
    }

    /// <summary>
    /// Generates a fresh default filename: bus_yyyyMMdd_HHmmss.csv under
    /// DefaultDirectory(). Each Start() call produces a new file. The .csv
    /// extension matches the actual content (frame rows are comma-separated);
    /// the header banner lines start with '#' so spreadsheet importers either
    /// land them in column A as plain text or skip them as comments.
    /// </summary>
    public static string DefaultPath()
    {
        var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        return Path.Combine(DefaultDirectory(), $"bus_{ts}.csv");
    }

    /// <summary>
    /// Opens the given path for writing and starts the background writer.
    /// Replaces any in-progress session (calls Stop() first).
    /// </summary>
    public void Start(string path)
    {
        Stop();

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 64 * 1024);
        var sw = new StreamWriter(fs) { AutoFlush = false };
        sw.WriteLine($"# GmEcuSimulator bus log");
        sw.WriteLine($"# Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss} (local)");
        sw.WriteLine($"# Path:    {path}");
        sw.WriteLine();

        stream = sw;
        CurrentPath = path;
        bytesWritten = 0;
        linesWritten = 0;
        running = true;

        writerThread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "FileLogSink-Writer",
        };
        writerThread.Start();
    }

    /// <summary>
    /// Signals the writer thread to drain remaining queued lines, writes a
    /// trailer, and closes the file. Safe to call when not running (no-op).
    /// </summary>
    public void Stop()
    {
        if (!running) return;
        running = false;
        dataAvailable.Set();
        writerThread?.Join(2000);
        writerThread = null;

        try
        {
            if (stream is not null)
            {
                stream.WriteLine();
                stream.WriteLine($"# Stopped: {DateTime.Now:yyyy-MM-dd HH:mm:ss} (local)");
                stream.WriteLine($"# Lines:   {LinesWritten:N0}");
                stream.WriteLine($"# Bytes:   {BytesWritten:N0}");
                stream.Flush();
                stream.Dispose();
            }
        }
        catch { /* file already gone / disk full - no recourse */ }

        stream = null;
        CurrentPath = null;
    }

    /// <summary>
    /// Enqueue a log line for the writer thread to flush. Thread-safe and
    /// non-blocking - returns immediately. No-op when not running.
    /// </summary>
    public void Write(string line)
    {
        if (!running) return;
        queue.Enqueue(line);
        dataAvailable.Set();
    }

    private void WriterLoop()
    {
        var sw = stream;
        if (sw is null) return;
        var batchBuf = new StringBuilder(16 * 1024);

        while (running)
        {
            // 100ms poll interval keeps Stop() latency bounded even if the
            // queue is empty between bursts; the AutoResetEvent wakes the
            // thread immediately when there's work to do.
            dataAvailable.WaitOne(100);
            DrainOnce(sw, batchBuf);
        }
        // Final drain after running=false to catch anything queued between
        // the last tick and Stop().
        DrainOnce(sw, batchBuf);
    }

    private void DrainOnce(StreamWriter sw, StringBuilder batchBuf)
    {
        batchBuf.Clear();
        int count = 0;
        while (queue.TryDequeue(out var line))
        {
            batchBuf.Append(line).Append('\n');
            count++;
            // Soft upper bound on per-tick batch size to keep the writer
            // responsive to Stop() and to bound StringBuilder memory.
            if (count >= 5000) break;
        }
        if (count == 0) return;
        try
        {
            sw.Write(batchBuf);
            sw.Flush();
            Interlocked.Add(ref bytesWritten, batchBuf.Length);
            Interlocked.Add(ref linesWritten, count);
        }
        catch { /* disk full / file gone - drop and continue, don't crash the sim */ }
    }

    public void Dispose()
    {
        Stop();
        dataAvailable.Dispose();
    }
}
