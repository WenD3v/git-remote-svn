using System.IO.Compression;

namespace GitRemoteSvn.Svn;

/// <summary>Aplica deltas svndiff (versoes 0 e 1), o formato dos <c>Text-delta</c> que o <c>svnrdump</c> gera.</summary>
public static class SvnDiff
{
    public static byte[] Apply(byte[] source, byte[] delta)
    {
        if (delta.Length < 4 || delta[0] != 'S' || delta[1] != 'V' || delta[2] != 'N')
            throw new InvalidDataException("svndiff: cabecalho invalido");
        var version = delta[3];
        if (version > 1) throw new InvalidDataException($"svndiff versao {version} nao suportada");

        using var output = new MemoryStream();
        var pos = 4;
        while (pos < delta.Length)
        {
            var srcOffset = ReadInt(delta, ref pos);
            ReadInt(delta, ref pos); // tamanho da janela de origem: as instrucoes ja limitam
            var targetLen = (int)ReadInt(delta, ref pos);
            var insLen = (int)ReadInt(delta, ref pos);
            var newLen = (int)ReadInt(delta, ref pos);
            var ins = Section(delta, pos, insLen, version);
            pos += insLen;
            var data = Section(delta, pos, newLen, version);
            pos += newLen;

            var target = new byte[targetLen];
            int t = 0, ip = 0, dp = 0;
            while (ip < ins.Length)
            {
                var op = ins[ip] >> 6;
                var len = (int)(ins[ip++] & 0x3f);
                if (len == 0) len = (int)ReadInt(ins, ref ip);
                switch (op)
                {
                    case 0: // copia da janela de origem
                        Array.Copy(source, srcOffset + ReadInt(ins, ref ip), target, t, len);
                        t += len;
                        break;
                    case 1: // copia do proprio alvo; pode sobrepor, entao byte a byte
                        var off = (int)ReadInt(ins, ref ip);
                        for (var i = 0; i < len; i++) target[t++] = target[off + i];
                        break;
                    case 2: // dados novos
                        Array.Copy(data, dp, target, t, len);
                        dp += len;
                        t += len;
                        break;
                    default:
                        throw new InvalidDataException("svndiff: instrucao invalida");
                }
            }
            if (t != targetLen) throw new InvalidDataException("svndiff: janela com tamanho divergente");
            output.Write(target);
        }
        return output.ToArray();
    }

    static long ReadInt(byte[] b, ref int p)
    {
        long v = 0;
        while (true)
        {
            var c = b[p++];
            v = (v << 7) | (uint)(c & 0x7f);
            if ((c & 0x80) == 0) return v;
        }
    }

    /// <summary>Na versao 1 cada secao comeca pelo tamanho original e vem em zlib quando isso a encolhe.</summary>
    static byte[] Section(byte[] b, int start, int len, byte version)
    {
        if (version == 0) return b.AsSpan(start, len).ToArray();
        var p = start;
        var original = (int)ReadInt(b, ref p);
        var rest = len - (p - start);
        if (rest == original) return b.AsSpan(p, rest).ToArray();
        using var z = new ZLibStream(new MemoryStream(b, p, rest), CompressionMode.Decompress);
        var result = new byte[original];
        z.ReadExactly(result);
        return result;
    }
}
