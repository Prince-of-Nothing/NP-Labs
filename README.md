# NP-Labs — Laboratory Work 1

The C#/.NET 8 implementation for Concurrency in Practice is in [Lab1/](./Lab1/).
See [Lab1/README.md](./Lab1/README.md) for build and run instructions for data
generation, Tasks 1a/1b/1c, and Task 2.

The `Lab1/usb/` folder is a local staging area for the eight files used in Task
2 and is intentionally ignored by Git. `Lab1/resources/` holds the lab PDFs and
is ignored as well.

## Swiss army knife scripts

`scripts/` contains two equivalent entry points that drive the whole lab:
prerequisite check, build, data generation, every task, hardware capture,
result verification, and report charts.

- Windows: `powershell -ExecutionPolicy Bypass -File scripts\lab.ps1 <command>`
- Linux (CachyOS): `scripts/lab.sh <command>` (run `chmod +x scripts/lab.sh` once)

Both print identical help for the same verbs (`check`, `status`, `build`,
`generate`, `split`, `task1a`, `task1b`, `task1c`, `task1`, `task2`,
`hardware`, `verify`, `charts`, `all`, `clean`). A typical session:

```text
lab check                          verify .NET 8 SDK, git, python + matplotlib
lab all                            build, generate 50M numbers, split into 8
                                   files, run tasks 1a/1b/1c, capture hardware,
                                   verify the totals, and draw the charts
lab task2 --dir <usb path>         Task 2 with an eject/replug pause per
                                   configuration, as the lab requires
lab verify                         cross-check that every correct row agrees
lab charts                         regenerate Lab1/charts/*.png for the report
```

Generated artifacts (`numbers.txt`, `usb/`, `charts/`, `hardware.txt`,
`results.csv`) are ignored by Git.
