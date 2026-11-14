namespace DogRoute.Routing;

internal enum SegmentKind : byte { Literal, Param, CatchAll }

public enum ConstraintKind : byte { Int = 0, Long = 1, Guid = 2, None = 3 }

internal readonly struct PatternSegment(SegmentKind kind, string text, ConstraintKind constraint)
{
    public SegmentKind Kind { get; } = kind;
    public string Text { get; } = text;
    public ConstraintKind Constraint { get; } = constraint;
}

internal static class RoutePattern
{
    public static List<PatternSegment> Parse(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        if (pattern.Length == 0 || pattern[0] != '/')
            throw new ArgumentException($"Route pattern must start with '/': \"{pattern}\".", nameof(pattern));

        var segments = new List<PatternSegment>();
        if (pattern.Length == 1) return segments;

        string body = pattern[1..];
        if (body[^1] == '/') body = body[..^1];

        string[] parts = body.Split('/');
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            bool last = i == parts.Length - 1;

            if (part.Length == 0)
                throw Invalid(pattern, "empty segment");

            if (part[0] == '{')
            {
                if (part[^1] != '}')
                    throw Invalid(pattern, $"segment \"{part}\" is missing a closing brace");

                string inner = part[1..^1];

                if (inner.StartsWith('*'))
                {
                    if (!last) throw Invalid(pattern, "a catch-all parameter must be the last segment");

                    string name = inner[1..];
                    CheckName(name, pattern, names);
                    segments.Add(new PatternSegment(SegmentKind.CatchAll, name, ConstraintKind.None));
                }
                else
                {
                    int colon = inner.IndexOf(':');
                    string name = colon < 0 ? inner : inner[..colon];
                    ConstraintKind constraint = colon < 0
                        ? ConstraintKind.None
                        : ParseConstraint(inner[(colon + 1)..], pattern);

                    CheckName(name, pattern, names);
                    segments.Add(new PatternSegment(SegmentKind.Param, name, constraint));
                }
            }
            else
            {
                if (part.Contains('{') || part.Contains('}'))
                    throw Invalid(pattern, $"segment \"{part}\": braces must wrap the whole segment");

                if (part == "." || part == "..")
                    throw Invalid(pattern, "\".\" and \"..\" segments can never match, paths are normalized");

                segments.Add(new PatternSegment(SegmentKind.Literal, part, ConstraintKind.None));
            }
        }

        return segments;
    }

    private static void CheckName(string name, string pattern, HashSet<string> names)
    {
        if (name.Length == 0)
            throw Invalid(pattern, "parameter name is empty");

        foreach (char c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-'))
                throw Invalid(pattern, $"parameter name \"{name}\" contains invalid characters");
        }

        if (!names.Add(name))
            throw Invalid(pattern, $"duplicate parameter name \"{name}\"");
    }

    private static ConstraintKind ParseConstraint(string text, string pattern)
    {
        if (string.Equals(text, "int", StringComparison.OrdinalIgnoreCase)) return ConstraintKind.Int;
        if (string.Equals(text, "long", StringComparison.OrdinalIgnoreCase)) return ConstraintKind.Long;
        if (string.Equals(text, "guid", StringComparison.OrdinalIgnoreCase)) return ConstraintKind.Guid;

        throw Invalid(pattern, $"unknown constraint \"{text}\" (expected int, long or guid)");
    }

    private static ArgumentException Invalid(string pattern, string reason) =>
        new($"Invalid route pattern \"{pattern}\": {reason}.", "pattern");
}
