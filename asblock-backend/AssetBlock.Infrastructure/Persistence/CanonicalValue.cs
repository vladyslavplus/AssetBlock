using System.Globalization;

namespace AssetBlock.Infrastructure.Persistence;

internal enum CanonicalValueKind
{
    NULL,
    STRING,
    BOOL,
    NUMBER,
    ARRAY,
    OBJECT
}

/// <summary>
/// Minimal value tree for canonical digests. Object members are kept ordinally sorted so the
/// emitted JSON matches the service canonical form without relying on caller ordering.
/// </summary>
internal sealed class CanonicalValue
{
    private CanonicalValue(CanonicalValueKind kind) => Kind = kind;

    public CanonicalValueKind Kind { get; }

    public string ScalarText { get; private init; } = "";

    public bool ScalarBool { get; private init; }

    public IReadOnlyList<CanonicalValue> Items { get; private init; } = [];

    public IReadOnlyList<KeyValuePair<string, CanonicalValue>> Members { get; private init; } = [];

    public static CanonicalValue Null() => new(CanonicalValueKind.NULL);

    public static CanonicalValue Text(string value) =>
        new(CanonicalValueKind.STRING) { ScalarText = value };

    public static CanonicalValue Bool(bool value) =>
        new(CanonicalValueKind.BOOL) { ScalarBool = value };

    public static CanonicalValue Number(long value) =>
        new(CanonicalValueKind.NUMBER) { ScalarText = value.ToString(CultureInfo.InvariantCulture) };

    public static CanonicalValue Number(int value) =>
        new(CanonicalValueKind.NUMBER) { ScalarText = value.ToString(CultureInfo.InvariantCulture) };

    public static CanonicalValue Number(double value) =>
        new(CanonicalValueKind.NUMBER) { ScalarText = value.ToString("R", CultureInfo.InvariantCulture) };

    public static CanonicalValue Array(IReadOnlyList<CanonicalValue> items) =>
        new(CanonicalValueKind.ARRAY) { Items = items };

    public static CanonicalValue Object(params (string Key, CanonicalValue Value)[] members)
    {
        var sorted = new List<KeyValuePair<string, CanonicalValue>>(members.Length);
        foreach ((string Key, CanonicalValue Value) member in members)
        {
            sorted.Add(new KeyValuePair<string, CanonicalValue>(member.Key, member.Value));
        }

        sorted.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key));
        return new CanonicalValue(CanonicalValueKind.OBJECT) { Members = sorted };
    }
}
