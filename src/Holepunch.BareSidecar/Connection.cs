using System.Runtime.InteropServices;

namespace Holepunch.BareSidecar;

// The local end of the connection to a process, and the remote end that is
// handed to it.
internal abstract class Connection(Stream stream, SafeHandle remote) : IDisposable
{
    public Stream Stream { get; } = stream;

    public SafeHandle Remote { get; } = remote;

    // Makes the remote end inheritable and returns the name that it is passed
    // to the process as.
    public abstract string Pass();

    public virtual bool IsReset(IOException e) => false;

    public virtual void WaitUntilReadable()
    {
    }

    // Ends the local end once the process has ended its own.
    public abstract void End();

    public void Dispose()
    {
        Stream.Dispose();
        Remote.Dispose();
    }
}
