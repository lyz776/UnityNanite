# Continuous LOD / 4K delivery closure — 2026-07-31

## Accepted implementation

- Camera hierarchy refinement is `projected error OR projected surface density`.
- Density uses mean one-sided projected coverage (`unsigned surface area / 4`).
- Ordinary and persistent GPU hierarchy traversal share the same predicate.
- Terminal vertex-update repair runs only after normal/sloppy simplification would form a root.
- Persisted UNORM16 component, coverage, UV-stretch, Page and DAG gates remain strict.
- HardwareOnly remains the production raster mode; Hybrid remains available but cost gated.

## Bake and hierarchy gates

- Source triangles: 312,269
- Resident root triangles: 31,537
- Mip triangles: `312269,157361,80212,36564,17455,9849,5890,2160,1687`
- Compressed Pages: 69
- DAG fatal counters: all zero
- Component growth / terminal growth / loss / UV stretch outlier: all zero
- Five-direction monotonic regressions: all zero

Representative 2160p/60-degree density curve:

| Surface distance | Triangles |
|---:|---:|
| 0.02R | 307,760 |
| 0.5R | 300,479 |
| 4R | 223,371 |
| 8R | 157,964 |
| 16R | 83,501 |
| 64R | 52,402 |
| 128R | 47,881 |
| 256R | 45,676 |
| 512R | 31,537 |

## Final DX12 Player gate

- Resolution / instances / shadows: 3840x2160 / 432 / four cascades
- Camera: 195,725 Clusters / 21,812,994 triangles
- Shadow queues: 125,712 Clusters per cascade
- Pages: 72/72 resident
- Measured frames: 299
- Wall benchmark: 74.59 FPS / 13.407 ms
- Final sampled GPU frame: 13.116 ms
- Invalid kernel, missing resource/binding, RenderGraph, RenderTexture and queue overflow errors: none
- Validation image: `stress-frame.png`

## Rejected experiments

- Allowing component growth inside terminal native candidates raised roots to 38,331 and reduced
  maximum mip to 7; reverted.
- A half-shadow-texel terminal cluster rejection removed zero submissions; reverted.
- Hybrid measured slower than HardwareOnly in this workload (18.79 ms sampled GPU frame).

## Known performance ceiling

With shadows disabled, the same 4K/432 workload samples 6.05 ms GPU. The remaining dominant cost
is therefore the 31,537-triangle correctness-preserving root cut repeated in four shadow cascades.
This limitation is recorded rather than hidden by lowering near-camera quality or silently reducing
the required four-cascade shadow configuration.

Evidence logs:

- `Logs/codex-density-terminal-strict-final-bake.log`
- `Logs/codex-density-quarter-projection-audit.log`
- `Logs/codex-density-final-build.log`
- `Logs/codex-density-final-4k-432-hardware-player.log`
