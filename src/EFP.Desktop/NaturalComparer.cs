using System.Text.RegularExpressions;

namespace EFP.Desktop;

/// <summary>Tri « naturel » : M404 avant M1132, MFC-L3750 avant MFC-L3760.</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();
    private static readonly Regex Parts = new(@"\d+|\D+", RegexOptions.Compiled);

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null) return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
        var a = Parts.Matches(x); var b = Parts.Matches(y);
        for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            var pa = a[i].Value; var pb = b[i].Value;
            int c = char.IsDigit(pa[0]) && char.IsDigit(pb[0]) && pa.Length < 10 && pb.Length < 10
                ? long.Parse(pa).CompareTo(long.Parse(pb))
                : string.Compare(pa, pb, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
        }
        return a.Count.CompareTo(b.Count);
    }
}
