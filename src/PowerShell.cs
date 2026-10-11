using System.Diagnostics;
using System.Text;

namespace WukongBenchmarkRunner;

public static class PowerShell
{
    public static async Task<string> RunScriptAsync(string code, CancellationToken cancellation = default)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; " + code)));
        using var process = Process.Start(psi) ?? throw new IOException("Не удалось запустить Windows PowerShell.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellation);
        var stderr = process.StandardError.ReadToEndAsync(cancellation);
        await process.WaitForExitAsync(cancellation);
        var output = await stdout;
        var errors = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException("PowerShell: " + errors + " " + output);
        return output.Trim();
    }
}
