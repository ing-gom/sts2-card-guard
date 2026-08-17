using System;
using System.Collections.Generic;
using System.Linq;

namespace Sts2CardGuard;

/// <summary>
/// One BLOCKED set keyed by scope — a card-pool title, or <see cref="CardGuardService.AllScope"/> for
/// the "every character" scope — plus the co-op host override that
/// <see cref="Multiplayer.MultiplayerSync"/> installs for a networked run.
///
/// <see cref="CardGuardService"/> and <see cref="RelicGuardService"/> spell this machinery out inline
/// (they predate it). The potion and event guards share it instead: each needs two of these sets
/// (whole mod packs, and individual items) and hand-copying the lock + override + snapshot plumbing
/// four more times is exactly where a "writes went to the override" bug would hide.
///
/// Default is PERMISSIVE — an absent entry means allowed, so a fresh install blocks nothing.
/// </summary>
internal sealed class ScopedBlockSet
{
    private static readonly StringComparer OIC = StringComparer.OrdinalIgnoreCase;

    private readonly object _lock = new();
    private readonly Dictionary<string, HashSet<string>> _local = new(OIC);
    private readonly Dictionary<string, HashSet<string>> _override = new(OIC);

    /// <summary>Read the host's set instead of ours. Set once per run at lock-in, never mid-run.</summary>
    private volatile bool _useOverride;

    /// <summary>Exact-scope lookup — what the settings panel needs, since it shows and edits ONE
    /// scope at a time and must not report the all-characters scope's entries as that scope's own.</summary>
    public bool GetAllowed(string scope, string key)
    {
        if (string.IsNullOrEmpty(scope) || string.IsNullOrEmpty(key)) return true;
        lock (_lock)
        {
            var d = _useOverride ? _override : _local;
            return !(d.TryGetValue(scope, out var s) && s.Contains(key));
        }
    }

    /// <summary>Scope + all-characters lookup — what every filter asks. A block set for either holds.</summary>
    public bool GetAllowedEffective(string scope, string key) =>
        GetAllowed(scope, key) && GetAllowed(CardGuardService.AllScope, key);

    /// <summary>Writes always land on the LOCAL set: the override is one run's borrowed config and is
    /// discarded at the end of it, so editing it would silently lose the user's own settings.</summary>
    public void SetAllowed(string scope, string key, bool allowed)
    {
        if (string.IsNullOrEmpty(scope) || string.IsNullOrEmpty(key)) return;
        lock (_lock)
        {
            if (!_local.TryGetValue(scope, out var s)) { s = new HashSet<string>(OIC); _local[scope] = s; }
            if (allowed) s.Remove(key); else s.Add(key);
        }
    }

    /// <summary>The blocked keys of one scope, from the LOCAL set (for a lossless save).</summary>
    public List<string> Blocked(string scope)
    {
        lock (_lock) { return _local.TryGetValue(scope, out var s) ? s.ToList() : new List<string>(); }
    }

    /// <summary>Every scope that currently has an entry (for a lossless save).</summary>
    public List<string> Scopes()
    {
        lock (_lock) { return _local.Keys.ToList(); }
    }

    /// <summary>A copy of the LOCAL config (never the override) — what the host sends to peers.</summary>
    public Dictionary<string, List<string>> SnapshotLocal()
    {
        var map = new Dictionary<string, List<string>>(OIC);
        lock (_lock)
            foreach (var (k, s) in _local)
                if (s.Count > 0) map[k] = s.ToList();
        return map;
    }

    /// <summary>Client of a networked run: filter with the host's set, ignoring ours.</summary>
    public void ApplyOverride(Dictionary<string, List<string>>? src)
    {
        lock (_lock)
        {
            _override.Clear();
            if (src != null)
                foreach (var (k, v) in src)
                    _override[k] = new HashSet<string>(v ?? new List<string>(), OIC);
            _useOverride = true;
        }
    }

    /// <summary>Filter with our own set (host of a networked run, or singleplayer).</summary>
    public void UseLocal()
    {
        lock (_lock) { _useOverride = false; }
    }

    /// <summary>Back to singleplayer: local set, override dropped.</summary>
    public void ClearOverride()
    {
        lock (_lock) { _useOverride = false; _override.Clear(); }
    }
}
