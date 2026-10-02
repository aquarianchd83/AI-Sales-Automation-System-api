using Microsoft.Extensions.Options;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>An <see cref="IOptionsSnapshot{T}"/> holding one fixed value, for services that read their settings per request.</summary>
internal sealed class Snapshot<T> : IOptionsSnapshot<T> where T : class
{
    public Snapshot(T value) => Value = value;

    public T Value { get; }

    public T Get(string? name) => Value;
}

internal static class Snapshot
{
    public static IOptionsSnapshot<T> Of<T>(T value) where T : class => new Snapshot<T>(value);
}
