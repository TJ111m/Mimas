// Watches a set of process names and reports whether any of them is alive.
//
// Used by "watch" mode: the proxy only listens while a host app (ZCode,
// Claude Code, ...) is running, so it costs nothing when the agent is idle.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Mimas
{
    public sealed class ProcessWatcher : IDisposable
    {
        readonly List<string> _names;
        readonly int _pollMs;
        readonly Action<bool> _onChange;
        Timer _timer;
        volatile bool _lastAlive;

        public ProcessWatcher(List<string> names, int pollMs, Action<bool> onChange)
        {
            _names = names ?? new List<string>();
            for (int i = 0; i < _names.Count; i++)
            {
                string n = _names[i];
                if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    _names[i] = n.Substring(0, n.Length - 4);
            }
            _pollMs = pollMs <= 0 ? 3000 : pollMs;
            _onChange = onChange ?? delegate { };
        }

        public void Start()
        {
            _lastAlive = IsAnyAlive();
            _onChange(_lastAlive);
            _timer = new Timer(Tick, null, _pollMs, _pollMs);
        }

        void Tick(object state)
        {
            bool alive;
            try { alive = IsAnyAlive(); }
            catch { return; }

            if (alive == _lastAlive) return;
            _lastAlive = alive;
            try { _onChange(alive); }
            catch { }
        }

        public bool IsAnyAlive()
        {
            for (int i = 0; i < _names.Count; i++)
            {
                string name = _names[i];

                Process[] procs;
                try { procs = Process.GetProcessesByName(name); }
                catch { continue; }

                if (procs.Length > 0)
                {
                    for (int p = 0; p < procs.Length; p++)
                        try { procs[p].Dispose(); } catch { }
                    return true;
                }
            }
            return false;
        }

        // Informational: which matched process names are currently running.
        public List<string> AliveNames()
        {
            List<string> alive = new List<string>();
            for (int i = 0; i < _names.Count; i++)
            {
                Process[] procs;
                try { procs = Process.GetProcessesByName(_names[i]); }
                catch { continue; }
                if (procs.Length > 0) alive.Add(_names[i] + " x" + procs.Length);
                for (int p = 0; p < procs.Length; p++)
                    try { procs[p].Dispose(); } catch { }
            }
            return alive;
        }

        public void Dispose()
        {
            if (_timer != null) { _timer.Dispose(); _timer = null; }
        }
    }
}
