# ngc-host

Hosts the SLua runtime (the parent tree, unmodified) from .NET through a small shared
library, `slua_shim` (`slua_shim.dll` on Windows x64, `libslua_shim.so` on Linux x64).

- `shim/slua_shim.h`: the flat C interface, documented below. Every export catches all C++
  exceptions and returns a negative code; the managed side never holds a `lua_State`.
- `shim/slua_shim.cpp`: the implementation over the Executor (`Provisioner`, `Script`).
- `CMakeLists.txt`, `inject_shim.cmake`: configure the SLua tree as its own top-level project
  and add the shim target to it. Two compile jobs at most.
- `dotnet/SluaHost`: managed wrapper (P/Invoke). `dotnet/SluaHost.Tool`: a small console
  used by the cross-process restore test. `dotnet/SluaHost.Tests`: the tests.
- `scripts/build-win.cmd`, `scripts/build-linux.sh`, `docker/Dockerfile`: build helpers.
- `THIRD-PARTY-NOTICES.txt`, `licenses/`: licences of everything linked into the library.
- `../.github/workflows/ngc-host.yml`: builds and tests on Windows and Ubuntu 22.04 for every
  push to a branch named `ngc-host/...`, and uploads the library with the notices.

## Build and test

Windows (Visual Studio 2022 C++ tools; uses their CMake and Ninja):

    build-win.cmd <repo root> <build dir>
    set SLUA_SHIM_PATH=<build dir>\slua
    dotnet test dotnet\SluaHost.Tests\SluaHost.Tests.csproj -c Release

Linux (cmake, g++, .NET SDK 10):

    sh scripts/build-linux.sh <repo root> <build dir>
    SLUA_SHIM_PATH=<build dir>/slua dotnet test dotnet/SluaHost.Tests/SluaHost.Tests.csproj -c Release

`SLUA_SHIM_PATH` is the folder holding the shared library. `SLUA_R1_OUT`, when set, is a
folder where the measurement tests append their results.

## API version

`slua_api_version()` returns `SLUA_API_VERSION` (currently 2). Any change to an export's
signature, to the meaning of a result code, or to the layout of `slua_value` or
`slua_step_result` bumps it. A host checks it before calling anything else.
`slua_struct_size` reports the library's `sizeof` for those two structs so a host can check its
own layout. `slua_version` returns a text form: `runtime=<SLua commit> api=<n>`.

## Threading rule

One VM, one thread at a time. A `slua_vm`, its images and all its scripts are used by one thread
at a time; different VMs can run on different threads at once. The library does not take locks
on behalf of the caller. A second concurrent caller in the same VM is refused with
`SLUA_ERR_BUSY` and counted in `slua_busy_violations()`; the first caller is unaffected. Host
functions run on the calling thread, inside the script's step, and must not call back into the
same VM.

## Error model

- Every export returns an `int32_t`. Zero is success, negative is an error, positive is a
  non-error outcome the caller looks at (`SLUA_RESTORED_FRESH`).
- No C++ exception leaves an export, and none reaches managed code. A host function that
  throws becomes a script error carrying its message; an uncaught script error faults that
  script only (see `slua_fault`); an out-of-memory or watchdog kill faults the script and leaves
  the VM and the other scripts intact.
- Strings returned by the library (`slua_compile` output and error text, `slua_serialize`
  output) are allocated by it and released with `slua_free`.
- Pointers inside a `slua_value` (string text, list items) are valid only for the call they
  arrive in. Copy what you keep.

| Code | Value | Meaning |
|---|---|---|
| `SLUA_OK` | 0 | Success. |
| `SLUA_RESTORED_FRESH` | 1 | `slua_restore` refused the saved state and started the script fresh. |
| `SLUA_ERR_NOT_IMPLEMENTED` | -1 | Not used by released builds. |
| `SLUA_ERR_EXCEPTION` | -2 | An internal exception was caught at the boundary. |
| `SLUA_ERR_BAD_ARG` | -3 | A null, out-of-range or malformed argument. |
| `SLUA_ERR_BUSY` | -4 | Another thread is inside this VM. |
| `SLUA_ERR_COMPILE` | -5 | Source did not compile; the error text names line and reason. |
| `SLUA_ERR_LOAD` | -6 | The asset is not valid bytecode for this runtime. |
| `SLUA_ERR_RESTORE` | -7 | The saved state is not ours at all (bad magic, truncated). |
| `SLUA_ERR_STATE` | -8 | The call is not valid in the script's current state. |
| `SLUA_ERR_LIMIT` | -9 | A memory limit above the ceiling, or below what the script uses. |
| `SLUA_ERR_UNSUPPORTED_FUNCTION` | -10 | LSL source calls an `os*`, `iw*` or `bot*` function. |

## Values

`slua_value` is 56 bytes on 64-bit targets: `type`, `len`, `i`, `n`, `s`, `items`, `v[4]`.

| Type | Carried in | Notes |
|---|---|---|
| `SLUA_T_NIL` | | |
| `SLUA_T_BOOL` | `i` (0 or 1) | |
| `SLUA_T_INT` | `i` | LSL integers are 32-bit. In SLua a returned integer reaches the script as a number. |
| `SLUA_T_NUMBER` | `n` | |
| `SLUA_T_STRING` | `s`, `len` | UTF-8, not necessarily zero-terminated. |
| `SLUA_T_VECTOR` | `v[0..2]` | |
| `SLUA_T_KEY` | `s`, `len` | UUID text. In LSL any string is a valid key. In SLua it arrives as a `uuid`. |
| `SLUA_T_ROTATION` | `v[0..3]` | x, y, z, s. A `quaternion` in the script. |
| `SLUA_T_LIST` | `items`, `len` | Up to `SLUA_MAX_LIST_ITEMS` (4096) elements. A table in the script. A list may not hold a list. |

## Exports

Lifecycle and information:

- `slua_version(buf, cap)`: writes the text version.
- `slua_api_version()`: the API version. `slua_struct_size(which)`: `SLUA_STRUCT_VALUE` or
  `SLUA_STRUCT_STEP_RESULT`.
- `slua_set_log(fn)`: one process-wide log callback (`level` 0 debug to 3 error).
- `slua_init()`: one-time setup; every export that needs it calls it.
- `slua_free(p)`: releases memory the library returned.
- `slua_busy_violations()`: the process-wide count of refused concurrent calls.

Compiling:

- `slua_compile(src, len, is_lsl, &out, &out_len, &err)`: source to an asset (header plus
  bytecode). `SLUA_ERR_COMPILE` puts the message in `*err`. For LSL, a call to an undeclared
  `os*`, `iw*` or `bot*` function returns `SLUA_ERR_UNSUPPORTED_FUNCTION` and `*err` names every
  such function and says which engine runs those scripts. The engine name and its header token
  are constants at the top of `slua_shim.cpp`.

VMs and host functions:

- `slua_vm_new(watchdog_mode, &vm)`: `SLUA_WATCHDOG_DEFAULT`, `_RESIDENT` or `_THREADED`.
- `slua_vm_free(vm)`: `SLUA_ERR_STATE` while any script or image of the VM is alive.
- `slua_register_host_fn(vm, table, name, id, cb, user)`: binds `table.name` in the scripts'
  environment. All host functions are registered before the first image is made
  (`SLUA_ERR_STATE` after). `cb` receives the arguments as `slua_value` records, writes up to
  8 return values and returns how many; `SLUA_RET_YIELD` yields the script with no values; any
  other negative value raises a script error whose text is `rets[0]` (a string).
- `slua_watchdog_stats(vm, &fires, &late_fires)`.

Images and scripts:

- `slua_image_new(vm, asset, len, &img)`: loads an asset once. `slua_image_free(img)`.
- `slua_script_new_from_image(img, memory_limit, tag, &script)`: any number of scripts per
  image; they share the loaded code and keep the image alive after `slua_image_free`.
  `slua_script_new(vm, asset, len, memory_limit, tag, &script)` is the one-script form.
  `tag` is passed to every host function call so the host knows which script is calling.
- `slua_script_free(script)`, `slua_script_start(script)`: loads the default state and stages the
  main function (SLua) or the first state (LSL).
- `slua_step(script, quanta_seconds, &result)`: one scheduling step. Serves sleep, then resumes
  the handler in flight or runs the pending `state_exit` / `state_entry`. `result.status` is
  `SLUA_STEP_OK`, `_PREEMPTED` (resume with another step), `_STATE_CHANGE`, `_FAULT`,
  `_NOT_RUN`, `_REFUSED` or `_IDLE` (nothing left to run). `sleep_served` is the sleep the
  script asked for, in seconds, which a scheduler should honour before the next step.
- `slua_event_index(name)`: the event number for an LSL event name.
- `slua_dispatch_event(script, event, args, nargs, quanta_seconds, &result)`: starts an event
  handler with up to 32 arguments and runs its first step. Valid only when no handler is in
  flight and no state change is waiting, otherwise `SLUA_ERR_STATE`; step until `SLUA_STEP_IDLE`
  first. `SLUA_STEP_NOT_RUN` means the script has no handler for the event. A later step of the
  same handler goes through `slua_step`. SLua scripts register handlers with `LLEvents:on`, and
  the library keeps the handler set current.
- `slua_timer_interval(script, &seconds)`: the interval an SLua script last asked for through
  `LLTimers`, 0 when it has none. The host dispatches a `timer` event at that time.
- `slua_kill(script)`: aborts a handler in flight, or faults an idle script.
- `slua_fault(script, &kind, buf, cap)`: the fault kind (`SLUA_FAULT_*`) and its text.

Saved state:

- `slua_serialize(script, &out, &out_len)` and `slua_restore(script, data, len)`. See below.

Memory:

- `slua_used_memory`, `slua_get_memory_limit`, `slua_set_memory_limit`. See below.

## Saved-state header and the fresh-start rule

`slua_serialize` writes `"SLSH"`, a `u32` format version, a `u16` length and the text of the
SLua runtime commit the library was built from, a `u64` hash of the bound host-function list
(sorted `table.name` entries, so registration order does not matter), then the Executor's
payload. `slua_restore` reads it into a script that has not been started:

- Not our format (bad magic, truncated header, payload that cannot be read): an error
  (`SLUA_ERR_RESTORE`), and nothing is changed.
- A different format version, a different runtime commit or a different bound function list: the
  state is refused and the script starts fresh, returning `SLUA_RESTORED_FRESH`. The reason is
  logged at warning level. The saved state is never half-applied.
- A state that matches but fails inside the Executor's restore: the script starts fresh and
  `SLUA_RESTORED_FRESH` is returned.

A host that stores saved state keeps the original asset or source beside it, because a fresh
start needs it.

## Memory rule

Every script has a limit, 128 KiB by default (`SLUA_MEMORY_LIMIT_MAX`) for SLua and for LSL on
Luau. A host may lower a limit with `slua_set_memory_limit`; asking for more than the ceiling,
or for less than the script already uses, returns `SLUA_ERR_LIMIT` and leaves the limit as it
was. `slua_script_new` and `slua_script_new_from_image` refuse a limit above the ceiling the same
way (0 means the default). The Executor's unchecked setter is not exposed.

## Pausing calls

A host function that has to wait for something (a dataserver reply, a region call) does not block
the VM and does not yield from inside the host call. The script calls a Lua-side wrapper that
starts the work with one host call, calls `coroutine.yield()`, and after the host resumes it with
another step, fetches the result with a second host call:

    local function await_thing(x)
        host.StartThing(x)       -- host function: starts the work, records it under this script's tag
        coroutine.yield()        -- script is Preempted; the host steps it again when the work is done
        return host.TakeResult() -- host function: returns the stored result
    end

The resume cannot carry a value, because the Executor always resumes with none, so the value
travels in the second host call. Saving state while the script waits in `coroutine.yield` is
supported and tested. A host function that returns `SLUA_RET_YIELD` also pauses the script, but
saving state during that kind of wait is not covered by a test, so it is not used.
