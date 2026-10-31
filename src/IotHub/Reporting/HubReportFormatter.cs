using System.Text;

namespace IotHub.Reporting;

/// <summary>Renders a <see cref="HubReport"/> as plain text for the CLI.
/// Sections are omitted when empty (a hub with no rules prints no
/// automation section), so a small hub's report stays short.</summary>
public static class HubReportFormatter
{
    public static string ToText(HubReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var text = new StringBuilder();
        text.AppendLine("Hub report");
        text.AppendLine($"  Devices: {report.TotalDevices} total, {report.OnlineDevices} online, {report.OfflineDevices} offline");
        foreach (var (group, count) in report.DevicesByGroup)
            text.AppendLine($"  Group {group}: {count}");
        foreach (var (kind, count) in report.DevicesByKind)
            text.AppendLine($"  Kind {kind}: {count}");
        if (report.RulesTotal > 0 || report.ScenesTotal > 0)
            text.AppendLine($"  Automation: {report.RulesTotal} rule(s) ({report.RulesEnabled} enabled), {report.ScenesTotal} scene(s)");
        if (report.TotalStateChanges > 0)
        {
            text.AppendLine($"  State changes recorded: {report.TotalStateChanges}");
            if (report.MostChangedDevice is not null)
                text.AppendLine($"  Most active device: {report.MostChangedDevice} ({report.ChangesByDevice[report.MostChangedDevice]} changes)");
        }
        if (report.StaleDevices.Count > 0)
            text.AppendLine($"  Stale devices (silent over the window): {string.Join(", ", report.StaleDevices)}");
        return text.ToString();
    }
}
