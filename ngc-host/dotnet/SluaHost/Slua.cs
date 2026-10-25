// Copyright (c) 2026 Legion Builds
// Licensed under the MIT License; see LICENSE.txt in the repository root.
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace SluaHost;

public enum ValueType { Nil = 0, Bool = 1, Int = 2, Number = 3, String = 4, Vector = 5 }

/// <summary>A scripting value as it crosses the native boundary.</summary>
public readonly record struct Value(ValueType Type, long I = 0, double N = 0, string? S = null, float X = 0, float Y = 0, float Z = 0)
{
    public static Value Nil => default;
    public static Value Bool(bool b) => new(ValueType.Bool, b ? 1 : 0);
    public static Value Int(long i) => new(ValueType.Int, i);
    public static Value Num(double n) => new(ValueType.Number, 0, n);
    public static Value Str(string s) => new(ValueType.String, 0, 0, s);
    public static Value Vec(float x, float y, float z) => new(ValueType.Vector, 0, 0, null, x, y, z);

    public override string ToString() => Type switch
    {
        ValueType.Nil => "nil",
        ValueType.Bool => I != 0 ? "true" : "false",
        ValueType.Int => $"int:{I}",
        ValueType.Number => $"num:{N.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
        ValueType.String => $"str:{S}",
        ValueType.Vector => $"vec:<{X},{Y},{Z}>",
        _ => "?",
    };
}

public enum StepStatus { Ok = 0, Preempted = 1, StateChange = 2, Fault = 3, NotRun = 4, Refused = 5, Idle = 100 }
public enum FaultKind { None = 0, Runtime = 1, OutOfMemory = 2, Timeout = 3, UnexpectedYield = 4, Unknown = 255 }
public enum Watchdog { Default = 0, Resident = 1, Threaded = 2 }

public readonly record struct StepResult(StepStatus Status, int NewState, float SleepServed, FaultKind Fault);

public class SluaException : Exception
{
    public int Code { get; }
    public SluaException(string what, int code, string? detail = null)
        : base($"{what} failed with code {code}" + (detail != null ? $": {detail}" : "")) { Code = code; }
}

/// <summary>Host function. Return null or an empty array for no values; return <see cref="Host.Yield"/> to yield the script.</summary>
public delegate Value[]? HostFn(long scriptTag, ReadOnlySpan<Value> args);

public static unsafe class Host
{
    public const int RetYield = -1000;
    /// <summary>Return this from a HostFn to yield the script with no values.</summary>
    public static readonly Value[] Yield = new Value[0];

    internal const int Ok = 0;
    internal const int RestoredFresh = 1;

    static Host()
    {
        NativeLibrary.SetDllImportResolver(typeof(Host).Assembly, Resolve);
    }

    static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? path)
    {
        if (name != "slua_shim") return IntPtr.Zero;
        string? dir = Environment.GetEnvironmentVariable("SLUA_SHIM_PATH");
        string file = OperatingSystem.IsWindows() ? "slua_shim.dll" : "libslua_shim.so";
        if (dir != null && NativeLibrary.TryLoad(Path.Combine(dir, file), out var h)) return h;
        return IntPtr.Zero;
    }

    internal static void Check(string what, int code)
    {
        if (code < 0) throw new SluaException(what, code);
    }

    static bool _init;
    static readonly object _initLock = new();

    public static void Init()
    {
        lock (_initLock)
        {
            if (_init) return;
            Check("slua_init", Native.slua_init());
            _init = true;
        }
    }

    public static string Version()
    {
        byte* buf = stackalloc byte[256];
        Check("slua_version", Native.slua_version(buf, 256));
        return Marshal.PtrToStringUTF8((IntPtr)buf)!;
    }

    public static int BusyViolations() => Native.slua_busy_violations();

    /// <summary>Compiles source to an asset. Throws SluaException (code -5) with the compiler message on failure.</summary>
    public static byte[] Compile(string source, bool lsl)
    {
        Init();
        byte[] src = Encoding.UTF8.GetBytes(source);
        byte* outp = null; nuint outLen = 0; byte* err = null;
        int rc;
        fixed (byte* sp = src)
            rc = Native.slua_compile(sp, (nuint)src.Length, lsl ? 1 : 0, &outp, &outLen, &err);
        try
        {
            if (rc != 0)
                throw new SluaException("slua_compile", rc, err != null ? Marshal.PtrToStringUTF8((IntPtr)err) : null);
            return new ReadOnlySpan<byte>(outp, (int)outLen).ToArray();
        }
        finally
        {
            if (outp != null) Native.slua_free(outp);
            if (err != null) Native.slua_free(err);
        }
    }
}

public sealed unsafe class Vm : IDisposable
{
    internal IntPtr Handle;
    readonly GCHandle _self;
    readonly List<HostFn> _fns = new();

    public Vm(Watchdog watchdog = Watchdog.Default)
    {
        Host.Init();
        IntPtr h;
        Host.Check("slua_vm_new", Native.slua_vm_new((int)watchdog, &h));
        Handle = h;
        _self = GCHandle.Alloc(this);
    }

    public void Register(string table, string name, HostFn fn)
    {
        int id = _fns.Count;
        _fns.Add(fn);
        Host.Check($"slua_register_host_fn({table}.{name})",
            Native.slua_register_host_fn(Handle, table, name, id, &HostEntry.Call, (void*)GCHandle.ToIntPtr(_self)));
    }

    public Script NewScript(byte[] asset, int memoryLimit = 128 * 1024, long tag = 0)
    {
        IntPtr s;
        int rc;
        fixed (byte* ap = asset)
            rc = Native.slua_script_new(Handle, ap, (nuint)asset.Length, memoryLimit, tag, &s);
        Host.Check("slua_script_new", rc);
        return new Script(this, s);
    }

    public (ulong Fires, ulong LateFires) WatchdogStats()
    {
        ulong f = 0, l = 0;
        Host.Check("slua_watchdog_stats", Native.slua_watchdog_stats(Handle, &f, &l));
        return (f, l);
    }

    internal HostFn Fn(int id) => _fns[id];

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            Native.slua_vm_free(Handle);
            Handle = IntPtr.Zero;
            _self.Free();
        }
    }
}

/// <summary>Entry point native code calls for every host function call. Nothing may throw out of it.</summary>
static unsafe class HostEntry
{
    // Strings handed back to native are copied by the shim before it returns,
    // so one reusable buffer per thread is enough.
    [ThreadStatic] static IntPtr _retBuf;
    [ThreadStatic] static int _retCap;

    static byte* Stage(string s, out int len)
    {
        byte[] b = Encoding.UTF8.GetBytes(s);
        len = b.Length;
        if (_retCap < b.Length + 1)
        {
            if (_retBuf != IntPtr.Zero) NativeMemory.Free((void*)_retBuf);
            _retCap = Math.Max(b.Length + 1, 256);
            _retBuf = (IntPtr)NativeMemory.Alloc((nuint)_retCap);
        }
        b.CopyTo(new Span<byte>((void*)_retBuf, b.Length));
        ((byte*)_retBuf)[b.Length] = 0;
        return (byte*)_retBuf;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static int Call(void* user, int fnId, long tag, Native.SluaValue* args, int nargs, Native.SluaValue* rets, int maxRets)
    {
        try
        {
            var vm = (Vm)GCHandle.FromIntPtr((IntPtr)user).Target!;
            Value[] a = new Value[nargs];
            for (int k = 0; k < nargs; k++) a[k] = Native.ToManaged(args[k]);
            Value[]? r = vm.Fn(fnId)(tag, a);
            if (ReferenceEquals(r, Host.Yield)) return Host.RetYield;
            if (r == null) return 0;
            // Only the first value can carry a string through the shared buffer.
            int n = Math.Min(r.Length, maxRets);
            bool usedBuf = false;
            for (int k = 0; k < n; k++)
            {
                if (r[k].Type == ValueType.String && usedBuf)
                    throw new InvalidOperationException("at most one string return value is supported by the spike");
                usedBuf |= r[k].Type == ValueType.String;
                Native.FromManaged(r[k], &rets[k], Stage);
            }
            return n;
        }
        catch (Exception e)
        {
            try
            {
                var m = new Value(ValueType.String, 0, 0, "host function failed: " + e.GetType().Name + ": " + e.Message);
                Native.FromManaged(m, &rets[0], Stage);
            }
            catch { }
            return -1;
        }
    }
}

public sealed unsafe class Script : IDisposable
{
    readonly Vm _vm;
    IntPtr _h;
    internal Script(Vm vm, IntPtr h) { _vm = vm; _h = h; }

    public void Start() => Host.Check("slua_script_start", Native.slua_script_start(_h));

    public StepResult Step(double quantaSeconds = 0.01)
    {
        Native.StepResultN r;
        Host.Check("slua_step", Native.slua_step(_h, quantaSeconds, &r));
        return new((StepStatus)r.Status, r.NewState, r.SleepServed, (FaultKind)r.FaultKind);
    }

    /// <summary>Steps until the script has nothing left to run, a fault, or maxSteps steps. Returns the last status that was not Idle.</summary>
    public StepResult RunToIdle(double quantaSeconds = 0.01, int maxSteps = 100000)
    {
        StepResult last = default;
        for (int i = 0; i < maxSteps; i++)
        {
            var r = Step(quantaSeconds);
            if (r.Status == StepStatus.Idle) return last;
            last = r;
            if (r.Status == StepStatus.Fault || r.Status == StepStatus.Refused || r.Status == StepStatus.NotRun) return r;
        }
        return last;
    }

    public void Kill() => Host.Check("slua_kill", Native.slua_kill(_h));

    public byte[] Serialize()
    {
        byte* p = null; nuint len = 0;
        Host.Check("slua_serialize", Native.slua_serialize(_h, &p, &len));
        try { return new ReadOnlySpan<byte>(p, (int)len).ToArray(); }
        finally { Native.slua_free(p); }
    }

    /// <summary>Returns true when the saved state was restored, false when the shim refused it and started the script fresh.</summary>
    public bool Restore(byte[] data)
    {
        int rc;
        fixed (byte* dp = data)
            rc = Native.slua_restore(_h, dp, (nuint)data.Length);
        Host.Check("slua_restore", rc);
        return rc == Host.Ok;
    }

    public (FaultKind Kind, string Message) Fault()
    {
        byte* buf = stackalloc byte[4096];
        int kind;
        Host.Check("slua_fault", Native.slua_fault(_h, &kind, buf, 4096));
        return ((FaultKind)kind, Marshal.PtrToStringUTF8((IntPtr)buf) ?? "");
    }

    public int UsedMemory => Native.slua_used_memory(_h);
    public int MemoryLimit => Native.slua_get_memory_limit(_h);
    public void SetMemoryLimit(int limit) => Host.Check("slua_set_memory_limit", Native.slua_set_memory_limit(_h, limit));

    public void Dispose()
    {
        if (_h != IntPtr.Zero)
        {
            Native.slua_script_free(_h);
            _h = IntPtr.Zero;
        }
    }
}
