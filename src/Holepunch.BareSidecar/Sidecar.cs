using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Holepunch.BareSidecar;

/// <summary>
/// A Bare process connected to its <c>Bare.IPC</c>.
/// </summary>
/// <remarks>
/// Reading yields the data that the process writes to <c>Bare.IPC</c> until it
/// ends the connection and exits, and writing sends data to it. One reader and one
/// writer may use the sidecar at the same time. Disposing the sidecar closes
/// the connection, after which the process exits on its own unless it keeps
/// <c>Bare.IPC</c> open. Use <see cref="Start"/> to start one.
/// </remarks>
public sealed class Sidecar : Stream
{
    // Read by the embedder, which opens `Bare.IPC` from the descriptor, or on
    // Windows the handle, it names.
    private const string IpcEnv = "BARE_SIDECAR_IPC";

    // Held while a connection is passed, so that processes started at the same
    // time never inherit each other's connection.
    private static readonly Lock s_passing = new();

    private readonly Connection _connection;

    private Sidecar(Process process, Connection connection)
    {
        Process = process;

        _connection = connection;
    }

    /// <summary>
    /// The process, which the caller owns and must dispose.
    /// </summary>
    public Process Process { get; }

    /// <summary>
    /// The path of the Bare executable that this package provides for the
    /// running platform.
    /// </summary>
    public static string Executable
    {
        get
        {
            var name = OperatingSystem.IsWindows() ? "bare.exe" : "bare";
            var path = Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", name);

            // Published for a runtime, the native assets of an application sit
            // next to it.
            return File.Exists(path) ? path : Path.Combine(AppContext.BaseDirectory, name);
        }
    }

    /// <summary>
    /// The versions of the components of the Bare executable that this package
    /// provides, keyed by name like <c>Bare.versions</c>, such as <c>bare</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Versions { get; } = ReadVersions();

    /// <summary>
    /// Starts a process with an IPC connection to it. The process must run a
    /// Bare executable built with the embedder of this package, such as
    /// <see cref="Executable"/>, with the entry point as its first argument and
    /// the arguments that follow it as the rest of <c>Bare.argv</c>.
    /// </summary>
    /// <remarks>
    /// The connection is passed in the environment of <paramref name="info"/>.
    /// A process that the application starts by other means at the same time
    /// may inherit it too, which keeps the connection open until that process
    /// exits.
    /// </remarks>
    public static Sidecar Start(ProcessStartInfo info)
    {
        Connection connection = OperatingSystem.IsWindows() ? Windows.Create() : Unix.Create();

        try
        {
            lock (s_passing)
            {
                info.Environment[IpcEnv] = connection.Pass();

                try
                {
                    return new Sidecar(Process.Start(info)!, connection);
                }
                finally
                {
                    connection.Remote.Dispose();
                }
            }
        }
        catch
        {
            connection.Dispose();

            throw;
        }
    }

    /// <summary>
    /// Asks the process to exit, with <c>SIGTERM</c> or on Windows by
    /// terminating it, unless it has already exited.
    /// </summary>
    public void Terminate()
    {
        if (OperatingSystem.IsWindows()) Windows.Terminate(Process);
        else Unix.Terminate(Process);
    }

    /// <inheritdoc/>
    public override bool CanRead => _connection.Stream.CanRead;

    /// <inheritdoc/>
    public override bool CanWrite => _connection.Stream.CanWrite;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Flush() => _connection.Stream.Flush();

    /// <summary>
    /// Reads data that the process has written to <c>Bare.IPC</c>, returning 0
    /// once it has ended the connection and exited.
    /// </summary>
    public override int Read(Span<byte> buffer)
    {
        int read;

        try
        {
            if (buffer.Length != 0) _connection.WaitUntilReadable();

            read = _connection.Stream.Read(buffer);
        }
        catch (IOException e) when (_connection.IsReset(e))
        {
            read = 0;
        }

        if (End(read, buffer.Length)) Process.WaitForExit();

        return read;
    }

    /// <inheritdoc cref="Read(Span{byte})"/>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read;

        try
        {
            read = await _connection.Stream.ReadAsync(buffer, cancellationToken);
        }
        catch (IOException e) when (_connection.IsReset(e))
        {
            read = 0;
        }

        if (End(read, buffer.Length)) await Process.WaitForExitAsync(cancellationToken);

        return read;
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);

        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);

        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <summary>
    /// Sends data to <c>Bare.IPC</c> of the process.
    /// </summary>
    public override void Write(ReadOnlySpan<byte> buffer) => _connection.Stream.Write(buffer);

    /// <inheritdoc cref="Write(ReadOnlySpan{byte})"/>
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        return _connection.Stream.WriteAsync(buffer, cancellationToken);
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => _connection.Stream.Write(buffer, offset, count);

    /// <inheritdoc/>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return _connection.Stream.WriteAsync(buffer, offset, count, cancellationToken);
    }

    /// <summary>
    /// Closes the connection.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing) _connection.Dispose();

        base.Dispose(disposing);
    }

    private static IReadOnlyDictionary<string, string> ReadVersions()
    {
        using var manifest = typeof(Sidecar).Assembly.GetManifestResourceStream("versions.json")!;
        using var document = JsonDocument.Parse(manifest);

        return document.RootElement.EnumerateObject().ToDictionary(entry => entry.Name, entry => entry.Value.GetString()!).AsReadOnly();
    }

    // The process exits only once the connection has closed, so the local end
    // is ended before waiting for the exit.
    private bool End(int read, int length)
    {
        if (read != 0 || length == 0) return false;

        _connection.End();

        return true;
    }
}
