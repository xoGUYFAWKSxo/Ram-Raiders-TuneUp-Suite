using System;
using System.Collections.Generic;
using System.Linq;

namespace TuneUpEngine
{
    // Holds the results of the engine's own scans so destructive commands can act on them by ID.
    //
    // The engine is elevated. If it accepted file paths or registry keys straight from the client
    // (as CleanJunk and RepairRegistry used to), any process able to reach the pipe could have it
    // delete arbitrary files or registry trees with administrator rights. Instead the client only
    // ever sends back IDs, and an ID can only refer to something one of the engine's scans found.
    //
    // Results are grouped by scan kind (e.g. "junk", "duplicates") and by scope — the user who ran
    // the scan — so one signed-in user can never act on another user's results. Re-running a scan
    // replaces that user's previous results of that kind, and acting on an item consumes it so it
    // can't be replayed.
    public class ResultStore<T>
    {
        private readonly Func<T, string> _idOf;
        private readonly Dictionary<string, (string Kind, string Scope, T Item)> _items = new(StringComparer.Ordinal);
        private readonly object _lock = new();

        public ResultStore(Func<T, string> idOf)
        {
            _idOf = idOf;
        }

        public void Replace(string kind, IEnumerable<T> items, string scope = "")
        {
            lock (_lock)
            {
                foreach (var id in _items.Where(kv => kv.Value.Kind == kind && kv.Value.Scope == scope).Select(kv => kv.Key).ToList())
                    _items.Remove(id);

                foreach (var item in items)
                {
                    string id = _idOf(item);
                    if (!string.IsNullOrEmpty(id))
                        _items[id] = (kind, scope, item);
                }
            }
        }

        // Removes and returns the stored items for the given IDs. Unknown IDs are ignored, so a
        // forged or stale ID simply matches nothing.
        public List<T> Take(IEnumerable<string>? ids, string scope = "")
        {
            var taken = new List<T>();
            if (ids == null) return taken;

            lock (_lock)
            {
                foreach (var id in ids.Where(i => !string.IsNullOrEmpty(i)).Distinct(StringComparer.Ordinal))
                {
                    // Another user's result is treated exactly like an unknown ID.
                    if (_items.TryGetValue(id, out var entry) && entry.Scope == scope)
                    {
                        _items.Remove(id);
                        taken.Add(entry.Item);
                    }
                }
            }
            return taken;
        }

        public int Count
        {
            get { lock (_lock) { return _items.Count; } }
        }
    }
}
