using Meziantou.Analyzer.Annotations;

// A Ulid's text is its fixed 26-character Crockford Base32 form whatever the culture: Ulid's
// provider-taking ToString overload ignores the provider. Marking the type here keeps the
// culture rules (MA0011, MA0075, MA0076) from asking every ToString() call for a culture that
// changes nothing.
[assembly: CultureInsensitiveType(typeof(Ulid))]

namespace Meziantou.Analyzer.Annotations;

// Meziantou.Analyzer finds this attribute by its name and namespace, so the project declares
// it rather than taking the annotations package. It is conditional, so nothing is emitted.
[System.Diagnostics.Conditional("MEZIANTOU_ANALYZER_ANNOTATIONS")]
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
internal sealed class CultureInsensitiveTypeAttribute(Type type) : Attribute
{
    public Type Type { get; } = type;
}
