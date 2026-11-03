using ClassicUO.Utility.Logging;

namespace ModernUO.Studio
{
    internal enum StudioLogLevel
    {
        Info,
        Warning,
        Error
    }

    /// <summary>The messages the Log panel shows; each one also goes to the client's console logger.</summary>
    internal static class StudioLog
    {
        public readonly record struct Entry(DateTime Time, StudioLogLevel Level, string Text);

        private const int Capacity = 2000;
        private static readonly Queue<Entry> _entries = new();
        private static readonly object _lock = new();

        public static int Version { get; private set; }

        public static void Info(string text) => Add(StudioLogLevel.Info, text);

        public static void Warn(string text) => Add(StudioLogLevel.Warning, text);

        public static void Error(string text) => Add(StudioLogLevel.Error, text);

        private static void Add(StudioLogLevel level, string text)
        {
            switch (level)
            {
                case StudioLogLevel.Error: Log.Error(text); break;
                case StudioLogLevel.Warning: Log.Warn(text); break;
                default: Log.Info(text); break;
            }

            lock (_lock)
            {
                _entries.Enqueue(new Entry(DateTime.Now, level, text));

                while (_entries.Count > Capacity)
                {
                    _entries.Dequeue();
                }

                Version++;
            }
        }

        public static void CopyTo(List<Entry> list)
        {
            lock (_lock)
            {
                list.Clear();
                list.AddRange(_entries);
            }
        }

        public static void Clear()
        {
            lock (_lock)
            {
                _entries.Clear();
                Version++;
            }
        }
    }
}
