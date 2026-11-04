// Runtime log: append-only file plus an in-memory ring buffer.
//
// Two consumers with different needs:
//   - the UI tails the ring buffer (fast, bounded, no disk I/O on the paint path)
//   - the file is the durable record that users open, and that AI-INSTALL tells
//     agents to grep for a "hoisted" line
//
// Writes happen on proxy threads, so the file append is queued to a background
// writer. A synchronous AppendAllText here would put an open/close on every
// single request, which is the last thing a proxy should do.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Mimas
{
    public sealed class LogEntry
    {
        public DateTime At;
        public string Text;
        public LogKind Kind;

        public string Line
        {
            get { return At.ToString("HH:mm:ss") + "  " + Text; }
        }
    }

    public sealed class LogHub : IDisposable
    {
        const int Capacity = 2000;

        readonly object _gate = new object();
        readonly List<LogEntry> _ring = new List<LogEntry>(Capacity);

        readonly Queue<string> _pending = new Queue<string>();
        readonly object _fileGate = new object();
        readonly AutoResetEvent _fileSignal = new AutoResetEvent(false);
        readonly Thread _writer;
        volatile bool _stopping;

        public event EventHandler Appended;

        public LogHub()
        {
            _writer = new Thread(WriterLoop);
            _writer.IsBackground = true;
            _writer.Name = "mimas-log";
            _writer.Start();
        }

        public void Write(string message, LogKind kind)
        {
            LogEntry e = new LogEntry();
            e.At = DateTime.Now;
            e.Text = message ?? "";
            e.Kind = kind;

            lock (_gate)
            {
                _ring.Add(e);
                if (_ring.Count > Capacity) _ring.RemoveRange(0, _ring.Count - Capacity);
            }

            // Timestamps in the file are full precision; the UI shows HH:mm:ss.
            string line = e.At.ToString("yyyy-MM-dd HH:mm:ss") + "  " + e.Text + "\r\n";
            lock (_fileGate)
            {
                // Bound the queue so a stalled disk cannot grow memory without limit.
                if (_pending.Count < 5000) _pending.Enqueue(line);
            }
            _fileSignal.Set();

            EventHandler h = Appended;
            if (h != null) { try { h(this, EventArgs.Empty); } catch { } }
        }

        void WriterLoop()
        {
            StringBuilder batch = new StringBuilder(4096);

            while (!_stopping)
            {
                _fileSignal.WaitOne(1000);

                batch.Length = 0;
                lock (_fileGate)
                {
                    while (_pending.Count > 0) batch.Append(_pending.Dequeue());
                }

                if (batch.Length == 0) continue;

                try { File.AppendAllText(AppConfig.LogPath, batch.ToString()); }
                catch { }
            }

            // Final drain on shutdown.
            batch.Length = 0;
            lock (_fileGate)
            {
                while (_pending.Count > 0) batch.Append(_pending.Dequeue());
            }
            if (batch.Length > 0)
            {
                try { File.AppendAllText(AppConfig.LogPath, batch.ToString()); }
                catch { }
            }
        }

        public void Info(string message) { Write(message, LogKind.Info); }
        public void Start(string message) { Write(message, LogKind.Start); }
        public void Rewrite(string message) { Write(message, LogKind.Rewrite); }
        public void Error(string message) { Write(message, LogKind.Error); }

        /// <summary>Newest-last snapshot of the last `max` entries.</summary>
        public List<LogEntry> Tail(int max)
        {
            lock (_gate)
            {
                int from = Math.Max(0, _ring.Count - max);
                return new List<LogEntry>(_ring.GetRange(from, _ring.Count - from));
            }
        }

        public int Count { get { lock (_gate) { return _ring.Count; } } }

        public void Clear()
        {
            lock (_gate) { _ring.Clear(); }
            lock (_fileGate) { _pending.Clear(); }

            try { File.WriteAllText(AppConfig.LogPath, ""); } catch { }

            EventHandler h = Appended;
            if (h != null) { try { h(this, EventArgs.Empty); } catch { } }
        }

        public void Dispose()
        {
            _stopping = true;
            _fileSignal.Set();
            try { _writer.Join(1500); } catch { }
        }
    }
}
