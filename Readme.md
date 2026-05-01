# DropdownGcDemo

Minimal repro of the GC-pressure bottleneck described in the blog post.
Two implementations of the same dropdown render — one allocates like crazy,
the other behaves.

Runs on Windows, macOS, or Linux. You just need the .NET 8 SDK.

## Run it

```bash
dotnet run -c Release
```

You should see something like:

```
[SLOW: string concat + Replace in loop]
  elapsed     :     ???? ms
  allocated   :   1xxx.x MB
  GC Gen0/1/2 :  xxx /  xx /  xx

[FAST: HtmlAgilityPack]
  elapsed     :     ???? ms
  allocated   :    xxx.x MB
  GC Gen0/1/2 :   xx /   x /   0
```

The interesting numbers are the **allocated MB** and the **Gen 2 count**.
The slow path promotes objects all the way out to Gen 2; the fast path
barely touches Gen 1.

## Profile it

Install the global diagnostic tools once. They work the same on Windows,
macOS, and Linux:

```bash
dotnet tool install -g dotnet-counters
dotnet tool install -g dotnet-trace
```

Make sure the .NET tools directory is on your `PATH` (the installer tells
you the path on first install).

### 1. Live counters — the cheapest, fastest signal

In one terminal:
```bash
dotnet run -c Release
```

In another, while it's running:
```bash
dotnet-counters monitor --name DropdownGcDemo System.Runtime
```

Watch `gen-2-gc-count`, `alloc-rate`, and `% time in GC`. The slow phase
will spike all three.

### 2. Trace + flame graph — the smoking gun

```bash
dotnet-trace collect --name DropdownGcDemo \
    --providers Microsoft-DotNETCore-SampleProfiler \
    --format speedscope \
    -o trace.speedscope.json
```

Open `trace.speedscope.json` at https://www.speedscope.app — you'll see
`String.Replace` and `String.Concat` dominating the slow path, and almost
nothing in the fast path.

### 3. Other options

- **PerfView** (Windows) — the classic deep-dive tool, free from Microsoft.
- **dotTrace / dotMemory** (JetBrains) — paid, GUI, very pleasant.
- **ANTS Performance Profiler** (Red Gate) — paid, GUI, Windows.
- **BenchmarkDotNet** — for micro-benchmarking specific methods with
  rigorous statistics.

Whichever you pick, the takeaway is the same: don't guess, profile.