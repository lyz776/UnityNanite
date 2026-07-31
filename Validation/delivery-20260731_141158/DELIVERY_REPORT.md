# UnityNanite delivery-candidate acceptance — 2026-07-31

## Result

Accepted as an approximately 90% delivery candidate for the requested Unity 6 / Windows DX12
scope. HardwareOnly is the production default. Hybrid is correctness-valid and cost gated.

## Main-project Bake

- Source: 233,886 vertices / 312,269 triangles
- Output: 5,122 Clusters / 781 Parts / 316 groups / 69 Pages
- Hierarchy: 23 roots, depth 7, mip 0–7
- Page V3: 15.45 MiB packed / 11.93 MiB NZC1-LZ4 / 30.35 MiB raw
- Page contract: 69/69 compressed, 6 root Pages, 0 oversized, 0 split groups
- DAG fatal counters: membership, duplicate, mip, invalid range, error monotonicity,
  containment, metadata and triangle-reduction failures all zero
- Structural audit: component growth/loss and UV-stretch outliers all zero
- Five directional distance sweeps: zero monotonic regressions

The local meshoptimizer native plugin does not export `SimplifySloppy`; the Bake used the
documented topology-preserving terminal-group fallback. It was accepted only because the full
structural, hierarchy, Page and runtime contracts passed.

## Main-project clean DX12 Player

- Unity 6000.3.10f1, NVIDIA RTX 4500 Ada, D3D12 feature level 12.2
- HardwareOnly and Hybrid 2 px: 180 camera renders and 52 captures each
- Pages: 69/69 resident, 6 pinned roots
- Invalid kernel/resource/UAV/Page/device errors: zero
- Hybrid audit: 251,760 winner pixels, 1,267 raster tiles, settled

| Correctness check | Result |
|---|---:|
| HW fixed-camera max adjacent MAE | 0.000422 / 255 |
| Hybrid fixed-camera max adjacent MAE | 0.000548 / 255 |
| Fixed-camera pixels over 8 levels | 0 |
| HW/Hybrid close-range worst MAE | 0.000617 / 255 |
| HW/Hybrid all-frame worst MAE | 0.002776 / 255 |
| HW/Hybrid all-frame worst pixels over 8 | 3 / 921,600 |

## 432-instance performance closure

The scene uses direct unique-geometry GPU Scene references, exact packet indirect raster,
69/69 resident Pages and zero synchronous readback.

| Mode | Four cascades | FPS | Average frame |
|---|---:|---:|---:|
| HardwareOnly | off | 277.37 | 3.605 ms |
| HardwareOnly | on | 71.35 | 14.016 ms |
| Hybrid 2 px | on | 63.23 | 15.815 ms |

Camera cut: 198,637 Clusters / 21.857M triangles. Each correctness-preserving shadow root cut is
162,000 Clusters / 17.607M triangles. This identifies four-cascade root-floor raster as the
remaining performance bottleneck; camera GPU Scene, traversal, Page residency and packet capacity
remain admitted.

At this extreme workload Hybrid requested 7.86M tile nodes against a 524,288-node sparse arena.
The overflow contract appended whole Clusters to HW; the final HW/Hybrid stress images differed by
only 0.000841/255 MAE (16 pixels over 8). No geometry was dropped. HardwareOnly remains default.

## Evidence

- `Logs/delivery-20260731_140957-bake.log`
- `Logs/delivery-20260731_141158-{lod-audit,hierarchy-audit,build,hardware,hybrid2}.log`
- `Logs/delivery-20260731_141158-stress-{hw432,hybrid2-432,hw432-noshadow}.log`
- `Validation/delivery-20260731_141158/hardware-vs-hybrid.csv`
- Capture and telemetry subdirectories beside this report

Design basis remains pinned to NVIDIA `vk_lod_clusters` `70506fdc...`, meshoptimizer
`a6ecc73c...`, Nyx `bc7e5b1...`, UE Nanite's producer-group/streaming principles and the NVIDIA
meshlet CAD sample `4f6f7f19...`; platform-specific APIs were not copied into the Unity backend.

## Completion matrix

| Requested subsystem | Current evidence | Status |
|---|---|---|
| GPU Scene | one immutable mesh, direct instance/geometry refs, 432 instances | accepted |
| GPU traversal | instance/spatial/Part/Cluster indirect hierarchy; nine DAG passes | accepted |
| Indirect raster | exact triangle packets, one admitted chunk, whole-Cluster fallback | accepted |
| Four-level shadows | four native-atlas external queues, complete captures | accepted; hotspot |
| Page format/compression | NPG1 V3 + NZC1-LZ4, 69/69 compressed, round-trip Bake gate | accepted |
| Residency/streaming | root pinning, Page Table/Pool, external `.npages`, prior churn matrix | accepted |
| Continuous LOD | projected geometric error, finite-error audit, five monotonic sweeps | accepted |
| Hierarchy quality | DAG/containment/component/UV fatal counters all zero | accepted |
| HW/SW hybrid | async sparse SW plus packet HW, overflow returns whole Clusters to HW | accepted, cost gated |
| Heterogeneous materials | URP/Lit bins plus explicit registered resolve families | accepted for registered families |
| Performance loop | camera/shadow isolation and HW/Hybrid 432-instance A/B | accepted |

Not claimed: universal arbitrary-shader virtualization, Mesh Shader backend, RT BLAS reuse or
cross-platform performance parity. These are outside this Windows DX12 delivery candidate rather
than hidden incomplete requirements.
