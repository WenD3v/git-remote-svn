using System.Globalization;
using System.Text;

namespace GitRemoteSvn.Svn;

/// <summary>No de um dump. <see cref="Props"/> null = sem bloco de propriedades; valor null = propriedade removida (delta).</summary>
public record DumpNode(string Path, string? Kind, char Action, string? CopyFromPath, long? CopyFromRev,
    bool TextDelta, byte[]? Text, string? TextMd5, bool PropDelta, IReadOnlyDictionary<string, string?>? Props);

public record DumpRevision(long Revision, IReadOnlyDictionary<string, string?> Props, IReadOnlyList<DumpNode> Nodes);

/// <summary>Le o formato <c>svn-fs-dump</c> (versoes 2 e 3) revisao a revisao, sem carregar o arquivo inteiro.</summary>
public static class SvnDumpReader
{
    public static IEnumerable<DumpRevision> Read(Stream stream)
    {
        var input = new BufferedStream(stream, 1 << 16);
        DumpRevision? current = null;
        List<DumpNode>? nodes = null;
        while (true)
        {
            var headers = ReadHeaders(input);
            if (headers is null) break;
            var contentLen = Long(headers, "Content-length") ?? 0;
            var propLen = Long(headers, "Prop-content-length") ?? 0;
            var textLen = Long(headers, "Text-content-length");

            if (headers.TryGetValue("Revision-number", out var revText))
            {
                var props = ReadProps(ReadExact(input, contentLen));
                if (current is not null) yield return current;
                nodes = new List<DumpNode>();
                current = new DumpRevision(long.Parse(revText, CultureInfo.InvariantCulture), props, nodes);
            }
            else if (headers.TryGetValue("Node-path", out var path))
            {
                var body = ReadExact(input, contentLen);
                var props = headers.ContainsKey("Prop-content-length") ? ReadProps(body.AsSpan(0, (int)propLen).ToArray()) : null;
                var text = textLen is null ? null : body.AsSpan((int)propLen, (int)textLen.Value).ToArray();
                var action = headers.GetValueOrDefault("Node-action") switch
                {
                    "add" => 'A', "delete" => 'D', "replace" => 'R', _ => 'M',
                };
                nodes?.Add(new DumpNode(path, headers.GetValueOrDefault("Node-kind"), action,
                    headers.GetValueOrDefault("Node-copyfrom-path"), Long(headers, "Node-copyfrom-rev"),
                    headers.GetValueOrDefault("Text-delta") == "true", text, headers.GetValueOrDefault("Text-content-md5"),
                    headers.GetValueOrDefault("Prop-delta") == "true", props));
            }
            else
            {
                ReadExact(input, contentLen); // SVN-fs-dump-format-version, UUID
            }
        }
        if (current is not null) yield return current;
    }

    static long? Long(Dictionary<string, string> h, string key) =>
        h.TryGetValue(key, out var v) ? long.Parse(v, CultureInfo.InvariantCulture) : null;

    /// <summary>Bloco "Chave: valor" ate a linha vazia; pula linhas vazias antes. null no fim do arquivo.</summary>
    static Dictionary<string, string>? ReadHeaders(Stream s)
    {
        string? line;
        do
        {
            line = ReadLine(s);
            if (line is null) return null;
        } while (line.Length == 0);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (!string.IsNullOrEmpty(line))
        {
            var i = line.IndexOf(": ", StringComparison.Ordinal);
            if (i > 0) result[line[..i]] = line[(i + 2)..];
            line = ReadLine(s);
        }
        return result;
    }

    static string? ReadLine(Stream s)
    {
        var buf = new List<byte>(64);
        while (true)
        {
            var b = s.ReadByte();
            if (b < 0) return buf.Count == 0 ? null : Encoding.UTF8.GetString(buf.ToArray());
            if (b == '\n') return Encoding.UTF8.GetString(buf.ToArray());
            buf.Add((byte)b);
        }
    }

    static byte[] ReadExact(Stream s, long len)
    {
        var data = new byte[len];
        s.ReadExactly(data);
        return data;
    }

    /// <summary>"K n / chave / V n / valor" (e "D n / chave" em delta) ate PROPS-END.</summary>
    static Dictionary<string, string?> ReadProps(byte[] block)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        var p = 0;
        while (p < block.Length)
        {
            var line = Line(block, ref p);
            if (line == "PROPS-END") break;
            var kind = line[0];
            var kLen = int.Parse(line[2..], CultureInfo.InvariantCulture);
            var key = Encoding.UTF8.GetString(block, p, kLen);
            p += kLen + 1;
            if (kind == 'D') { result[key] = null; continue; }
            var vLine = Line(block, ref p);
            var vLen = int.Parse(vLine[2..], CultureInfo.InvariantCulture);
            result[key] = Encoding.UTF8.GetString(block, p, vLen);
            p += vLen + 1;
        }
        return result;
    }

    static string Line(byte[] b, ref int p)
    {
        var end = Array.IndexOf(b, (byte)'\n', p);
        if (end < 0) end = b.Length;
        var s = Encoding.ASCII.GetString(b, p, end - p);
        p = end + 1;
        return s;
    }
}
