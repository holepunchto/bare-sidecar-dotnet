using System.Diagnostics;
using System.Text;
using Xunit;

namespace Holepunch.BareSidecar.Tests;

public sealed class SidecarTests : IDisposable
{
    private readonly List<Sidecar> _sidecars = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static ProcessStartInfo Command(string fixture, params string[] args)
    {
        return new ProcessStartInfo(Sidecar.Executable, [Path.Combine(AppContext.BaseDirectory, "fixtures", fixture), .. args]);
    }

    private Sidecar Start(ProcessStartInfo info)
    {
        var sidecar = Sidecar.Start(info);

        _sidecars.Add(sidecar);

        return sidecar;
    }

    private static async Task<string> ReadAllAsync(Sidecar sidecar)
    {
        using var reader = new StreamReader(sidecar, leaveOpen: true);

        return await reader.ReadToEndAsync(Token);
    }

    private static async Task<int> WaitAsync(Sidecar sidecar)
    {
        await sidecar.Process.WaitForExitAsync(Token);

        return sidecar.Process.ExitCode;
    }

    public void Dispose()
    {
        foreach (var sidecar in _sidecars)
        {
            sidecar.Dispose();
            sidecar.Process.Kill();
            sidecar.Process.WaitForExit();
            sidecar.Process.Dispose();
        }
    }

    [Fact]
    public async Task EchoesIpcData()
    {
        var sidecar = Start(Command("echo.js"));

        await sidecar.WriteAsync("Hello world"u8.ToArray(), Token);

        var data = new byte[11];

        await sidecar.ReadExactlyAsync(data, Token);

        Assert.Equal("Hello world", Encoding.UTF8.GetString(data));
    }

    [Fact]
    public async Task PassesArguments()
    {
        var sidecar = Start(Command("argv.js", "foo", "bar"));

        Assert.Equal("""["foo","bar"]""", await ReadAllAsync(sidecar));
        Assert.Equal(0, await WaitAsync(sidecar));
    }

    [Fact]
    public async Task RunsTheBundledBareVersion()
    {
        var sidecar = Start(Command("version.js"));

        Assert.Equal($"v{Sidecar.Versions["bare"]}", await ReadAllAsync(sidecar));
    }

    [Fact]
    public async Task ReportsTheExitCode()
    {
        var sidecar = Start(Command("exit.js"));

        Assert.Equal(3, await WaitAsync(sidecar));
        Assert.Equal("", await ReadAllAsync(sidecar));
    }

    [Fact]
    public async Task EndsOnceTheProcessHasExited()
    {
        var sidecar = Start(Command("end-exit.js"));

        Assert.Equal("", await ReadAllAsync(sidecar));
        Assert.True(sidecar.Process.HasExited);
        Assert.Equal(3, sidecar.Process.ExitCode);
    }

    [Fact]
    public void EndsOnceTheProcessHasExitedWhenReadSynchronously()
    {
        var sidecar = Start(Command("end-exit.js"));

        using var reader = new StreamReader(sidecar, leaveOpen: true);

        Assert.Equal("", reader.ReadToEnd());
        Assert.True(sidecar.Process.HasExited);
        Assert.Equal(3, sidecar.Process.ExitCode);
    }

    [Fact]
    public async Task ExitsOnceDisposed()
    {
        var sidecar = Start(Command("echo.js"));

        sidecar.Dispose();

        Assert.Equal(0, await WaitAsync(sidecar));
    }

    [Fact]
    public async Task ExitsAfterIpcDestroyWhileWriting()
    {
        var sidecar = Start(Command("ipc-destroy-exit.js"));

        var exited = sidecar.Process.WaitForExitAsync(Token);

        while (!exited.IsCompleted)
        {
            try
            {
                sidecar.Write("Hello world"u8);
            }
            catch (IOException)
            {
            }

            await Task.WhenAny(exited, Task.Delay(10, Token));
        }

        Assert.Equal(3, sidecar.Process.ExitCode);
    }

    [Fact]
    public async Task PipesStdout()
    {
        var info = Command("log.js");

        info.RedirectStandardOutput = true;

        var sidecar = Start(info);

        Assert.Equal("Hello world", (await sidecar.Process.StandardOutput.ReadToEndAsync(Token)).Trim());
        Assert.Equal(0, await WaitAsync(sidecar));
    }

    [Fact]
    public async Task Terminates()
    {
        var sidecar = Start(Command("sleep.js"));

        sidecar.Terminate();

        Assert.Equal(OperatingSystem.IsWindows() ? 1 : 128 + 15, await WaitAsync(sidecar));

        sidecar.Terminate();
    }

    [Fact]
    public async Task StreamsLargeData()
    {
        var data = new byte[256 * 4096];

        for (var i = 0; i < data.Length; i++) data[i] = (byte)i;

        var sidecar = Start(Command("echo.js"));

        var written = sidecar.WriteAsync(data, Token).AsTask();

        var echoed = new byte[data.Length];

        await sidecar.ReadExactlyAsync(echoed, Token);
        await written;

        Assert.Equal(data, echoed);
    }
}
