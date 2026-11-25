# bare-sidecar-dotnet

Start and manage Bare sidecar processes from .NET, each running Bare alongside the application with an IPC to it exposed as `Bare.IPC` inside the process.

```
dotnet add package Holepunch.BareSidecar
```

## Usage

```csharp
using System.Diagnostics;
using System.Text;
using Holepunch.BareSidecar;

using var sidecar = Sidecar.Start(new ProcessStartInfo(Sidecar.Executable, ["app.js"]));

await sidecar.WriteAsync("Hello world"u8.ToArray());

var data = new byte[11];

await sidecar.ReadExactlyAsync(data);

Console.WriteLine(Encoding.UTF8.GetString(data));
```

Where `app.js` is a Bare entry point using `Bare.IPC`:

```js
Bare.IPC.on('data', (data) => Bare.IPC.write(data))
```

A sidecar is a `Stream`, and the process is an ordinary `Process`, so its standard I/O, environment, and working directory are configured on the `ProcessStartInfo` as usual. Reading returns 0 only once the process has ended `Bare.IPC` and exited, so its exit code is known by then. Disposing the sidecar closes the connection, after which `Bare.IPC` ends and the process may exit on its own.

## Prebuilds

The process must run a Bare executable built with the embedder of this package. The package includes prebuilt executables for Linux, macOS, and Windows on arm64 and x64, and `Sidecar.Executable` is the path of the one for the running platform. To use a build of its own instead, an application passes its path as the file name of the `ProcessStartInfo`.

`Sidecar.Versions` holds the versions of the components of the bundled executable, keyed by name like `Bare.versions`:

```csharp
Console.WriteLine(Sidecar.Versions["bare"]);
```

The versions come from `versions.json` at the root of the repository, which is also where the build of the executable reads the version of Bare from.

## License

Apache-2.0
