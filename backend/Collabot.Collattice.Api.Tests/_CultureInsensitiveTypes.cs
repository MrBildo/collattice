using Meziantou.Analyzer.Annotations;

// The same marker as the API project's: a Ulid's text does not depend on the culture. The
// attribute type is the API project's own, visible here through InternalsVisibleTo.
[assembly: CultureInsensitiveType(typeof(Ulid))]
