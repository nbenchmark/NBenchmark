---
title: Deep Dives
description: The engineering internals of NBenchmark - how workers are launched, what crosses the wire, and how the measurement engine resolves its numbers.
order: 9
---

# Deep dives

The [Features](../features/) and [Statistics](../statistics/) sections describe what NBenchmark does and how to use it. These pages provide the engineering perspective: how the engine finds and launches a worker, what crosses the process boundary and why, and how the measurement engine resolves the numbers it reports.

Each page is self-contained but assumes you've read the high-level treatment first: the feature page for the behavior and the statistics page for the math.

## In this section

- **[Isolation internals](./isolation-internals.md)** - The worker protocol: discovery, addressing, the wire format, budget ceilings, and the captured-state transfer rules that decide what can cross a process boundary.
- **[The measurement engine](./measurement-engine.md)** - The adaptive loop at engineering depth: the clock-resolution probe, jitter calibration and detector auto-switch, quantization, and the optional-stopping correction.
- **[Instance lifetime resolution](./instance-lifetime-resolution.md)** - How long a benchmark instance lives, who decides, and why the answer travels with the run.

## See also

- [Features](../features/) - The user-facing model these pages describe
- [Statistics](../statistics/) - The mathematical methodology
- [Reference](../reference/) - The configuration and CLI surface
