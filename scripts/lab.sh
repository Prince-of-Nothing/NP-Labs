#!/usr/bin/env bash
# Lab 1 Swiss-army-knife script for Linux (CachyOS and friends).
# lab.ps1 provides the same verbs for Windows.
#
# Usage:
#   scripts/lab.sh <command> [options]
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd -- "$SCRIPT_DIR/.." && pwd)"
LAB_DIR="$ROOT_DIR/Lab1"
NUMBERS_FILE="$LAB_DIR/numbers.txt"
USB_DIR="$LAB_DIR/usb"
DEFAULT_CSV="$LAB_DIR/results.csv"
HARDWARE_FILE="$ROOT_DIR/hardware.txt"
CHARTS_SCRIPT="$SCRIPT_DIR/charts.py"

COMMAND="${1:-help}"
if [[ $# -gt 0 ]]; then shift; fi

step() { printf '==> %s\n' "$*"; }
die()  { printf 'error: %s\n' "$*" >&2; exit 1; }

usage() {
  cat <<'EOF'
Lab 1 Swiss-army-knife script for Linux (CachyOS). lab.ps1 provides the same verbs for Windows.

Usage:
  scripts/lab.sh <command> [options]

Commands:
  check                  Verify prerequisites (.NET 8 SDK, git, python + matplotlib, free disk space)
  status                 Show which generated files exist and what is in results.csv
  build                  Build the project (Release)
  generate [--count N] [--seed N] [--force]
                         Write Lab1/numbers.txt (50,000,000 integers by default)
  split [--files N] [--force]
                         Split numbers.txt into Lab1/usb/numbers-01..08.txt
  task1a [args...]       Task 1a: counting with predivided file (1,2,4,8,16,32,64 threads)
  task1b [args...]       Task 1b: three ways to count (logical-processor count)
  task1c [args...]       Task 1c: producer and consumers (1..64 workers)
  task1 [args...]        Run task1a + task1b + task1c in order
                         (task1b always uses the logical-processor count)
  task2 --dir <path> [--threads 1,2,4] [--no-pause] [args...]
                         Task 2 from the USB drive; pauses between configurations
                         so you can unmount/unplug/replug as the lab requires
  hardware               Write hardware.txt (dotnet hardware + lscpu/free/lsblk/uname details)
  verify [--csv path]    Cross-check totals of every correct row in the CSV
  charts [--csv path] [--out dir]
                         Generate report charts (task1a/b/c/2 .png) with matplotlib
  all [--force]          check + build + generate + split + task1 + hardware + verify + charts
                         (everything that does not need the manual USB replug)
  clean [--all]          Remove bin/ and obj/; --all also removes generated data
                         (numbers.txt, usb/, charts/, hardware.txt, results.csv)

Any extra arguments are forwarded verbatim to the Lab1 program, e.g.
  scripts/lab.sh task1a --threads 1,2,4 --csv ~/runs/results.csv --expected-count 200000
EOF
}

run_dotnet() { ( cd "$LAB_DIR" && dotnet "$@" ); }
run_lab()    { ( cd "$LAB_DIR" && dotnet run -c Release --no-build -- "$@" ); }

build() {
  step "Building Lab1 (Release)"
  run_dotnet build -c Release --nologo
}

ensure_build() {
  local dll="$LAB_DIR/bin/Release/net8.0/Lab1.dll"
  if [[ -f "$dll" && "$dll" -nt "$LAB_DIR/Program.cs" && "$dll" -nt "$LAB_DIR/Lab1.csproj" ]]; then
    return
  fi
  build
}

split_file_count() {
  find "$USB_DIR" -maxdepth 1 -name 'numbers-*.txt' ! -name '*-summary.txt' 2>/dev/null | wc -l
}

cmd_check() {
  step "Checking prerequisites"
  local failed=0
  if command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks 2>/dev/null | grep -q '^8\.'; then
    echo "  [ok] .NET 8 SDK: $(dotnet --version)"
  else
    echo "  [!!] .NET 8 SDK not found. CachyOS/Arch: sudo pacman -S dotnet-sdk"
    echo "       (or https://dotnet.microsoft.com/download/dotnet/8.0)"
    failed=1
  fi

  if command -v git >/dev/null 2>&1; then
    echo "  [ok] git: $(git --version)"
  else
    echo "  [~~] git not found (needed for the repository link in the report)"
  fi

  if command -v python3 >/dev/null 2>&1; then
    if python3 -c 'import matplotlib' 2>/dev/null; then
      echo "  [ok] python3 + matplotlib (charts)"
    else
      echo "  [~~] matplotlib missing (needed for charts): sudo pacman -S python-matplotlib"
    fi
  else
    echo "  [~~] python3 not found (needed for charts): sudo pacman -S python-matplotlib"
  fi

  local free_gib
  free_gib="$(df -Pk "$ROOT_DIR" | awk 'NR==2 {printf "%.1f", $4/1024/1024}')"
  if awk -v f="$free_gib" 'BEGIN {exit !(f >= 2)}'; then
    echo "  [ok] ${free_gib} GiB free here (numbers.txt + split copies need ~1 GiB)"
  else
    echo "  [~~] only ${free_gib} GiB free here; generating 50M numbers may fail"
  fi

  if [[ $failed -eq 1 ]]; then exit 1; fi
  echo "Prerequisites OK."
}

cmd_status() {
  step "Status"
  if [[ -f "$NUMBERS_FILE" ]]; then
    awk -v s="$(stat -c %s "$NUMBERS_FILE")" 'BEGIN {printf "  numbers.txt: %.1f MiB\n", s/1048576}'
  else
    echo "  numbers.txt: missing (run: lab.sh generate)"
  fi

  local count
  count="$(split_file_count)"
  if [[ "$count" -gt 0 ]]; then
    local bytes
    bytes="$(find "$USB_DIR" -maxdepth 1 -name 'numbers-*.txt' ! -name '*-summary.txt' -printf '%s\n' 2>/dev/null | awk '{s+=$1} END {print s+0}')"
    awk -v c="$count" -v b="$bytes" 'BEGIN {printf "  usb/: %d files, %.1f MiB total\n", c, b/1048576}'
  else
    echo "  usb/: no split files (run: lab.sh split)"
  fi

  if [[ -f "$DEFAULT_CSV" ]]; then
    local rows tasks
    rows="$(($(wc -l < "$DEFAULT_CSV") - 1))"
    tasks="$(awk -F, 'NR>1 {count[$1]++} END {for (t in count) printf "%s\t%d\n", t, count[t]}' "$DEFAULT_CSV" | sort | awk -F'\t' '{printf "%s%s (%d)", sep, $1, $2; sep=", "}')"
    echo "  results.csv: $rows rows [$tasks]"
  else
    echo "  results.csv: missing (run some tasks)"
  fi

  if [[ -f "$HARDWARE_FILE" ]]; then
    echo "  hardware.txt: present"
  else
    echo "  hardware.txt: missing (run: lab.sh hardware)"
  fi
}

cmd_generate() {
  local force=0 args=()
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --force) force=1; shift ;;
      *) args+=("$1"); shift ;;
    esac
  done
  ensure_build
  step "Generating numbers.txt"
  if [[ $force -eq 1 ]]; then
    run_lab generate --force "${args[@]}"
  else
    run_lab generate "${args[@]}"
  fi
}

cmd_split() {
  local force=0 args=()
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --force) force=1; shift ;;
      *) args+=("$1"); shift ;;
    esac
  done
  ensure_build
  step "Splitting numbers.txt into usb/"
  if [[ $force -eq 1 ]]; then
    run_lab split --force "${args[@]}"
  else
    run_lab split "${args[@]}"
  fi
}

cmd_task() {
  local task="$1"; shift
  local csv="" args=()
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --csv) csv="${2:-}"; [[ -n "$csv" ]] || die "--csv requires a value"; shift 2 ;;
      *) args+=("$1"); shift ;;
    esac
  done
  ensure_build
  step "Running $task"
  run_lab "$task" --csv "${csv:-$DEFAULT_CSV}" "${args[@]}"
}

cmd_task1() {
  # task1b takes a single thread count; the lab wants the logical-processor
  # count, so --threads only applies to task1a and task1c.
  local main_args=("$@")
  local t1b_args=()
  local i=0
  while [[ $i -lt ${#main_args[@]} ]]; do
    if [[ "${main_args[i]}" == "--threads" ]]; then
      [[ $((i + 1)) -lt ${#main_args[@]} ]] || die "--threads requires a value"
      i=$((i + 2))
    else
      t1b_args+=("${main_args[i]}")
      i=$((i + 1))
    fi
  done
  cmd_task task1a "${main_args[@]}"
  cmd_task task1b "${t1b_args[@]}"
  cmd_task task1c "${main_args[@]}"
}

cmd_task2() {
  local dir="" csv="" threads="" pause=1 args=()
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --dir) dir="${2:-}"; shift 2 ;;
      --csv) csv="${2:-}"; shift 2 ;;
      --threads) threads="${2:-}"; shift 2 ;;
      --no-pause) pause=0; shift ;;
      *) args+=("$1"); shift ;;
    esac
  done
  [[ -n "$dir" ]] || die "task2 needs --dir <folder with numbers-01..08.txt on the USB drive>, e.g. --dir /run/media/\$USER/LABEL (see lsblk)"
  [[ -d "$dir" ]] || die "Directory not found: $dir"

  local series=()
  if [[ -n "$threads" ]]; then
    IFS=',' read -r -a series <<< "$threads"
  else
    series=(1 2 4)
  fi

  ensure_build
  local t
  for t in "${series[@]}"; do
    if [[ $pause -eq 1 ]]; then
      echo
      echo "Task 2 with $t thread(s):"
      echo "  1. Unmount and eject the drive, then unplug it, e.g.:"
      echo "       udisksctl unmount -b <device>   (or: umount \"$dir\")"
      echo "       udisksctl eject <device>"
      echo "  2. Plug it back in so no data comes from the file cache."
      read -r -p "  Press Enter to run this configuration" _ || die "no interactive input; rerun in a terminal or pass --no-pause"
    fi
    step "Running task2 ($t thread(s))"
    run_lab task2 --input-dir "$dir" --threads "$t" --csv "${csv:-$DEFAULT_CSV}" "${args[@]}"
  done
}

cmd_hardware() {
  ensure_build
  step "Collecting hardware details"
  {
    echo "Captured: $(date -Is)"
    echo
    echo "--- dotnet hardware ---"
    ( cd "$LAB_DIR" && dotnet run -c Release --no-build -- hardware )
    echo
    echo "--- lscpu ---"
    lscpu 2>/dev/null || true
    echo
    echo "--- free -h ---"
    free -h 2>/dev/null || true
    echo
    echo "--- lsblk disks ---"
    lsblk -d -o NAME,MODEL,ROTA,TRAN,SIZE 2>/dev/null || true
    echo
    echo "--- lsblk mounts ---"
    lsblk -o NAME,TRAN,MOUNTPOINT,SIZE 2>/dev/null || true
    echo
    echo "--- uname -a ---"
    uname -a 2>/dev/null || true
    echo
    echo "--- /etc/os-release ---"
    cat /etc/os-release 2>/dev/null || true
    echo
    echo "--- lsusb ---"
    lsusb 2>/dev/null || echo "(lsusb not available; install usbutils to list the USB drive)"
    echo
    echo "Record the USB flash-drive model, its USB version and capacity separately (drive spec or lsusb -v)."
  } | tee "$HARDWARE_FILE"
  echo
  echo "Wrote $HARDWARE_FILE"
}

cmd_verify() {
  local csv="$DEFAULT_CSV"
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --csv) csv="${2:-}"; shift 2 ;;
      *) die "verify: unknown argument $1" ;;
    esac
  done
  [[ -f "$csv" ]] || die "CSV not found: $csv"
  step "Verifying $csv"
  awk -F, '
    NR == 1 { next }
    {
      if ($11 == "true") {
        ok++
        key = $6 "," $7 "," $8 "," $9
        if (seen == "") { seen = key; canonical_total = $9; canonical_expected = $10 }
        else if (key != seen) { mismatch = 1; mismatch_keys[key] = 1 }
      } else {
        if ($2 ~ /^unsynchronized/) {
          printf "  [info] %s/%s workers %s: incorrect as designed (race condition); record the actual totals.\n", $1, $3, $2
        } else {
          printf "  [!!] %s/%s workers %s is incorrect - investigate before the report.\n", $1, $3, $2
          bad = 1
        }
      }
    }
    END {
      if (ok == 0) { print "  no correct rows found - check the measurements."; exit 1 }
      if (mismatch) {
        print "MISMATCH: correct rows disagree on the totals:"
        for (k in mismatch_keys) print "  " k
        exit 1
      }
      printf "All %d correct rows agree: negative/positive/zero/total = %s (expected %s).\n", ok, seen, canonical_expected
      if (canonical_total != canonical_expected) {
        printf "MISMATCH: total %s != expected %s\n", canonical_total, canonical_expected
        exit 1
      }
      if (bad) exit 1
      print "Verification passed."
    }
  ' "$csv"
}

cmd_charts() {
  local csv="$DEFAULT_CSV" out="$LAB_DIR/charts"
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --csv) csv="${2:-}"; shift 2 ;;
      --out) out="${2:-}"; shift 2 ;;
      *) die "charts: unknown argument $1" ;;
    esac
  done
  command -v python3 >/dev/null 2>&1 || die "python3 not found. CachyOS: sudo pacman -S python-matplotlib"
  python3 -c 'import matplotlib' 2>/dev/null || die "matplotlib not found. CachyOS: sudo pacman -S python-matplotlib"
  step "Generating charts from $csv"
  python3 "$CHARTS_SCRIPT" --csv "$csv" --out "$out"
}

cmd_all() {
  local force=0
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --force) force=1; shift ;;
      *) die "all: unknown argument $1" ;;
    esac
  done
  cmd_check
  build

  if [[ -f "$NUMBERS_FILE" && $force -eq 0 ]]; then
    step "numbers.txt already exists - skipping generate (use --force to redo)"
  elif [[ $force -eq 1 ]]; then
    cmd_generate --force
  else
    cmd_generate
  fi

  if [[ "$(split_file_count)" -ge 8 && $force -eq 0 ]]; then
    step "usb/ already has the split files - skipping split (use --force to redo)"
  elif [[ $force -eq 1 ]]; then
    cmd_split --force
  else
    cmd_split
  fi

  cmd_task task1a
  cmd_task task1b
  cmd_task task1c
  cmd_hardware
  cmd_verify
  cmd_charts || echo "Charts skipped (fix the error above and rerun: lab.sh charts)"

  echo
  echo "All local work is done."
  echo "Next: copy the 8 files from $USB_DIR to the USB drive and run:"
  echo "  scripts/lab.sh task2 --dir /run/media/\$USER/LABEL"
}

cmd_clean() {
  local all=0
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --all) all=1; shift ;;
      *) die "clean: unknown argument $1" ;;
    esac
  done
  step "Cleaning build output (bin/, obj/)"
  rm -rf "$LAB_DIR/bin" "$LAB_DIR/obj"
  if [[ $all -eq 1 ]]; then
    step "Removing generated data (numbers.txt, usb/, charts/, hardware.txt, results.csv)"
    rm -rf "$NUMBERS_FILE" "$USB_DIR" "$LAB_DIR/charts" "$HARDWARE_FILE" "$DEFAULT_CSV"
  fi
  echo "Clean done."
}

case "$COMMAND" in
  check)     cmd_check ;;
  status)    cmd_status ;;
  build)     build ;;
  generate)  cmd_generate "$@" ;;
  split)     cmd_split "$@" ;;
  task1a)    cmd_task task1a "$@" ;;
  task1b)    cmd_task task1b "$@" ;;
  task1c)    cmd_task task1c "$@" ;;
  task1)    cmd_task1 "$@" ;;
  task2)     cmd_task2 "$@" ;;
  hardware)  cmd_hardware ;;
  verify)    cmd_verify "$@" ;;
  charts)    cmd_charts "$@" ;;
  all)       cmd_all "$@" ;;
  clean)     cmd_clean "$@" ;;
  help|-h|--help) usage ;;
  *)
    echo "Unknown command: $COMMAND" >&2
    usage
    exit 1
    ;;
esac
