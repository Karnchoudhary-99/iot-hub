using IotHub.Devices;

namespace IotHub.Automation;

/// <summary>What one rule evaluation produced for one rule.</summary>
public sealed record RuleFiring(
    string RuleId,
    string RuleName,
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> Skipped);

/// <summary>
/// Evaluates rules against a registry, in list order. Safety rules, in order:
/// disabled rules never fire; a rule inside its cooldown never fires; an
/// action whose target is unknown or offline is skipped (the hub cannot act
/// on a device it cannot reach, and must not mark it online by "acting" on
/// it); a firing whose every action was skipped does not consume the
/// cooldown, so the rule retries on the next evaluation instead of going
/// quiet for the cooldown window. Actions go through
/// <see cref="DeviceRegistry.UpdateState"/> so range validation and history
/// recording apply exactly as they do for a manual set-state.
/// </summary>
public static class AutomationEngine
{
    /// <summary>Evaluates all rules against the registry's current state.
    /// Mutates <see cref="AutomationRule.LastFiredUtc"/> on rules that fire
    /// cleanly; callers persist the rule set afterwards.</summary>
    public static IReadOnlyList<RuleFiring> Evaluate(
        DeviceRegistry registry,
        IReadOnlyList<AutomationRule> rules,
        Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(rules);
        var at = (now ?? (() => DateTimeOffset.UtcNow))();
        var firings = new List<RuleFiring>();

        foreach (var rule in rules)
        {
            if (!rule.Enabled) continue;
            if (rule.LastFiredUtc is not null &&
                at - rule.LastFiredUtc.Value < TimeSpan.FromSeconds(rule.CooldownSeconds))
                continue;
            var trigger = registry.Get(rule.When.DeviceId);
            if (trigger is null) continue;
            if (!rule.When.Matches(trigger)) continue;

            var applied = new List<string>();
            var skipped = new List<string>();
            foreach (var action in rule.Actions)
            {
                var target = registry.Get(action.TargetDeviceId);
                if (target is null)
                {
                    skipped.Add($"{action.TargetDeviceId}: unknown device");
                    continue;
                }
                if (!target.IsOnline)
                {
                    skipped.Add($"{action.TargetDeviceId}: offline");
                    continue;
                }
                var changes = registry.UpdateState(action.TargetDeviceId, action.SetState);
                foreach (var change in changes)
                    applied.Add($"{action.TargetDeviceId}.{change.Field}={change.NewValue}");
                if (changes.Count == 0)
                    applied.Add($"{action.TargetDeviceId}: already in target state");
            }

            // A clean firing (nothing skipped for reachability) consumes the
            // cooldown even when every action was already satisfied; a firing
            // blocked on an unreachable target retries next evaluation.
            if (skipped.Count == 0) rule.LastFiredUtc = at;
            firings.Add(new RuleFiring(rule.Id, rule.Name, applied, skipped));
        }

        return firings;
    }
}
