using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace Holepunch.BareSidecar;

internal sealed partial class Windows : Connection
{
    private const uint HANDLE_FLAG_INHERIT = 0x1;

    private readonly NamedPipeServerStream _server;

    private Windows(NamedPipeServerStream server, SafeHandle remote) : base(server, remote)
    {
        _server = server;
    }

    // A single instance that this process connects to before the child exists,
    // so that no other process can connect in its place.
    public static Windows Create()
    {
        var name = $"bare-sidecar-{Guid.NewGuid()}";

        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance);

        NamedPipeClientStream? client = null;

        try
        {
            client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);

            client.Connect();
            server.WaitForConnection();

            return new Windows(server, client.SafePipeHandle);
        }
        catch
        {
            client?.Dispose();
            server.Dispose();

            throw;
        }
    }

    // The runtime lets a process inherit every inheritable handle, so the
    // remote end only becomes inheritable once it is passed.
    public override string Pass()
    {
        if (!SetHandleInformation(Remote, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return Remote.DangerousGetHandle().ToString();
    }

    // Named pipes cannot be half closed, so the process only sees the
    // connection end once it is disconnected.
    public override void End()
    {
        try
        {
            _server.Disconnect();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(SafeHandle handle, uint mask, uint flags);
}
