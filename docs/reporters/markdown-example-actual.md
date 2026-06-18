## Benchmark Results

> **2026-06-18 00:03:05 UTC** · 3 warmup · 48 measured · realistic profile

### Comparison

| | Benchmark | Median | Mean | Ops/s | Ratio | Scale | Sig | Magnitude | Alloc/op |
|:---:|---|---:|---:|---:|:---:|---|---:|---:|---:|
| | Compute | 2.4 ns | 2.4 ns | 419.68 Mops/s | **1.00x** | `███████████████` | ✗ | neg | 0 B |
| | **Baseline** _(baseline)_ | 2.4 ns | 2.4 ns | 418.02 Mops/s | _baseline_ | `███████████████` | - | - | 0 B |

### Precision & Tail Latency

| Benchmark | Error (±CI) | StdDev | CV | P95 | P99 |
|---|---:|---:|---:|---:|---:|
| Compute | ±0.0 ns (0.73%) | 0.1 ns | 2.58% | 2.5 ns | 2.5 ns |
| Baseline | ±0.0 ns (0.76%) | 0.1 ns | 2.60% | 2.5 ns | 2.5 ns |

---

### Interpretation

**Omnibus**: not run (fewer than 3 comparable groups).

- Significance: Mann-Whitney U (p < 0.05)
- Outliers: IQR fence (1.5×)
- Effect metric: Cliff's δ (Romano neg/small/med/large labels)

_2 benchmark(s) · 0.0s total · Mann-Whitney U (p < 0.05) · CI 95%_
