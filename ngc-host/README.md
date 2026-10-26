# ngc-host

This repository is Linden Lab's SLua runtime (the parent tree, pinned to one commit) plus this folder,
`ngc-host/`, and the workflow that builds and releases it. The long-lived branch is `ngc`. `ngc-host/`
turns the runtime into a small shared library, `slua_shim` (`slua_shim.dll` on Windows x64,
`libslua_shim.so` on Linux x64), that a host process loads through a flat C interface.

- `shim/slua_shim.h`: the C interface, documented below. Every export catches all C++ exceptions and
  returns a negative code; the host never holds a `lua_State`.
- `shim/slua_shim.cpp`: the implementation over the Executor (`Provisioner`, `Script`).
- `CMakeLists.txt`, `inject_shim.cmake`, `stamp.cmake`: configure the SLua tree as its own top-level
  project and add the shim target to it. Two compile jobs at most.
- `LL-RUNTIME-PIN`: the commit of the parent tree the library is built against.
- `dotnet/SluaHost`, `dotnet/SluaHost.TestKit`, `dotnet/SluaHost.Tool`, `dotnet/SluaHost.Tests`:
  C# bindings and tests, used to test the library. They are test-only code and are never shipped.
- `scripts/`: `build-win.cmd`, `build-linux.sh` (builds); `make-bundle.py` (the release bundle);
  `pack.sh` (the native-only package, from a bundle).
- `package/UtopiaSkye.SLua.csproj`: the native-only NuGet package project.
- `THIRD-PARTY-NOTICES.txt`, `licenses/`: licences of everything linked into the library.
- `../.github/workflows/ngc-host.yml`: the CI and release workflow.

## Build and test

Windows (Visual Studio 2022 C++ tools; uses their CMake and Ninja):

    build-win.cmd <repo root> <build dir>
    set SLUA_SHIM_PATH=<build dir>\slua
    dotnet test dotnet\SluaHost.Tests\SluaHost.Tests.csproj -c Release

Linux (cmake, g++, .NET SDK 10, python3):

    sh scripts/build-linux.sh <repo root> <build dir>
    SLUA_SHIM_PATH=<build dir>/slua dotnet test dotnet/SluaHost.Tests/SluaHost.Tests.csproj -c Release

`SLUA_SHIM_PATH` is the folder holding the shared library. `SLUA_R1_OUT`, when set, is a
folder where the measurement tests append their results.

## Syncing with Linden Lab's runtime

The parent tree must match the commit in `ngc-host/LL-RUNTIME-PIN`; configure stops otherwise (see
"The build stamp"). To take a newer runtime, merge Linden Lab's tree into `ngc` and, in the same
change, set `LL-RUNTIME-PIN` to the new commit. That change is made on its own, as a pin-bump, not
mixed with other work. It changes the runtime part of the build stamp, so scripts a host has saved
start fresh once. The pin lives only in `LL-RUNTIME-PIN`.

## Releases

Releases come only from CI. Pushing a tag `vX.Y.Z` on `ngc` makes the workflow build and test both
libraries and publish a GitHub release; every file in the release comes from that run's builds:

- `slua-native-win-x64-X.Y.Z.zip` (`slua_shim.dll`) and `slua-native-linux-x64-X.Y.Z.tar.gz`
  (`libslua_shim.so`, built on Ubuntu 22.04 so it loads on newer distributions);
- `slua_shim.h`, `THIRD-PARTY-NOTICES.txt` and the licence texts;
- `RELEASE-NOTES.md`: the runtime pin, the API version, the stamp format and the build's commit;
- `SHA256SUMS.txt`: the SHA-256 of every other file (licence texts are named `licenses/<file>`).

A tag that is not on `ngc` is refused. Running the workflow by hand with `dry_run` builds the same
bundle and keeps it as an artifact (`slua-release-bundle`), and publishes nothing.

The release carries native files only, no managed code. The NuGet package `UtopiaSkye.SLua` is
packaged from a release by the NGC maintainer, Mike. `scripts/pack.sh <repo root> <bundle> <out>`
shows how: it checks the bundle against `SHA256SUMS.txt`, then packs `package/UtopiaSkye.SLua.csproj`
(`runtimes/win-x64/native`, `runtimes/linux-x64/native`, the notices). The C# binding that calls the
library belongs to NGC itself; the `SluaHost` code here is for testing only.

## API version

`slua_api_version()` returns `SLUA_API_VERSION` (currently 3). Changing or removing an export (its
signature, the meaning of a result code, the layout of `slua_value` or `slua_step_result`) bumps it;
adding an export does not. A host requires an exact match and checks it before calling anything
else. `slua_struct_size` reports the library's `sizeof` for those two structs so a host can check its
own layout. `slua_version` returns a text form: `runtime=<pinned SLua commit> api=<n>` (see the build
stamp below). The header lists the exports each version added.

Version 3 added `slua_add_sleep` (a host function asks for a delay), `slua_register_pausing_fn` (a
pausing function the library binds) and `slua_debug_alloc_stats` (what the library holds now). It is
a version bump because a host built for version 2 cannot make pausing calls that survive a save, and
a saved state records the bound-function list, which now distinguishes a pausing function from a
plain one of the same name.

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
- `slua_debug_alloc_stats(out, n)`: fills up to 6 counters: live VMs, images and scripts, blocks handed to
  the host and not yet released with `slua_free`, their bytes, and the bytes in use on the C heap.

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
- `slua_register_pausing_fn(vm, table, name, start_id, take_id, cb, user)`: binds `table.name` as
  a pausing function (see "Pausing calls"). Same rule as `slua_register_host_fn`: before the first
  image is made, `SLUA_ERR_STATE` after.
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
- `slua_add_sleep(script, seconds)`: adds a delay to the script. The next step reports it in
  `sleep_served`, and the scheduler waits that long before stepping the script again. Valid only
  inside a host function that this script called (`SLUA_ERR_STATE` anywhere else, including from
  another script's host function), on the thread running that function. `seconds` must be finite
  and above zero (`SLUA_ERR_BAD_ARG`). Calls add up. The delay is part of the saved state.
- `slua_kill(script)`: aborts a handler in flight, or faults an idle script.
- `slua_fault(script, &kind, buf, cap)`: the fault kind (`SLUA_FAULT_*`) and its text.

Saved state:

- `slua_serialize(script, &out, &out_len)` and `slua_restore(script, data, len)`. See below.

Memory:

- `slua_used_memory`, `slua_get_memory_limit`, `slua_set_memory_limit`. See below.

## Saved-state header and the fresh-start rule

`slua_serialize` writes `"SLSH"`, a `u32` format version, a `u16` length and the text of the
build stamp, a `u64` hash of the bound host-function list (sorted `table.name` entries with a
mark for each pausing function, so registration order does not matter but a plain function and a
pausing function of the same name differ), then the Executor's payload. `slua_restore` reads it
into a script that has not been started:

- Not our format (bad magic, truncated header, payload that cannot be read): an error
  (`SLUA_ERR_RESTORE`), and nothing is changed.
- A different format version, a different build stamp or a different bound function list: the
  state is refused and the script starts fresh, returning `SLUA_RESTORED_FRESH`. The reason is
  logged at warning level. The saved state is never half-applied.
- A state that matches but fails inside the Executor's restore: the script starts fresh and
  `SLUA_RESTORED_FRESH` is returned.

### The build stamp

The stamp text is `runtime=<runtime> api=<n>`, the same text `slua_version` returns. It has no
part that depends on this repository's own commits, so committing to `ngc-host/` never makes a
host's stored scripts start fresh.

- `<runtime>` is the commit in `ngc-host/LL-RUNTIME-PIN`: the commit of the parent SLua tree the
  library is built against. At configure time `ngc-host/stamp.cmake` compares the parent tree's
  files (everything except `ngc-host/` and `.github/workflows/ngc-host.yml`) with the files at
  that commit. If they differ, or the commit is not in the clone, configure fails. A build from
  CI needs the full history for this check.
- `cmake ... -DSLUA_ALLOW_UNPINNED_TREE=ON` lets a build go ahead with a parent tree that differs
  from the pin. The runtime part is then `tree-<10 hex>`, a hash of the parent tree's file list
  (path and blob id of every file), so two builds of the same parent files agree.
- `<n>` is the C API version.
- The bound-function hash is the separate `u64` in the header.

A part that does not match, in any of the three, starts the script fresh. More parts can be
appended to the stamp text later without changing the header layout.

A host that stores saved state keeps the original asset or source beside it, because a fresh
start needs it.

## Memory rule

Every script has a limit, 128 KiB by default (`SLUA_MEMORY_LIMIT_MAX`) for SLua and for LSL on
Luau. A host may lower a limit with `slua_set_memory_limit`; asking for more than the ceiling,
or for less than the script already uses, returns `SLUA_ERR_LIMIT` and leaves the limit as it
was. `slua_script_new` and `slua_script_new_from_image` refuse a limit above the ceiling the same
way (0 means the default). The Executor's unchecked setter is not exposed.

## Delays

Many `ll` functions make the calling script wait after they return (the call delays of the Second
Life wiki). A host function does that with `slua_add_sleep` from inside the call, rather than by
sleeping itself. The step that follows reports the total in `sleep_served`; the scheduler honours it
(the engine's own punishment for an overrunning script travels the same way). A script that asked for
a delay is paused by the runtime at its next safe point. A delay that has been asked for and not
yet served is saved with the script and reported again by the first step after a restore.

## Pausing calls

A host function that has to wait for something (a dataserver reply, a region call) must not block
the VM. Register it with `slua_register_pausing_fn(vm, table, name, start_id, take_id, cb, user)`:

1. The script calls `table.name(...)`. The library calls `cb` with `start_id` and the call's
   arguments. The host starts the work and records it under the script's tag. Return values are
   ignored.
2. The library yields the script; the step returns `SLUA_STEP_PREEMPTED`. The host does not step the
   script until the work is done.
3. The host steps the script again. The library calls `cb` with `take_id` and no arguments; the
   values it returns (up to 8) are the values of `table.name(...)`. A negative return raises a
   script error, which the script can catch with `pcall`.

It is bound in the environment for SLua and for LSL scripts alike, so LSL compiled by the runtime's
compiler calls it as it calls any other `ll` function. The step cannot carry a value into the script,
which is why the answer travels in the second host call.

Saving state while a script waits inside a pausing call is supported and tested for SLua and for LSL.
The pending call itself is not part of the saved state: after a restore the first step calls `take_id`
as usual, and it is the host's job to have an answer or to return its failure value.

The library does the yielding natively. A Lua function made in the environment (a wrapper written in
Lua and installed before the script runs) cannot take this job: the saved-state writer accepts only
code that belongs to the script's own image, and refuses to save a script that is suspended inside any
other Lua function.

A Lua-side wrapper that a script itself contains still works and still saves, as before:

    local function await_thing(x)
        host.StartThing(x)       -- host function: starts the work, records it under this script's tag
        coroutine.yield()        -- script is Preempted; the host steps it again when the work is done
        return host.TakeResult() -- host function: returns the stored result
    end

A plain host function that returns `SLUA_RET_YIELD` also pauses the script. It saves and restores as
well (a script waiting in one was saved and restored in a fresh VM in a test of this version), but it
leaves the take call to the script, so a pausing function is the form to use.
