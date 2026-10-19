using System.Diagnostics;

namespace GitTfs.Util
{
    /// <summary>
    /// Full-screen interactive console UI: a scrolling log pane on top and a live diagnostics pane at the bottom
    /// showing the tree of operations currently running (with elapsed time), recently completed steps with
    /// their duration, retries/errors and process counters. Used to see in real time what git-tfs is doing.
    /// Only active when the console is interactive (no redirection) and GIT_TFS_NO_TUI is not set.
    /// </summary>
    public static class ConsoleUi
    {
        private const int MaxLogLines = 2000;
        private const int MaxRecent = 6;
        private const int StatusHeight = 20;
        private static readonly TimeSpan SlowStep = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan VerySlowStep = TimeSpan.FromSeconds(30);

        private static readonly object _lock = new object();
        private static readonly LinkedList<string> _logLines = new LinkedList<string>();
        private static readonly List<StepScope> _active = new List<StepScope>();
        private static readonly LinkedList<string> _recent = new LinkedList<string>();
        private static readonly Dictionary<string, StepScope> _changesetSteps = new Dictionary<string, StepScope>();
        private static readonly Stopwatch _elapsed = new Stopwatch();
        private static Timer _timer;
        private static bool _logDirty;
        private static int _lastWidth, _lastHeight;
        private static long _nextStepId;
        private static int _scrollOffset; // lines scrolled up from the newest log line
        private static int _logHeight = 1;

        private static int _changesetsFetched;
        private static long _stepsCompleted;
        private static long _retries;
        private static string _lastProblem;
        private static DateTime _lastProblemTime;

        [ThreadStatic]
        private static StepScope _currentOnThread;

        public static bool IsActive { get; private set; }

        public static bool ShouldEnable()
        {
            try
            {
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GIT_TFS_NO_TUI")))
                    return false;
                if (Console.IsOutputRedirected || Console.IsErrorRedirected || Console.IsInputRedirected)
                    return false;
                return Console.WindowHeight >= StatusHeight + 5 && Console.WindowWidth >= 60;
            }
            catch
            {
                return false;
            }
        }

        public static void Start()
        {
            lock (_lock)
            {
                if (IsActive)
                    return;
                IsActive = true;
                _elapsed.Start();
                try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
                try { Console.CursorVisible = false; } catch { }
                Console.Clear();
                _logDirty = true;
                _timer = new Timer(_ => Render(), null, 0, 250);
                new Thread(ReadKeys) { IsBackground = true, Name = "git-tfs console keys" }.Start();
            }
        }

        public static void Stop()
        {
            lock (_lock)
            {
                if (!IsActive)
                    return;
                _timer?.Dispose();
                _timer = null;
                IsActive = false;
                _elapsed.Stop();
                try
                {
                    Console.ResetColor();
                    Console.Clear();
                    var height = Math.Max(1, Console.WindowHeight - 3);
                    foreach (var line in _logLines.Skip(Math.Max(0, _logLines.Count - height)))
                        Console.WriteLine(line);
                    if (_stepsCompleted > 0)
                        Console.WriteLine(BuildSummary());
                    Console.CursorVisible = true;
                }
                catch { }
            }
        }

        public static void Log(string message)
        {
            if (message == null)
                return;
            lock (_lock)
            {
                foreach (var line in message.Replace("\r", string.Empty).Split('\n'))
                {
                    _logLines.AddLast(line);
                    if (_logLines.Count > MaxLogLines)
                        _logLines.RemoveFirst();
                    else if (_scrollOffset > 0)
                        _scrollOffset++;
                }
                _logDirty = true;
            }
        }

        /// <summary>
        /// Starts a named background operation; dispose the result to mark it as completed.
        /// Steps started on the same thread while another is running are shown nested under it.
        /// </summary>
        public static IDisposable Step(string description) => Step(description, _currentOnThread);

        /// <summary>The innermost step on the current thread; pass it to Step(description, parent) from worker threads.</summary>
        public static object CurrentStep => _currentOnThread;

        public static IDisposable Step(string description, object parent)
        {
            if (!IsActive)
                return NullScope.Instance;
            var scope = new StepScope(description, parent as StepScope);
            lock (_lock)
                _active.Add(scope);
            scope.PreviousOnThread = _currentOnThread;
            _currentOnThread = scope;
            return scope;
        }

        /// <summary>Updates the detail text of a specific step (thread-safe).</summary>
        public static void Detail(IDisposable step, string detail)
        {
            if (step is StepScope scope)
                scope.Detail = detail;
        }

        /// <summary>Updates the detail text of the innermost step running on the current thread.</summary>
        public static void Detail(string detail)
        {
            var current = _currentOnThread;
            if (current != null)
                current.Detail = detail;
        }

        public static void ChangesetStarted(int changesetId, string tfsPath)
        {
            if (!IsActive)
                return;
            ChangesetEnded(tfsPath, false);
            var step = (StepScope)Step(string.Format("C{0} on {1}", changesetId, tfsPath));
            lock (_lock)
                _changesetSteps[tfsPath] = step;
        }

        public static void ChangesetCompleted(int changesetId, string tfsPath)
        {
            if (!IsActive)
                return;
            ChangesetEnded(tfsPath, true);
        }

        /// <summary>Closes the changeset step of a branch (e.g. when a fetch returns early).</summary>
        public static void ChangesetEnded(string tfsPath, bool succeeded)
        {
            if (!IsActive || tfsPath == null)
                return;
            StepScope step;
            lock (_lock)
            {
                if (!_changesetSteps.TryGetValue(tfsPath, out step))
                    return;
                _changesetSteps.Remove(tfsPath);
                if (succeeded)
                    _changesetsFetched++;
            }
            if (!succeeded)
                step.Detail = "aborted";
            step.Dispose();
        }

        public static void Retry(string operation, Exception exception)
        {
            if (!IsActive)
                return;
            lock (_lock)
            {
                _retries++;
                _lastProblem = string.Format("retry {0}: {1}: {2}", operation, exception.GetType().Name, exception.Message);
                _lastProblemTime = DateTime.Now;
            }
        }

        public static void Problem(string message)
        {
            if (!IsActive)
                return;
            lock (_lock)
            {
                _lastProblem = message;
                _lastProblemTime = DateTime.Now;
            }
        }

        private static void Complete(StepScope scope)
        {
            lock (_lock)
            {
                if (!_active.Remove(scope))
                    return;
                _stepsCompleted++;
                var duration = scope.Elapsed;
                _recent.AddFirst(string.Format("{0,8} {1}{2}", FormatDuration(duration), scope.Description,
                    string.IsNullOrEmpty(scope.Detail) ? string.Empty : " (" + scope.Detail + ")"));
                while (_recent.Count > MaxRecent)
                    _recent.RemoveLast();
            }
            if (_currentOnThread == scope)
                _currentOnThread = scope.PreviousOnThread != null && !scope.PreviousOnThread.IsDisposed ? scope.PreviousOnThread : null;
        }

        private static string BuildSummary()
        {
            var minutes = Math.Max(1.0 / 60, _elapsed.Elapsed.TotalMinutes);
            return string.Format("elapsed {0:hh\\:mm\\:ss} | changesets {1} ({2:0.0}/min) | steps {3} | retries {4}",
                _elapsed.Elapsed, _changesetsFetched, _changesetsFetched / minutes, _stepsCompleted, _retries);
        }

        private static void Render()
        {
            lock (_lock)
            {
                if (!IsActive)
                    return;
                try
                {
                    int width = Console.WindowWidth;
                    int height = Console.WindowHeight;
                    if (width != _lastWidth || height != _lastHeight)
                    {
                        Console.Clear();
                        _lastWidth = width;
                        _lastHeight = height;
                        _logDirty = true;
                    }
                    var lineWidth = width - 1;
                    var logHeight = Math.Max(1, height - StatusHeight);
                    var top = Console.WindowTop;

                    if (_logDirty)
                    {
                        _logHeight = logHeight;
                        _scrollOffset = Math.Min(_scrollOffset, MaxScroll());
                        var visible = _scrollOffset > 0 ? logHeight - 1 : logHeight;
                        var first = Math.Max(0, _logLines.Count - visible - _scrollOffset);
                        var logs = _logLines.Skip(first).Take(visible).ToList();
                        for (int i = 0; i < visible; i++)
                            WriteAt(top + i, i < logs.Count ? logs[i] : string.Empty, lineWidth, ConsoleColor.Gray);
                        if (_scrollOffset > 0)
                            WriteAt(top + visible, string.Format(" ↑ scrolled {0} lines up — PgUp/PgDn/↑/↓/Home to scroll, End to follow", _scrollOffset),
                                lineWidth, ConsoleColor.Black, ConsoleColor.DarkYellow);
                        _logDirty = false;
                    }

                    var row = top + logHeight;
                    var end = top + height - 1;
                    var process = Process.GetCurrentProcess();
                    WriteAt(row++, new string('─', lineWidth), lineWidth, ConsoleColor.DarkCyan);
                    WriteAt(row++, string.Format(" ● {0} | mem {1} MB | threads {2}", BuildSummary(),
                        GC.GetTotalMemory(false) / (1024 * 1024), process.Threads.Count), lineWidth, ConsoleColor.Cyan);

                    var activeRows = end - row - MaxRecent - 3;
                    WriteAt(row++, string.Format(" Running ({0}):", _active.Count), lineWidth, ConsoleColor.White);
                    var ordered = OrderAsTree();
                    for (int i = 0; i < activeRows; i++)
                    {
                        if (i == activeRows - 1 && ordered.Count > activeRows)
                        {
                            WriteAt(row++, string.Format("   … and {0} more", ordered.Count - i), lineWidth, ConsoleColor.DarkGray);
                            continue;
                        }
                        if (i >= ordered.Count)
                        {
                            WriteAt(row++, string.Empty, lineWidth, ConsoleColor.Gray);
                            continue;
                        }
                        var step = ordered[i];
                        var text = string.Format("   {0}{1} {2,8}  {3}{4}{5}",
                            new string(' ', step.Depth * 2), step.Depth == 0 ? "▶" : "└",
                            FormatDuration(step.Elapsed), step.Description,
                            string.IsNullOrEmpty(step.Detail) ? string.Empty : " — " + step.Detail,
                            step.ThreadId != ordered[0].ThreadId ? " [T" + step.ThreadId + "]" : string.Empty);
                        WriteAt(row++, text, lineWidth, ColorFor(step.Elapsed, ConsoleColor.Green));
                    }

                    WriteAt(row++, " Recently completed:", lineWidth, ConsoleColor.White);
                    var recent = _recent.ToList();
                    for (int i = 0; i <= MaxRecent; i++)
                        WriteAt(row++, i < recent.Count ? "   ✓ " + recent[i] : string.Empty, lineWidth, ConsoleColor.DarkGray);
                    Console.ResetColor();
                }
                catch
                {
                    // never let rendering issues break the fetch
                }
            }
        }

        private static List<StepScope> OrderAsTree()
        {
            var result = new List<StepScope>();
            var activeSet = new HashSet<StepScope>(_active);
            void Add(StepScope parent, int depth)
            {
                foreach (var step in _active.Where(s => (s.Parent != null && activeSet.Contains(s.Parent) ? s.Parent : null) == parent))
                {
                    step.Depth = depth;
                    result.Add(step);
                    Add(step, depth + 1);
                }
            }
            Add(null, 0);
            return result;
        }

        private static ConsoleColor ColorFor(TimeSpan elapsed, ConsoleColor normal)
            => elapsed >= VerySlowStep ? ConsoleColor.Red : elapsed >= SlowStep ? ConsoleColor.Yellow : normal;

        private static int MaxScroll() => Math.Max(0, _logLines.Count - (_logHeight - 1));

        private static void ReadKeys()
        {
            while (IsActive)
            {
                try
                {
                    if (!Console.KeyAvailable)
                    {
                        Thread.Sleep(50);
                        continue;
                    }
                    var key = Console.ReadKey(true).Key;
                    lock (_lock)
                    {
                        var page = Math.Max(1, _logHeight - 2);
                        switch (key)
                        {
                            case ConsoleKey.PageUp: _scrollOffset += page; break;
                            case ConsoleKey.PageDown: _scrollOffset -= page; break;
                            case ConsoleKey.UpArrow: _scrollOffset++; break;
                            case ConsoleKey.DownArrow: _scrollOffset--; break;
                            case ConsoleKey.Home: _scrollOffset = MaxScroll(); break;
                            case ConsoleKey.End:
                            case ConsoleKey.Escape: _scrollOffset = 0; break;
                            default: continue;
                        }
                        _scrollOffset = Math.Max(0, Math.Min(_scrollOffset, MaxScroll()));
                        _logDirty = true;
                    }
                    Render();
                }
                catch
                {
                    return; // no usable keyboard: scrolling disabled, fetch unaffected
                }
            }
        }

        private static void WriteAt(int row, string text, int width, ConsoleColor color, ConsoleColor background = ConsoleColor.Black)
        {
            if (row >= Console.BufferHeight)
                return;
            Console.SetCursorPosition(0, row);
            Console.ForegroundColor = color;
            Console.BackgroundColor = background;
            text = text ?? string.Empty;
            Console.Write(text.Length > width ? text.Substring(0, width) : text.PadRight(width));
        }

        private static string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalSeconds < 1)
                return string.Format("{0}ms", (int)duration.TotalMilliseconds);
            if (duration.TotalMinutes < 1)
                return string.Format("{0:0.0}s", duration.TotalSeconds);
            return string.Format("{0}m{1:00}s", (int)duration.TotalMinutes, duration.Seconds);
        }

        private sealed class StepScope : IDisposable
        {
            private readonly Stopwatch _watch = Stopwatch.StartNew();

            public StepScope(string description, StepScope parent)
            {
                Id = Interlocked.Increment(ref _nextStepId);
                Description = description;
                Parent = parent;
                ThreadId = Thread.CurrentThread.ManagedThreadId;
            }

            public long Id { get; }
            public string Description { get; }
            public StepScope Parent { get; }
            public StepScope PreviousOnThread { get; set; }
            public int ThreadId { get; }
            public int Depth { get; set; }
            public volatile string Detail;
            public bool IsDisposed { get; private set; }
            public TimeSpan Elapsed => _watch.Elapsed;

            public void Dispose()
            {
                if (IsDisposed)
                    return;
                IsDisposed = true;
                _watch.Stop();
                Complete(this);
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new NullScope();
            public void Dispose() { }
        }
    }
}
