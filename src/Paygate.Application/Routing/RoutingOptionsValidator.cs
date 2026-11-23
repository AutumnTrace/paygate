using Microsoft.Extensions.Options;
using Paygate.Application.Providers;

namespace Paygate.Application.Routing;

/// <summary>Fails fast on start-up if the routing table points at providers that do not exist.</summary>
public sealed class RoutingOptionsValidator(ProviderCatalog catalog) : IValidateOptions<RoutingOptions>
{
    public ValidateOptionsResult Validate(string? name, RoutingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();

        if (options.Rules.Count == 0)
        {
            errors.Add("Routing:Rules must contain at least one rule.");
        }

        foreach (var (rule, index) in options.Rules.Select((r, i) => (r, i)))
        {
            var label = string.IsNullOrWhiteSpace(rule.Name) ? $"Routing:Rules:{index}" : $"rule '{rule.Name}'";

            if (string.IsNullOrWhiteSpace(rule.Name))
            {
                errors.Add($"{label} needs a Name.");
            }

            if (rule.Targets.Count == 0)
            {
                errors.Add($"{label} has no Targets.");
            }

            if (rule is { MinAmount: { } min, MaxAmount: { } max } && min >= max)
            {
                errors.Add($"{label}: MinAmount must be lower than MaxAmount.");
            }

            foreach (var target in rule.Targets)
            {
                if (!catalog.TryGet(target.Provider, out _))
                {
                    errors.Add($"{label} targets unknown provider '{target.Provider}'. Known: {string.Join(", ", catalog.Names)}.");
                }

                if (target.Priority < 1 || target.Weight < 1)
                {
                    errors.Add($"{label}: Priority and Weight of '{target.Provider}' must be at least 1.");
                }
            }
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
