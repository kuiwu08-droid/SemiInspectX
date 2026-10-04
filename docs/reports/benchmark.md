# Measured benchmark

5 rounds × 1000 frames, after warmup. Same C++ algorithm and simulated delays; outputs compared including coordinates, decisions, widths and defect boxes.

|Metric|Measured|
|---|---:|
|Serial throughput (mean)|31.82 dies/s|
|Pipeline throughput (mean)|31.92 dies/s|
|Throughput ratio|1.003×|
|Pure algorithm P50|0.402 ms|
|Pure algorithm P95|0.503 ms|

Per-round latency, memory, hardware and queue settings: benchmark.json. End-to-end latency includes queueing and the first result commit; the final latency metadata update is excluded. High throughput can increase queueing latency. Results apply to this simulation and machine only.
