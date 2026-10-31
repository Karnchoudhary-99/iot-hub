namespace IotHub.Devices;

/// <summary>
/// In-memory registry of every device the hub knows about, plus its state-change
/// history. All mutations go through here so validation, duplicate detection and
/// history recording cannot be bypassed by a caller writing to a list directly.
/// Thread-safe for the hub server's concurrent readers/writers.
/// </summary>
public sealed class DeviceRegistry
{
    private readonly Dictionary<string, Device> _devices = new(StringComparer.Ordinal);
    private readonly List<StateChange> _history = new();
    private readonly object _gate = new();

    public int Count
    {
        get { lock (_gate) return _devices.Count; }
    }

    /// <summary>Registers a device. Throws <see cref="InvalidOperationException"/> on a duplicate id.</summary>
    public Device Register(Device device)
    {
        ArgumentNullException.ThrowIfNull(device);
        device.Validate();
        lock (_gate)
        {
            if (_devices.ContainsKey(device.Id))
                throw new InvalidOperationException($"A device with id '{device.Id}' is already registered.");
            _devices.Add(device.Id, device);
            return device;
        }
    }

    public Device? Get(string id)
    {
        lock (_gate) return _devices.TryGetValue(id, out var d) ? d : null;
    }

    /// <summary>Throws <see cref="KeyNotFoundException"/> when the id is unknown.</summary>
    public Device GetOrThrow(string id) =>
        Get(id) ?? throw new KeyNotFoundException($"No device with id '{id}' is registered.");

    /// <summary>Removes a device (and keeps its history — the audit trail outlives the device).
    /// Returns false when the id was unknown.</summary>
    public bool Remove(string id)
    {
        lock (_gate) return _devices.Remove(id);
    }

    /// <summary>Devices ordered by group, then name. Filters combine (AND).</summary>
    public IReadOnlyList<Device> List(string? group = null, DeviceKind? kind = null, bool onlineOnly = false)
    {
        lock (_gate)
        {
            return _devices.Values
                .Where(d => group is null || string.Equals(d.Group, group, StringComparison.OrdinalIgnoreCase))
                .Where(d => kind is null || d.Kind == kind)
                .Where(d => !onlineOnly || d.IsOnline)
                .OrderBy(d => d.Group, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>Distinct non-empty groups, sorted.</summary>
    public IReadOnlyList<string> Groups()
    {
        lock (_gate)
        {
            return _devices.Values.Select(d => d.Group)
                .Where(g => !string.IsNullOrEmpty(g))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>Moves a device to another group. Empty group means ungrouped.</summary>
    public void MoveToGroup(string id, string group)
    {
        lock (_gate) GetOrThrow(id).Group = group ?? "";
    }

    /// <summary>Marks a device online/offline and refreshes its last-seen time when coming online.</summary>
    public void SetOnline(string id, bool online)
    {
        lock (_gate)
        {
            var device = GetOrThrow(id);
            device.IsOnline = online;
            if (online) device.LastSeenUtc = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Applies a partial state update: only non-null patch fields change, the
    /// merged state is validated, and every changed field is appended to the
    /// history. A patch that changes nothing records nothing. Returns the
    /// changes that were recorded.
    /// </summary>
    public IReadOnlyList<StateChange> UpdateState(string id, DeviceState patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        lock (_gate)
        {
            var device = GetOrThrow(id);
            var merged = device.State.WithPatch(patch); // throws before any mutation on invalid values
            var now = DateTimeOffset.UtcNow;
            var changes = device.State.Diff(merged)
                .Select(d => new StateChange(id, d.Field, d.Old, d.New, now))
                .ToList();
            device.State = merged;
            device.LastSeenUtc = now;
            device.IsOnline = true;
            _history.AddRange(changes);
            return changes;
        }
    }

    /// <summary>
    /// Marks every online device not heard from within <paramref name="maxAge"/> as
    /// offline (a device that stops reporting is not "online" just because nobody
    /// told the hub). Returns the devices that flipped. Already-offline devices
    /// are untouched.
    /// </summary>
    public IReadOnlyList<Device> MarkStale(TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        lock (_gate)
        {
            var flipped = _devices.Values.Where(d => d.IsOnline && d.LastSeenUtc < cutoff).ToList();
            foreach (var d in flipped) d.IsOnline = false;
            return flipped;
        }
    }

    /// <summary>Newest-first history for one device, capped at <paramref name="limit"/> (0 = all).</summary>
    public IReadOnlyList<StateChange> History(string id, int limit = 0)
    {
        lock (_gate)
        {
            var query = _history.Where(c => c.DeviceId == id).Reverse();
            if (limit > 0) query = query.Take(limit);
            return query.ToList();
        }
    }

    /// <summary>All history, newest first (used by persistence).</summary>
    public IReadOnlyList<StateChange> AllHistory()
    {
        lock (_gate) return _history.AsEnumerable().Reverse().ToList();
    }

    internal void Restore(IEnumerable<Device> devices, IEnumerable<StateChange> history)
    {
        lock (_gate)
        {
            _devices.Clear();
            foreach (var d in devices) _devices.Add(d.Id, d);
            _history.Clear();
            _history.AddRange(history.OrderBy(c => c.At));
        }
    }
}
