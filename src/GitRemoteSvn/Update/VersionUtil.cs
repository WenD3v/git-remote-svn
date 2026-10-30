using System.Globalization;
using System.Reflection;

namespace GitRemoteSvn.Update;

/// <summary>Versao do helper e comparacao com a tag publicada no GitHub.</summary>
public static class VersionUtil
{
    /// <summary>Versao em execucao (informativa, sem metadata de build e sem sufixo de pre-release).</summary>
    public static string Current { get; } = ReadCurrent();

    /// <summary>Tira o <c>v</c> inicial e a metadata <c>+sha</c>; mantem o sufixo de pre-release.</summary>
    public static string Normalize(string tag)
    {
        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        var plus = text.IndexOf('+');
        return plus < 0 ? text : text[..plus];
    }

    /// <summary>Compara componente a componente; pre-release nunca e mais nova que a mesma versao estavel.</summary>
    public static bool IsNewer(string candidate, string current) => Compare(candidate, current) > 0;

    public static bool IsPrerelease(string tag) => Normalize(tag).Contains('-');

    static int Compare(string candidate, string current)
    {
        var (a, aParts) = Core(candidate);
        var (b, bParts) = Core(current);
        var len = Math.Max(aParts.Length, bParts.Length);
        for (var i = 0; i < len; i++)
        {
            var x = i < aParts.Length ? aParts[i] : 0;
            var y = i < bParts.Length ? bParts[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        // mesma versao numerica: so a estavel supera uma pre-release
        if (a.Length > 0 && b.Length == 0) return -1;
        return 0;
    }

    static (string Suffix, int[] Parts) Core(string tag)
    {
        var normalized = Normalize(tag);
        var dash = normalized.IndexOf('-');
        var core = dash < 0 ? normalized : normalized[..dash];
        var suffix = dash < 0 ? "" : normalized[dash..];
        var parts = core.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0)
            .ToArray();
        return (suffix, parts);
    }

    static string ReadCurrent()
    {
        var info = typeof(VersionUtil).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var text = Normalize(info ?? "1.0.0");
        var dash = text.IndexOf('-');
        if (dash >= 0) text = text[..dash];
        var parts = text.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (parts.Count < 3) parts.Add("0");
        return string.Join('.', parts.Take(3));
    }
}
