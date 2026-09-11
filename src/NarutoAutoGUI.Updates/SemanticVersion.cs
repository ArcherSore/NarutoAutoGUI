using System.Globalization;
using System.Text.RegularExpressions;

namespace NarutoAutoGUI.Updates;

public sealed class SemanticVersion : IComparable<SemanticVersion>
{
    private static readonly Regex Pattern = new(
        @"^v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)"
        + @"(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private readonly (bool Numeric, uint Number, string Text)[] _prerelease;

    private SemanticVersion(uint major, uint minor, uint patch,
        (bool Numeric, uint Number, string Text)[] prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _prerelease = prerelease;
    }

    public uint Major { get; }
    public uint Minor { get; }
    public uint Patch { get; }

    public static SemanticVersion Parse(string value)
    {
        var match = Pattern.Match(value);
        if (!match.Success) {
            throw new InvalidDataException("无效的 MaaNOP 版本。");
        }
        var identifiers = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
        var prerelease = new (bool Numeric, uint Number, string Text)[identifiers.Length];
        for (var i = 0; i < identifiers.Length; i++) {
            var part = identifiers[i];
            var numeric = part.All(char.IsAsciiDigit);
            if (numeric && part.Length > 1 && part[0] == '0') {
                throw new InvalidDataException("无效的 Semantic Version 预发布版本。");
            }
            prerelease[i] = (numeric, numeric ? ParseNumber(part) : 0, part);
        }
        return new SemanticVersion(ParseNumber(match.Groups[1].Value),
            ParseNumber(match.Groups[2].Value), ParseNumber(match.Groups[3].Value), prerelease);
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) {
            return 1;
        }
        var result = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (result != 0) {
            return result;
        }
        if (_prerelease.Length == 0 || other._prerelease.Length == 0) {
            return (_prerelease.Length == 0).CompareTo(other._prerelease.Length == 0);
        }
        for (var i = 0; i < Math.Min(_prerelease.Length, other._prerelease.Length); i++) {
            var (left, right) = (_prerelease[i], other._prerelease[i]);
            var identifier = (left.Numeric, right.Numeric) switch {
                (true, true) => left.Number.CompareTo(right.Number),
                (true, false) => -1,  // Numeric identifiers rank below alphanumeric ones.
                (false, true) => 1,
                _ => string.CompareOrdinal(left.Text, right.Text)
            };
            if (identifier != 0) {
                return identifier;
            }
        }
        return _prerelease.Length.CompareTo(other._prerelease.Length);
    }

    private static uint ParseNumber(string value)
    {
        if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) {
            throw new InvalidDataException("MaaNOP 版本数字超出支持范围。");
        }
        return number;
    }
}
