using System.Text.Json;
using System.Text.Json.Nodes;

namespace Collabot.Collattice.Api.Installation;

public static class AppSettingsMerger
{
    public static MergeResult Merge(JsonNode shipped, JsonNode current, JsonNode? baseline)
    {
        ArgumentNullException.ThrowIfNull(shipped);
        ArgumentNullException.ThrowIfNull(current);

        if (shipped is not JsonObject shippedObj)
        {
            throw new ArgumentException("shipped must be a JSON object at the root", nameof(shipped));
        }

        if (current is not JsonObject currentObj)
        {
            throw new ArgumentException("current must be a JSON object at the root", nameof(current));
        }

        if (baseline is not null and not JsonObject)
        {
            throw new ArgumentException("baseline must be a JSON object at the root", nameof(baseline));
        }

        var changes = new List<MergeChange>();
        var merged = MergeObject(shippedObj, currentObj, baseline as JsonObject, string.Empty, changes);

        return new MergeResult(merged, changes, baseline is null);
    }

    private static JsonObject MergeObject
    (
        JsonObject shipped,
        JsonObject current,
        JsonObject? baseline,
        string path,
        List<MergeChange> changes
    )
    {
        var result = new JsonObject();

        // Pass 1: walk the union of keys, preferring shipped's order so the merged file looks like
        // a fresh shipped file with operator edits layered on, not the operator's reordered tree.
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (key, shippedValue) in shipped)
        {
            seen.Add(key);
            var keyPath = path.Length == 0 ? key : $"{path}:{key}";

            var currentHas = current.TryGetPropertyValue(key, out var currentValue);
            JsonNode? baselineValue = null;
            var baselineHas = baseline is not null && baseline.TryGetPropertyValue(key, out baselineValue);

            if (!currentHas)
            {
                // Operator file does not have the key. Could mean the operator deleted it, or it's
                // a new key in shipped. We add it either way: a deleted key on disk reads identically
                // to a not-yet-seen key, and the conservative choice is to give the operator the new
                // shipped value so the install is not silently missing functionality.
                result[key] = Clone(shippedValue);
                changes.Add(new MergeChange(keyPath, MergeChangeKind.Added));
                continue;
            }

            // Both present. Recurse on objects, three-way scalar/array compare otherwise.
            if (shippedValue is JsonObject shippedChild && currentValue is JsonObject currentChild)
            {
                var baselineChild = baselineValue as JsonObject;
                result[key] = MergeObject(shippedChild, currentChild, baselineChild, keyPath, changes);
                continue;
            }

            // Type mismatch (object on one side, scalar/array on the other) or scalar/array on both.
            // Treat as a leaf decision.
            if (baseline is null || !baselineHas)
            {
                // No baseline knowledge for this key. Conservative: keep operator's value.
                result[key] = Clone(currentValue);
                changes.Add(new MergeChange(keyPath, MergeChangeKind.PreservedConservative));
                continue;
            }

            if (JsonNodeEquals(currentValue, baselineValue))
            {
                // Operator never touched this key. Refresh to the new shipped default unless the
                // value is unchanged (no-op).
                if (JsonNodeEquals(shippedValue, currentValue))
                {
                    result[key] = Clone(currentValue);
                }
                else
                {
                    result[key] = Clone(shippedValue);
                    changes.Add(new MergeChange(keyPath, MergeChangeKind.RefreshedDefault));
                }
            }
            else
            {
                // Operator has edited this key. Preserve the operator's value; never silently
                // overwrite an operator edit even when shipped has changed too. We surface the
                // preservation either way -- the operator may want to know shipped's default
                // moved, but the merger never pre-empts their edit.
                result[key] = Clone(currentValue);
                changes.Add(new MergeChange(keyPath, MergeChangeKind.PreservedOperatorEdit));
            }
        }

        // Pass 2: keys present in current but absent from shipped. The shipped file no longer
        // contains the key, but we keep the operator's value because (a) the operator might be
        // using a key the new release also reads via in-code fallback, and (b) silently dropping
        // operator data is the wrong default. The merge result therefore is a strict superset of
        // both shipped and current.
        foreach (var (key, currentValue) in current)
        {
            if (seen.Contains(key))
            {
                continue;
            }

            var keyPath = path.Length == 0 ? key : $"{path}:{key}";
            result[key] = Clone(currentValue);
            changes.Add(new MergeChange(keyPath, MergeChangeKind.PreservedExtraKey));
        }

        return result;
    }

    private static JsonNode? Clone(JsonNode? node) => node?.DeepClone();

    private static bool JsonNodeEquals(JsonNode? left, JsonNode? right)
    {
        if (left is null && right is null)
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        // ToJsonString round-trips through canonical JSON. Cheaper than implementing structural
        // comparison by hand and exact enough for our purposes (we only ever compare values
        // produced by JsonNode.Parse, so number formatting is consistent).
        return string.Equals(left.ToJsonString(), right.ToJsonString(), StringComparison.Ordinal);
    }
}

public sealed record MergeResult
(
    JsonObject Merged,
    IReadOnlyList<MergeChange> Changes,
    bool Conservative
)
{
    public bool HasChanges => Changes.Any(c => c.Kind is MergeChangeKind.Added or MergeChangeKind.RefreshedDefault);

    public string ToJsonString()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };

        return Merged.ToJsonString(options) + Environment.NewLine;
    }
}

public sealed record MergeChange(string Path, MergeChangeKind Kind);

public enum MergeChangeKind
{
    Added,
    RefreshedDefault,
    PreservedOperatorEdit,
    PreservedConservative,
    PreservedExtraKey
}
