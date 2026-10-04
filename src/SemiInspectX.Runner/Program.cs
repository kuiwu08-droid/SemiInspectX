using System.Diagnostics;
using System.Text.Json;
using SemiInspectX.Core;
using SemiInspectX.Infrastructure;

static string Option(string[] args, string name, string fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}
static double Percentile(IEnumerable<double> source, double q)
{
    var values = source.Order().ToArray();
    return values.Length == 0 ? 0 : values[(int)Math.Ceiling((values.Length - 1) * q)];
}
try
{
    string command = args.FirstOrDefault() ?? "inspect";
    string dataset = Path.GetFullPath(Option(args, "--dataset", "datasets/generated/demo"));
    string output = Path.GetFullPath(Option(args, "--output", "artifacts/run"));
    Recipe recipe = Recipe.Load(Option(args, "--recipe", "recipes/default.json"));
    int count = int.Parse(Option(args, "--count", "100"));
    Directory.CreateDirectory(output);
    if (command == "inspect")
    {
        await using var session = new SimulationSession(dataset, output);
        if (Enum.TryParse<FaultKind>(Option(args, "--fault", "None"), true, out var fault))
            session.Faults.Plan = new(fault, int.Parse(Option(args, "--fault-die", "10")));
        session.Controller.EventRaised += e => Console.WriteLine($"{e.Timestamp:HH:mm:ss.fff} {e.Code}: {e.Message}");
        await session.Controller.InitializeAsync();
        await session.LoadAsync(recipe, count);
        await session.Controller.StartAsync(Option(args, "--lot", "LOT-DEMO"), Option(args, "--wafer", "WAFER-01"), args.Contains("--serial"));
        await session.Controller.Completion;
        var results = await session.Store.QueryResultsAsync(session.Controller.RunId!);
        await SqliteResultStore.ExportCsvAsync(Path.Combine(output, "results.csv"), results);
        var run = (await session.Store.QueryRunsAsync(run: session.Controller.RunId)).Single();
        Console.WriteLine(JsonSerializer.Serialize(run, Recipe.Json));
        return session.Controller.LastStatus == RunStatus.Completed ? 0 : 2;
    }
    if (command == "evaluate")
    {
        var manifest = Wafer.Manifest(dataset);
        using var engine = new NativeEngine(recipe, dataset);
        int defects = 0, matched = 0, normals = 0, falsePositives = 0, widthRejected = 0, alignmentErrors = 0;
        var errors = new List<double>();
        var failures = new List<object>();
        static double Iou(Defect d, TruthFrame t)
        {
            int overlap = Math.Max(0, Math.Min(d.X + d.Width, t.DefectX + t.DefectWidth) - Math.Max(d.X, t.DefectX)) *
                Math.Max(0, Math.Min(d.Y + d.Height, t.DefectY + t.DefectHeight) - Math.Max(d.Y, t.DefectY));
            return (double)overlap / (d.Width * d.Height + t.DefectWidth * t.DefectHeight - overlap);
        }
        foreach (var truth in manifest.Frames)
        {
            using var frame = Pgm.Read(Path.Combine(dataset, truth.Image));
            try
            {
                var result = engine.Inspect(frame);
                errors.Add(Math.Abs(result.WidthPixels - truth.WidthPixels));
                if (result.ShiftX != truth.ShiftX || result.ShiftY != truth.ShiftY)
                    alignmentErrors++;
                if (truth.DefectWidth > 0)
                {
                    defects++;
                    if (result.Defects.Any(d => Iou(d, truth) >= 0.5))
                        matched++;
                    else
                        failures.Add(new
                        {
                            truth.Index,
                            Reason = "Missed defect",
                            truth.Kind
                        });
                }
                if (truth.Kind == "normal")
                {
                    normals++;
                    if (result.Verdict != Verdict.Pass)
                    {
                        falsePositives++;
                        failures.Add(new
                        {
                            truth.Index,
                            Reason = "False rejection",
                            truth.Kind
                        });
                    }
                }
                if (truth.Kind == "width-out-of-spec")
                {
                    if (result.Verdict == Verdict.Fail)
                        widthRejected++;
                    else
                        failures.Add(new
                        {
                            truth.Index,
                            Reason = "Missed width rejection",
                            truth.Kind
                        });
                }
            }
            catch (Exception e) { failures.Add(new { truth.Index, Reason = e.Message, truth.Kind }); if (truth.DefectWidth > 0) defects++; if (truth.Kind == "normal") { normals++; falsePositives++; } }
        }
        double recall = defects == 0 ? 0 : (double)matched / defects, fpr = normals == 0 ? 1 : (double)falsePositives / normals;
        double mae = errors.Count == 0 ? double.PositiveInfinity : errors.Average();
        bool passed = defects > 0 && normals > 0 && recall >= 0.95 && fpr <= 0.01 && alignmentErrors == 0 && failures.Count == 0 && (manifest.NoiseSigma > 0 || mae <= 0.5);
        var report = new
        {
            manifest.Seed,
            manifest.NoiseSigma,
            Frames = manifest.Frames.Count,
            Defects = defects,
            Matched = matched,
            Recall = recall,
            Normals = normals,
            FalsePositives = falsePositives,
            FalsePositiveRate = fpr,
            WidthMeanAbsoluteErrorPixels = mae,
            WidthRejected = widthRejected,
            AlignmentErrors = alignmentErrors,
            Matching = "Bounding-box IoU >= 0.5, aligned coordinates",
            Passed = passed,
            Failures = failures
        };
        await File.WriteAllTextAsync(Path.Combine(output, "evaluation.json"), JsonSerializer.Serialize(report, Recipe.Json));
        await File.WriteAllTextAsync(Path.Combine(output, "evaluation.md"), $"# Synthetic evaluation\n\nSeed: {manifest.Seed}; frames: {manifest.Frames.Count}; noise sigma: {manifest.NoiseSigma}. Translation ±5 px; brightness ±10; defect contrast ≥60; area ≥9 px. Matching: aligned bounding-box IoU ≥0.5.\n\n|Metric|Measured|\n|---|---:|\n|Defect recall|{recall:P2}|\n|Normal die false rejection rate|{fpr:P2}|\n|Line-width MAE (px)|{mae:F4}|\n|Alignment errors|{alignmentErrors}|\n|Failures|{failures.Count}|\n\nAcceptance: {(passed ? "PASS" : "FAIL")}. Synthetic simulation only; no physical calibration or real wafer validation. Failure details: evaluation.json.\n");
        Console.WriteLine(JsonSerializer.Serialize(report, Recipe.Json));
        return passed ? 0 : 3;
    }
    if (command == "benchmark")
    {
        int rounds = int.Parse(Option(args, "--rounds", "5"));
        if (rounds < 1 || count < 1)
            throw new ArgumentException("Positive rounds/count required.");
        recipe = recipe with
        {
            Workers = int.Parse(Option(args, "--workers", "2")),
            QueueCapacity = int.Parse(Option(args, "--capacity", "8"))
        };
        recipe.Validate();
        var algorithm = new List<double>();
        using (var engine = new NativeEngine(recipe, dataset))
        {
            using var warm = Pgm.Read(Path.Combine(dataset, "die-0.pgm"));
            for (int i = 0; i < 20; i++)
                engine.Inspect(warm);
            for (int round = 0; round < rounds; round++)
                for (int i = 0; i < count; i++)
                {
                    using var frame = Pgm.Read(Path.Combine(dataset, $"die-{i}.pgm"));
                    algorithm.Add(engine.Inspect(frame).AlgorithmMs);
                }
        }
        var runs = new List<object>();
        var fingerprints = new Dictionary<bool, string>();
        var serialFps = new List<double>();
        var pipelineFps = new List<double>();
        for (int round = -1; round < rounds; round++)
            foreach (bool serial in new[] { true, false })
            {
                string runOutput = Path.Combine(output, $"{(serial ? "serial" : "pipeline")}-{round + 1}");
                await using var session = new SimulationSession(dataset, runOutput);
                await session.Controller.InitializeAsync();
                await session.LoadAsync(recipe, round < 0 ? Math.Min(count, 20) : count);
                var timer = Stopwatch.StartNew();
                await session.Controller.StartAsync(serial: serial);
                await session.Controller.Completion;
                timer.Stop();
                if (session.Controller.LastStatus != RunStatus.Completed)
                    throw new InvalidOperationException("Benchmark run failed.");
                var results = await session.Store.QueryResultsAsync(session.Controller.RunId!);
                if (round < 0)
                    continue;
                string fingerprint = JsonSerializer.Serialize(results.Select(r => new { r.Die.Index, r.Die.X, r.Die.Y, r.Inspection.Verdict, r.Inspection.ShiftX, r.Inspection.ShiftY, r.Inspection.WidthPixels, r.Inspection.Defects }));
                if (fingerprints.Values.Any(f => f != fingerprint))
                    throw new InvalidOperationException("Serial/pipeline output mismatch.");
                fingerprints[serial] = fingerprint;
                double fps = count / timer.Elapsed.TotalSeconds;
                (serial ? serialFps : pipelineFps).Add(fps);
                runs.Add(new
                {
                    Round = round + 1,
                    Mode = serial ? "serial" : "pipeline",
                    Frames = count,
                    Throughput = fps,
                    LatencyP50 = Percentile(results.Select(r => r.EndToEndMs), 0.5),
                    LatencyP95 = Percentile(results.Select(r => r.EndToEndMs), 0.95),
                    AlgorithmP50 = Percentile(results.Select(r => r.Inspection.AlgorithmMs), 0.5),
                    PrivateMemoryBytes = Process.GetCurrentProcess().PrivateMemorySize64,
                    Workers = serial ? 1 : recipe.Workers,
                    recipe.QueueCapacity
                });
            }
        var report = new
        {
            Environment = new
            {
                System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                CPU = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
                LogicalProcessors = Environment.ProcessorCount,
                Runtime = Environment.Version.ToString(),
                Configuration = "Release x64",
                OpenCv = "4.14.0; internal threads=1",
                Storage = "SQLite WAL synchronous=NORMAL; image + transaction + latency metadata update",
                recipe.StageDelayMs,
                recipe.CameraDelayMs
            },
            Rounds = rounds,
            FramesPerRound = count,
            AlgorithmP50 = Percentile(algorithm, 0.5),
            AlgorithmP95 = Percentile(algorithm, 0.95),
            SerialMeanFps = serialFps.Average(),
            PipelineMeanFps = pipelineFps.Average(),
            ThroughputRatio = pipelineFps.Average() / serialFps.Average(),
            OutputEquivalent = true,
            Runs = runs
        };
        await File.WriteAllTextAsync(Path.Combine(output, "benchmark.json"), JsonSerializer.Serialize(report, Recipe.Json));
        await File.WriteAllTextAsync(Path.Combine(output, "benchmark.md"), $"# Measured benchmark\n\n{rounds} rounds × {count} frames, after warmup. Same C++ algorithm and simulated delays; outputs compared including coordinates, decisions, widths and defect boxes.\n\n|Metric|Measured|\n|---|---:|\n|Serial throughput (mean)|{serialFps.Average():F2} dies/s|\n|Pipeline throughput (mean)|{pipelineFps.Average():F2} dies/s|\n|Throughput ratio|{report.ThroughputRatio:F3}×|\n|Pure algorithm P50|{report.AlgorithmP50:F3} ms|\n|Pure algorithm P95|{report.AlgorithmP95:F3} ms|\n\nPer-round latency, memory, hardware and queue settings: benchmark.json. End-to-end latency includes queueing and the first result commit; the final latency metadata update is excluded. High throughput can increase queueing latency. Results apply to this simulation and machine only.\n");
        Console.WriteLine(JsonSerializer.Serialize(report, Recipe.Json));
        return 0;
    }
    throw new ArgumentException("Commands: inspect, evaluate, benchmark. Options: --dataset --recipe --output --count; benchmark: --rounds --workers --capacity; inspect: --serial --fault --fault-die --lot --wafer.");
}
catch (Exception e) { Console.Error.WriteLine(e.ToString()); return 1; }
