using System.Diagnostics;
using System.Text;

namespace GitRemoteSvn.Util;

public static class Proc
{
    public static async Task<(int Code, string StdOut, string StdErr)> RunAsync(
        string exe, string? cwd, IEnumerable<string> args, string? stdin = null,
        IReadOnlyDictionary<string, string>? env = null)
    {
        var sw = Stopwatch.StartNew();
        var psi = Start(exe, cwd, args, out var p, env);
        if (stdin is not null)
        {
            await p.StandardInput.WriteAsync(stdin);
            p.StandardInput.Close();
        }
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        Trace(sw, exe, psi);
        return (p.ExitCode, stdout, stderr);
    }

    /// <summary>Com GIT_REMOTE_SVN_TRACE=&lt;arquivo&gt;, cada processo externo e anotado com a duracao.</summary>
    static void Trace(Stopwatch sw, string exe, ProcessStartInfo psi)
    {
        var file = Environment.GetEnvironmentVariable("GIT_REMOTE_SVN_TRACE");
        if (string.IsNullOrEmpty(file)) return;
        lock (typeof(Proc))
            File.AppendAllText(file, $"{sw.ElapsedMilliseconds,7} ms  {Path.GetFileName(exe)} {string.Join(' ', psi.ArgumentList)}\n");
    }

    public static async Task<(int Code, byte[] StdOut, string StdErr)> RunBytesAsync(
        string exe, string? cwd, IEnumerable<string> args)
    {
        var sw = Stopwatch.StartNew();
        var psi = Start(exe, cwd, args, out var p);
        using var ms = new MemoryStream();
        await p.StandardOutput.BaseStream.CopyToAsync(ms);
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        Trace(sw, exe, psi);
        return (p.ExitCode, ms.ToArray(), stderr);
    }

    static ProcessStartInfo Start(string exe, string? cwd, IEnumerable<string> args, out Process p,
        IReadOnlyDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = cwd ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null)
            foreach (var (k, v) in env) psi.Environment[k] = v;
        p = Process.Start(psi)!;
        return psi;
    }
}
