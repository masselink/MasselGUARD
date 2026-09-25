using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MasselGUARD.Models;

namespace MasselGUARD.Services
{
    /// <summary>
    /// Manages the in-app activity log.
    /// ViewModels subscribe to EntryAdded to render entries.
    /// No UI references - produces pure LogEntry objects.
    /// </summary>
    public class LogService
    {
        private readonly List<LogEntry> _entries = new();
        private readonly object _lock = new();
        private LogLevel _minLevel = LogLevel.Ok;

        // ── Persistence (optional) ──────────────────────────────────────────────
        private readonly object _fileLock = new();
        private string? _filePath;          // null → in-memory only (no persistence)
        private long    _maxBytes;          // 0 → unlimited
        private long    _fileBytes;         // running size of the on-disk log

        /// <summary>
        /// Raised after each new entry is added.
        /// May be invoked on a background thread - subscribers must marshal to the UI
        /// thread themselves (e.g. via Dispatcher.BeginInvoke).
        /// </summary>
        public event Action<LogEntry>? EntryAdded;

        /// <summary>
        /// Returns a point-in-time snapshot of all entries.
        /// Safe to call from any thread.
        /// </summary>
        public IReadOnlyList<LogEntry> Entries
        {
            get { lock (_lock) { return _entries.ToList(); } }
        }

        /// <summary>Thread-safe entry count (no snapshot allocation).</summary>
        public int Count { get { lock (_lock) { return _entries.Count; } } }

        public LogLevel MinLevel
        {
            get => _minLevel;
            set => _minLevel = value;
        }

        public bool IsExtended
        {
            get => _minLevel <= LogLevel.Info;
            set => _minLevel = value ? LogLevel.Debug : LogLevel.Ok;
        }

        /// <summary>Master switch (mirrors <c>AppConfig.ActivityLogEnabled</c>). When false the log
        /// records nothing new - used by the "Activity log" feature's disable behaviour so a disabled
        /// log area stops writing entirely (in-memory and file). Existing entries/reads are unaffected.</summary>
        public bool Enabled { get; set; } = true;

        // ── Persistence setup ───────────────────────────────────────────────────
        /// <summary>
        /// Enable writing the log to <paramref name="filePath"/> with a rolling size cap
        /// (<paramref name="maxSizeKB"/>, 0 = unlimited; oldest lines dropped past it). When
        /// <paramref name="clearOnStart"/> is true the file is truncated now; otherwise the existing
        /// lines are loaded into the in-memory log so they render at startup. Best-effort - a failure
        /// never stops logging.
        /// </summary>
        public void InitPersistence(string filePath, int maxSizeKB, bool clearOnStart)
        {
            _filePath = filePath;
            _maxBytes = maxSizeKB > 0 ? (long)maxSizeKB * 1024L : 0;
            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                if (clearOnStart)
                {
                    lock (_fileLock) { File.WriteAllText(filePath, "", System.Text.Encoding.UTF8); _fileBytes = 0; }
                }
                else if (File.Exists(filePath))
                {
                    TrimFile();   // enforce the cap on whatever was left from last run
                    LoadFromFile();
                    lock (_fileLock) { _fileBytes = new FileInfo(filePath).Length; }
                }
            }
            catch { /* persistence is best-effort */ }
        }

        private void LoadFromFile()
        {
            try
            {
                var loaded = new List<LogEntry>();
                foreach (var line in File.ReadLines(_filePath!, System.Text.Encoding.UTF8))
                {
                    if (line.Length == 0) continue;
                    // Format: yyyy-MM-dd HH:mm:ss \t Level \t cont(0/1) \t message
                    var p = line.Split('\t', 4);
                    if (p.Length == 4
                        && DateTime.TryParse(p[0], out var ts)
                        && Enum.TryParse<LogLevel>(p[1], out var lvl))
                        loaded.Add(new LogEntry(ts, lvl, p[3], p[2] == "1"));
                    else
                        loaded.Add(new LogEntry(DateTime.Now, LogLevel.Info, line));
                }
                lock (_lock) { _entries.InsertRange(0, loaded); }
            }
            catch { }
        }

        // ── Write ─────────────────────────────────────────────────────────────
        public void Write(LogLevel level, string message, bool isContinuation = false)
        {
            if (!Enabled) return;
            if (level < _minLevel) return;
            var entry = new LogEntry(DateTime.Now, level, message, isContinuation);
            lock (_lock) { _entries.Add(entry); }
            AppendToFile(entry);
            // Fire outside the lock - handlers may call back into LogService.
            EntryAdded?.Invoke(entry);
        }

        private void AppendToFile(LogEntry e)
        {
            if (_filePath == null) return;
            string line = $"{e.Timestamp:yyyy-MM-dd HH:mm:ss}\t{e.Level}\t{(e.IsContinuation ? "1" : "0")}\t{e.Message}\n";
            try
            {
                lock (_fileLock)
                {
                    File.AppendAllText(_filePath, line, System.Text.Encoding.UTF8);
                    _fileBytes += System.Text.Encoding.UTF8.GetByteCount(line);
                    if (_maxBytes > 0 && _fileBytes > _maxBytes) TrimFile();
                }
            }
            catch { }
        }

        /// <summary>Drop the oldest lines until the file is within the cap (trims to ~80% so it isn't
        /// re-trimmed on every subsequent line). Caller holds <see cref="_fileLock"/> - except the
        /// startup call, which runs before any concurrent writers.</summary>
        private void TrimFile()
        {
            if (_filePath == null || _maxBytes <= 0) return;
            try
            {
                var fi = new FileInfo(_filePath);
                if (!fi.Exists || fi.Length <= _maxBytes) { _fileBytes = fi.Exists ? fi.Length : 0; return; }

                long target = (long)(_maxBytes * 0.8);
                var lines = File.ReadAllLines(_filePath, System.Text.Encoding.UTF8);
                // Keep the newest lines that fit within the target, from the end backwards.
                int start = lines.Length;
                long running = 0;
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    running += System.Text.Encoding.UTF8.GetByteCount(lines[i]) + 1;
                    if (running > target) { start = i + 1; break; }
                    start = i;
                }
                var kept = lines.Skip(start);
                File.WriteAllText(_filePath, string.Join('\n', kept) + (start < lines.Length ? "\n" : ""),
                    System.Text.Encoding.UTF8);
                _fileBytes = new FileInfo(_filePath).Length;
            }
            catch { }
        }

        public void Ok   (string msg) => Write(LogLevel.Ok,   msg);
        public void Warn  (string msg) => Write(LogLevel.Warn,  msg);
        public void Info  (string msg) => Write(LogLevel.Info,  msg);
        public void Debug (string msg) => Write(LogLevel.Debug, msg);

        // ── Export ────────────────────────────────────────────────────────────
        public void ExportToFile(string path)
        {
            IReadOnlyList<LogEntry> snapshot;
            lock (_lock) { snapshot = _entries.ToList(); }
            using var sw = new StreamWriter(path, append: false,
                encoding: System.Text.Encoding.UTF8);
            foreach (var e in snapshot)
                sw.WriteLine($"{e.Timestamp:HH:mm:ss}  [{e.Level,-5}]  {e.Message}");
        }

        /// <summary>Clear the whole log - the in-memory entries AND the persisted file.</summary>
        public void Clear()
        {
            lock (_lock) { _entries.Clear(); }
            if (_filePath != null)
            {
                try { lock (_fileLock) { File.WriteAllText(_filePath, "", System.Text.Encoding.UTF8); _fileBytes = 0; } }
                catch { }
            }
        }
    }
}
