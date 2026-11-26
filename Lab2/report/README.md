# Lab 2 report

- `report.pdf` - compiled report (13 pages), same title-page style as Lab 1.
- `report.tex` - LaTeX source. Compiled with Tectonic 0.17 (or any LaTeX
  distribution with the packages from the preamble).
- `figures/` - UTM logo, console screenshots, and the metrics charts (converted
  from the SVG files produced by `Lab2/scripts/metrics_report.py`).

Build:

```powershell
tectonic -X compile --outfmt pdf --keep-logs report.tex
```

The charts come from the captured runs:

- `Lab2/evidence/cross-device-2026-11-26/server-metrics/` (16 Go clients on a
  phone + C# server);
- `Lab2/evidence/cross-device-2026-11-26/desktop-csharp-clients-metrics/`
  (five simultaneous C# clients).

Regenerate the data and charts with:

```powershell
python Lab2/scripts/metrics_report.py --dir Lab2/evidence/cross-device-2026-11-26/server-metrics
python Lab2/scripts/metrics_report.py --dir Lab2/evidence/cross-device-2026-11-26/desktop-csharp-clients-metrics
```
