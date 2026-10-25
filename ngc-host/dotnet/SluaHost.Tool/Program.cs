// Copyright (c) 2026 Legion Builds
// Licensed under the MIT License; see LICENSE.txt in the repository root.
using SluaHost;

// Usage:
//   SluaHost.Tool run     <source file> <quanta seconds>    run a script, printing each step
//   SluaHost.Tool save    <asset file> <state file>   run a script until it first yields, save its state
//   SluaHost.Tool restore <asset file> <state file>   restore in a fresh VM, run to the end, print reported lines
// The asset is a compiled script, written by the caller. Exit code 0 on success.
if (args.Length == 3 && args[0] == "run")
{
    // Debug aid: run a source file, printing each step. run <source file> <quanta seconds>
    var k = new TestKit();
    using var v = new Vm(Watchdog.Resident);
    k.Register(v);
    using var sc = v.NewScript(Host.Compile(File.ReadAllText(args[1]), args[1].EndsWith(".lsl")), tag: 1);
    sc.Start();
    for (int i = 0; i < 40; i++)
    {
        var r = sc.Step(double.Parse(args[2]));
        Console.WriteLine($"step {i}: {r.Status} sleep={r.SleepServed} fault={r.Fault}");
        if (r.Status is StepStatus.Fault or StepStatus.Ok or StepStatus.Idle) break;
    }
    var (kind, msg) = sc.Fault();
    Console.WriteLine($"fault: {kind} {msg}");
    foreach (var line in k.Snapshot()) Console.WriteLine("line: " + line);
    return 0;
}
if (args.Length != 3) { Console.Error.WriteLine("usage: save|restore <asset> <state>"); return 2; }

byte[] asset = File.ReadAllBytes(args[1]);
var kit = new TestKit();
using var vm = new Vm(Watchdog.Resident);
kit.Register(vm);
using var script = vm.NewScript(asset, tag: 1);

if (args[0] == "save")
{
    script.Start();
    var r = script.Step(0.05);
    if (r.Status != StepStatus.Preempted) { Console.Error.WriteLine("expected the script to be mid-yield, got " + r.Status); return 3; }
    File.WriteAllBytes(args[2], script.Serialize());
}
else if (args[0] == "restore")
{
    bool restored = script.Restore(File.ReadAllBytes(args[2]));
    Console.WriteLine("restored=" + restored);
    var last = script.RunToIdle(0.05);
    Console.WriteLine("final=" + last.Status);
}
else return 2;

foreach (var line in kit.Snapshot()) Console.WriteLine("line: " + line);
return 0;
