# Verification and reproducibility

Run `scripts/build.ps1` for native + managed regression, `scripts/evaluate.ps1` for two independent datasets, and the README benchmark command for paired performance rounds.

## Acceptance evidence

- Native tests: nominal width/units, anomaly bounding box, translation, bad input and failed alignment.
- Managed tests: ABI/version and invalid buffers; known synthetic cases; serial and pipeline 100-die scans; unique coordinates; immutable recipe snapshot; persistence notification counts; byte-for-byte saved-image checks; filters; deterministic camera/stage/image/storage faults and reset recovery.
- Lifecycle tests: drain before Paused, retain RunId on Resume, reject duplicate commands, stop with capacity-one queues and slow consumers, compare serial/pipeline semantic outputs, and 1,000 start/stop cycles with at least one saved frame per cycle, bounded timeout and a broad memory-growth check.
- Desktop checks: real WPF startup, completed 100-die run, snapshot rendering and clean shutdown. Automated demo additionally drives pause/resume, faults, reset and aborted runs.

The invalid-recipe scenario verifies rejection before entering Ready; it intentionally does not put healthy devices into Error. Fault recovery means subsequent complete runs after the simulated fault is cleared.

## Evaluation conditions

Development/demo: seed 42, 100 frames. Evaluation: seed 314159, 1,000 frames, Gaussian noise sigma 2. Metrology: seed 271828, 1,000 frames, no noise. Both evaluations include shifts ±5 pixels and brightness offsets ±10. Each 1,000-frame dataset has 500 normal cases, 300 defect cases and 200 width-out-of-tolerance cases.

Ground truth comes from geometry, independently of detection. Defect matching uses aligned-coordinate bounding-box IoU ≥0.5. Normal-die false rejection includes any erroneous Fail. Width MAE is compared against generated width; the ≤0.5 px acceptance applies to the noise-free evaluation. Width-out-of-tolerance cases must also be rejected. Evaluation exits nonzero when acceptance fails; details are saved in JSON.

These small scenario families are deliberately controlled. They do not cover rotation, wafer texture, illumination gradients, focus variation, real optical point-spread functions or physical calibration. A 100% recall on this dataset is not a general semiconductor inspection accuracy claim.

## Performance interpretation

Each mode uses the identical C++ implementation and recipe. Twenty frames warm up the end-to-end paths; the native algorithm has its own warmup. Five rounds of 1,000 frames are recorded. The semantic comparison includes coordinates, verdicts, alignment shifts, width and defect boxes, excluding IDs/timestamps/timing.

OpenCV internal threads are fixed to one. The default recipe models camera and stage delays. Windows timer granularity can dominate short `Task.Delay` values. The pipeline's throughput improvement may be modest for this workload; larger bounded queues can increase P95 latency. No absolute timing gate is applied to GitHub's shared runners.

Reports retain OS, processor identifier, runtime, queue/worker settings, per-round latency and process memory. The memory-growth regression detects substantial leaks, not every small leak or every allocator behavior.

## Release check

`scripts/publish.ps1` creates a self-contained win-x64 desktop and CLI package with native engine, OpenCV, MSVC runtime and sample data. Local release verification relocates that package outside the source tree, strips SDK-related environment variables, and runs with a minimal PATH. This checks packaging completeness on the current Windows installation; it is not a separate clean-machine/VM certification.

GitHub workflow files are present. Hosted CI results and remote release status remain unverified until the repository is pushed and workflows run.
