# ngc-host

Hosts the SLua runtime (the parent tree, unmodified) from .NET through a small shared
library, `slua_shim` (`slua_shim.dll` on Windows, `libslua_shim.so` on Linux).

- `shim/slua_shim.h`: the flat C interface. Every export catches all C++ exceptions and
  returns a negative code; the managed side never holds a `lua_State`.
- `shim/slua_shim.cpp`: the implementation over the Executor (`Provisioner`, `Script`).
- `CMakeLists.txt`, `inject_shim.cmake`: configure the SLua tree as its own top-level project
  and add the shim target to it. Two compile jobs at most.
- `dotnet/SluaHost`: managed wrapper (P/Invoke). `dotnet/SluaHost.Tool`: a small console
  used by the cross-process restore test. `dotnet/SluaHost.Tests`: the tests.
- `scripts/build-win.cmd`, `scripts/build-linux.sh`, `docker/Dockerfile`: build helpers.
- `THIRD-PARTY-NOTICES.txt`, `licenses/`: licences of everything linked into the library.

Build and test (Windows, from a Visual Studio 2022 developer prompt or `build-win.cmd`):

    build-win.cmd <repo root> <build dir>
    set SLUA_SHIM_PATH=<build dir>\slua
    dotnet test dotnet\SluaHost.Tests\SluaHost.Tests.csproj -c Release

`SLUA_SHIM_PATH` is the folder holding the shared library. `SLUA_R1_OUT`, when set, is a
folder where the measurement tests append their results.

Saved script state is wrapped in a header holding the runtime commit and a hash of the bound
host-function list. A state saved under a different commit or function list is refused at
load and the script starts fresh (`slua_restore` returns `SLUA_RESTORED_FRESH`).
