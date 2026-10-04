# Engineering notes from implementation

## Native/managed lifetime design

The main risk was freeing an image while native inspection still used it. The implementation pins only for a synchronous native call, never retains input pointers, and transfers the frame to the writer before returning it to `ArrayPool`. Native engine/result allocations are covered by `SafeHandle`. Stop and fault handling drain acquired frames instead of abandoning active calls.

Evidence: invalid-buffer interop tests, capacity-one queue stop regression, and 1,000 start/stop cycles. This is a design validation, not a claim that a production crash occurred.

## Actual bug: WPF close reentrancy

The first automated desktop snapshot completed inspection and produced a screenshot, but exited with `InvalidOperationException`: closing was reentered from an async Closing handler when disposal completed synchronously. The fix guards an in-progress close and dispatches the final Close to the next UI turn. The snapshot command then exited normally. The full demo also exercises application teardown.

## Actual build issues

The initial CMake fetch of GoogleTest hit an HTTP/2 transport failure. The download was moved to GitHub's codeload endpoint and pinned with SHA256. OpenCV 4.14 also deprecated comma-style matrix initialization under the selected warning policy; transforms now use `cv::Matx23d`.

The first SQLite package restore identified a vulnerable transitive native SQLite version. Rather than suppressing the warning, the dependency was updated and the compatible SQLitePCLRaw bundle explicitly pinned. Package lock files retain the resolved versions.

Building while a benchmark loaded native DLLs also exposed unnecessary runtime-copy retries. The build now skips unchanged native files. Native changes still require stopping the process that has the older DLL loaded.

## Performance experiment

The serial baseline and pipeline use the same algorithm; the experiment tests overlap and reduced allocation, rather than attributing speed to C++. `docs/reports/benchmark.json` retains actual measurements. Synthetic device delays and Windows timer behavior can dominate end-to-end throughput, so pure algorithm latency is reported separately.

No invented throughput figures, profiler conclusions or historical race crashes are included. Follow-up profiling should be driven by the measured bottleneck.

Two completed experiments are retained. With default device delays the throughput ratio was 1.003×, consistent with acquisition waiting dominating the workload. With simulated delays set to zero, the measured ratio was 1.637×, but pipeline P95 latency increased to roughly 30–41 ms because frames queue ahead of persistence. The zero-delay run was repeated after the video encoder finished to avoid including that known competing workload; the earlier concurrent experiment is not used in the retained report.

The PGM decoder also removed an unnecessary temporary payload array: a span is copied directly from the file buffer into the pooled image buffer. File reading still allocates a buffer; this is not a zero-allocation claim.
