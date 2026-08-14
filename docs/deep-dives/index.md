---
title: Deep Dives
description: The engineering internals of NBenchmark - how workers are launched, what crosses the wire, and how the measurement engine resolves its numbers.
order: 9
---

# Deep Dives

The [Features](../features/) and [Statistics](../statistics/) sections describe what NBenchmark does and how to use it. These pages are for readers who want the engineering picture: how a worker is found and launched, what crosses the process boundary and why, and how the measurement engine resolves the numbers it reports.

Each page is self-contained but assumes you have read the surface-level treatment first - the feature page for the behavior, the statistics page for the math.

## In this section

- **[Isolation internals](./isolation-internals.md)** - the worker protocol: discovery, addressing, the wire format, budget ceilings, and the captured-state transfer rules that decide what can cross a process boundary.
- **[The measurement engine](./measurement-engine.md)** - the adaptive loop at engineering depth: the clock-resolution probe, jitter calibration and detector auto-switch, quantization, and the optional-stopping correction.
- **[Instance lifetime resolution](./instance-lifetime-resolution.md)** - how long a benchmark instance lives, who decides, and why the answer travels with the run.

## See also

- [Features](../features/) - the user-facing model these pages describe
- [Statistics](../statistics/) - the mathematical methodology
- [Reference](../reference/) - the configuration and CLI surface
