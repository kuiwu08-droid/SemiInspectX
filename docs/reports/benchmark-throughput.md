# Measured benchmark

5 rounds × 1000 frames, after warmup. Same C++ algorithm and simulated delays; outputs compared including coordinates, decisions, widths and defect boxes.

|Metric|Measured|
|---|---:|
|Serial throughput (mean)|557.58 dies/s|
|Pipeline throughput (mean)|912.94 dies/s|
|Throughput ratio|1.637×|
|Pure algorithm P50|0.400 ms|
|Pure algorithm P95|0.451 ms|

Per-round latency, memory, hardware and queue settings: benchmark.json. End-to-end latency includes queueing and the first result commit; the final latency metadata update is excluded. High throughput can increase queueing latency. Results apply to this simulation and machine only.
