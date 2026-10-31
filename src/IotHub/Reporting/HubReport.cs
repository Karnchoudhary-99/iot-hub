using IotHub.Automation;
using IotHub.Devices;
using IotHub.Scenes;

namespace IotHub.Reporting;

/// <summary>
/// A point-in-time summary of the whole hub: how many devices it manages,
/// where they live, how they are doing, what automations exist, and which
/// devices have been the most active. Built purely from the registry, the
/// rule list and the scene list — no new state of its own, so a report can
/// never disagree with the stores it summarises.
/// </summary>
public sealed record HubReport(
    int TotalDevices,
    int OnlineDevices,
    int OfflineDevices,
    IReadOnlyDictionary<string, int> DevicesByGroup,
    IReadOnlyDictionary<string, int> DevicesByKind,
    int RulesTotal,
    int RulesEnabled,
    int ScenesTotal,
    int TotalStateChanges,
    IReadOnlyDictionary<string, int> ChangesByDevice,
    string? MostChangedDevice,
    IReadOnlyList<string> StaleDevices)
{
    /// <summary>Builds a report from the hub's current state. Devices that
    /// are online but unheard from within <paramref name="staleAfter"/>
    /// are listed as stale — the same definition the CLI's sweep uses.</summary>
    public static HubReport Build(
        DeviceRegistry registry,
        IReadOnlyList<AutomationRule> rules,
        IReadOnlyList<Scene> scenes,
        TimeSpan staleAfter,
        Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(scenes);
        var at = (now ?? (() => DateTimeOffset.UtcNow))();
        var devices = registry.List();
        var byGroup = devices
            .GroupBy(d => string.IsNullOrEmpty(d.Group) ? "(ungrouped)" : d.Group, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var byKind = devices
            .GroupBy(d => d.Kind.ToString(), StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var stale = devices
            .Where(d => d.IsOnline && d.LastSeenUtc < at - staleAfter)
            .Select(d => d.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        // History stats are filled in by WithHistory — Build keeps the
        // counts-only shape so callers that only need inventory skip the
        // history scan.
        return new HubReport(
            devices.Count,
            devices.Count(d => d.IsOnline),
            devices.Count(d => !d.IsOnline),
            byGroup,
            byKind,
            rules.Count,
            rules.Count(r => r.Enabled),
            scenes.Count,
            0,
            new Dictionary<string, int>(),
            null,
            stale);
    }

    /// <summary>Returns a copy of this report with history-derived activity
    /// filled in: total changes, per-device counts, and the most-changed
    /// device (null when nothing has ever changed).</summary>
    public HubReport WithHistory(DeviceRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var byDevice = registry.AllHistory()
            .GroupBy(c => c.DeviceId, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return this with
        {
            TotalStateChanges = byDevice.Values.Sum(),
            ChangesByDevice = byDevice,
            MostChangedDevice = byDevice.Count == 0 ? null : byDevice.First().Key,
        };
    }
}
