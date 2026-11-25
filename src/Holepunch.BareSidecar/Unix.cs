using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Holepunch.BareSidecar;

internal sealed unsafe partial class Unix : Connection
{
    private const int AF_UNIX = 1;
    private const int SOCK_STREAM = 1;
    private const int SOCK_CLOEXEC = 0x80000;
    private const int F_SETFD = 2;
    private const int FD_CLOEXEC = 1;
    private const int SIGTERM = 15;
    private const int ESRCH = 3;

    private readonly Socket _socket;

    static Unix()
    {
        NativeLibrary.SetDllImportResolver(typeof(Unix).Assembly, Resolve);
    }

    private Unix(Socket socket, SafeHandle remote) : base(new NetworkStream(socket, ownsSocket: true), remote)
    {
        _socket = socket;
    }

    // Resolved through the process, as the C library has no file name common to
    // every platform.
    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        return name == "libc" ? NativeLibrary.GetMainProgramHandle() : 0;
    }

    // Both ends are close-on-exec until the remote end is passed, so that no
    // other process inherits them. On Linux, they are created as such.
    public static Unix Create()
    {
        var fds = stackalloc int[2];

        if (socketpair(AF_UNIX, OperatingSystem.IsLinux() ? SOCK_STREAM | SOCK_CLOEXEC : SOCK_STREAM, 0, fds) != 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        fcntl(fds[0], F_SETFD, FD_CLOEXEC);
        fcntl(fds[1], F_SETFD, FD_CLOEXEC);

        return new Unix(new Socket(new SafeSocketHandle(fds[0], ownsHandle: true)), new SafeFileHandle(fds[1], ownsHandle: true));
    }

    public override string Pass()
    {
        var fd = (int)Remote.DangerousGetHandle();

        fcntl(fd, F_SETFD, 0);

        return fd.ToString();
    }

    // On Linux, a process that exits with data still unread resets the
    // connection rather than ending it.
    public override bool IsReset(IOException e)
    {
        return e.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset };
    }

    // On macOS, a blocking receive may miss the end of the connection, so it
    // only runs once data or the end is pending.
    public override void WaitUntilReadable() => _socket.Poll(-1, SelectMode.SelectRead);

    // The process exits once the connection has closed, which takes both ends
    // to have ended.
    public override void End()
    {
        try
        {
            _socket.Shutdown(SocketShutdown.Send);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
        }
    }

    // Checked first, as the runtime reaps exited processes, after which their
    // ID may be reused.
    public static void Terminate(Process process)
    {
        if (process.HasExited || kill(process.Id, SIGTERM) == 0) return;

        var error = Marshal.GetLastPInvokeError();

        if (error != ESRCH) throw new Win32Exception(error);
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int socketpair(int domain, int type, int protocol, int* fds);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int fcntl(int fd, int cmd, int arg);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int kill(int pid, int signal);
}
