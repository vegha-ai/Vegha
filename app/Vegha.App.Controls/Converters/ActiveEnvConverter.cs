using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using DomainEnv = Vegha.Core.Domain.Environment;

namespace Vegha.App.Controls.Converters;

/// <summary>
/// Multi-binding converter that returns <c>true</c> when the row env matches the panel's
/// currently-active env. Used by the master env list to render the green check-mark on
/// the active row. Compares by <c>Id</c> when both are set; falls back to <c>Name</c> for
/// back-compat with envs loaded before the Id field existed.
/// </summary>
public sealed class ActiveEnvConverter : IMultiValueConverter
{
    public static readonly ActiveEnvConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2) return false;
        if (values[0] is not DomainEnv row || values[1] is not DomainEnv active) return false;
        if (!string.IsNullOrEmpty(row.Id) && !string.IsNullOrEmpty(active.Id))
            return string.Equals(row.Id, active.Id, StringComparison.Ordinal);
        return !string.IsNullOrEmpty(row.Name)
            && string.Equals(row.Name, active.Name, StringComparison.OrdinalIgnoreCase);
    }
}
