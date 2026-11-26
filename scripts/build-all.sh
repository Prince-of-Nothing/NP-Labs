#!/usr/bin/env bash
# Build every NP-Labs project and package the runnable artifacts under dist/.
# Usage: ./scripts/build-all.sh
set -u

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
if [ ! -f README.md ]; then
  echo "Run this script from the repository root (scripts/build-all.sh)." >&2
  exit 2
fi

DIST="$ROOT/dist"
LOGS="$DIST/logs"
HOST_OS="$(uname -s | tr '[:upper:]' '[:lower:]')"
HOST_ARCH="$(uname -m)"
case "$HOST_ARCH" in
  x86_64|amd64) HOST_ARCH=x64 ;;
  aarch64|arm64) HOST_ARCH=arm64 ;;
esac
HOST="$HOST_OS-$HOST_ARCH"
HOSTDIR="$DIST/$HOST"
WIN="$DIST/win-x64"
ARM="$DIST/linux-arm64"
rm -rf "$DIST"
mkdir -p "$HOSTDIR" "$WIN" "$ARM" "$LOGS"

FAILED=()

publish_dotnet() {
  local name="$1" project="$2"
  echo "== dotnet publish $name"
  local out="$HOSTDIR/$name" log="$LOGS/dotnet-$name.log"
  mkdir -p "$out"
  if ! dotnet publish "$ROOT/$project" -c Release -o "$out" --nologo -v q >"$log" 2>&1; then
    echo "FAILED dotnet publish $name" >&2
    tail -20 "$log" >&2 || true
    FAILED+=("dotnet:$name")
  fi
}

build_go() {
  local name="$1" workdir="$2" target="$3" goos="$4" goarch="$5" outdir="$6" exe="${7:-}"
  echo "== go build $name ($goos/$goarch)"
  local log="$LOGS/go-$name-$goos-$goarch.log"
  if ! ( cd "$ROOT/$workdir" && GOOS="$goos" GOARCH="$goarch" CGO_ENABLED=0 go build -o "$outdir/$name$exe" "$target" ) >"$log" 2>&1; then
    echo "FAILED go build $name ($goos/$goarch)" >&2
    tail -20 "$log" >&2 || true
    FAILED+=("go:$name:$goos/$goarch")
  fi
}

build_rust() {
  local name="$1" workdir="$2" binary="$3"
  if ! command -v cargo >/dev/null 2>&1; then
    echo "== skip rust $name (cargo not installed)"
    return
  fi
  echo "== cargo build $name"
  local log="$LOGS/rust-$name.log"
  if ! ( cd "$ROOT/$workdir" && cargo build --release ) >"$log" 2>&1; then
    echo "FAILED cargo build $name" >&2
    tail -20 "$log" >&2 || true
    FAILED+=("rust:$name")
    return
  fi
  local src="$ROOT/$workdir/target/release/$binary"
  if [ -f "$src" ]; then cp "$src" "$HOSTDIR/$name"; fi
}

echo "NP-Labs build-all (host: $HOST)"

publish_dotnet lab1 "Lab1/Lab1.csproj"
publish_dotnet lab2-server-cs "Lab2/csharp/Server/Server.csproj"
publish_dotnet lab2-client-cs "Lab2/csharp/Client/Client.csproj"
publish_dotnet lab3-chat-cs "Lab3/csharp/Chat.csproj"
publish_dotnet lab4-http-broker-cs "Lab4/part1-http/broker/Broker.csproj"
publish_dotnet lab4-http-publisher-cs "Lab4/part1-http/publisher/Publisher.csproj"
publish_dotnet lab4-http-subscriber-cs "Lab4/part1-http/subscriber/Subscriber.csproj"
publish_dotnet lab4-grpc-broker-cs "Lab4/part2-grpc/broker/Broker.csproj"
publish_dotnet lab4-grpc-publisher-cs "Lab4/part2-grpc/publisher/Publisher.csproj"
publish_dotnet lab4-grpc-subscriber-cs "Lab4/part2-grpc/subscriber/Subscriber.csproj"

GO_TARGETS=(
  "lab2-server-go|Lab2/go/server|."
  "lab2-client-go|Lab2/go/client|."
  "lab3-chat-go|Lab3/go|."
  "lab4-http-broker-go|Lab4/part1-http/go|./broker"
  "lab4-http-publisher-go|Lab4/part1-http/go|./publisher"
  "lab4-http-subscriber-go|Lab4/part1-http/go|./subscriber"
  "lab4-grpc-broker-go|Lab4/part2-grpc/go|./broker"
  "lab4-grpc-publisher-go|Lab4/part2-grpc/go|./publisher"
  "lab4-grpc-subscriber-go|Lab4/part2-grpc/go|./subscriber"
)

for entry in "${GO_TARGETS[@]}"; do
  IFS='|' read -r name dir target <<<"$entry"
  build_go "$name" "$dir" "$target" windows amd64 "$WIN" ".exe"
done
for entry in "${GO_TARGETS[@]}"; do
  IFS='|' read -r name dir target <<<"$entry"
  build_go "$name" "$dir" "$target" linux arm64 "$ARM" ""
done
for entry in "${GO_TARGETS[@]}"; do
  IFS='|' read -r name dir target <<<"$entry"
  build_go "$name" "$dir" "$target" "$HOST_OS" "$HOST_ARCH" "$HOSTDIR" ""
done

build_rust lab2-server-rust "Lab2/rust/server" server
build_rust lab2-client-rust "Lab2/rust/client" client

if command -v python3 >/dev/null 2>&1; then
  echo "== python report script"
  python3 -m py_compile "$ROOT/Lab2/scripts/metrics_report.py" || FAILED+=("python:metrics_report")
fi

cat >"$DIST/README.txt" <<'EOF'
NP-Labs compiled artifacts
==========================

win-x64/       Windows binaries (Go/Rust) and C# publish output lives in the host folder
linux-arm64/   Go arm64 binaries for the Android phone
<host>/        C# publish output and host Go/Rust binaries (built by this script)
logs/          build logs

Lab 2:
  lab2-server-cs/<Server> 5000
  lab2-client-cs/<Client> 127.0.0.1 5000 client
  lab2-server-go 5000
  linux-arm64/lab2-client-go 192.168.1.33 5000 client

Lab 3:
  lab3-chat-cs/<Chat> Alice 9000 secret
  lab3-chat-go Alice 9000 secret
  linux-arm64/lab3-chat-go Phone 9000 secret

Lab 4 HTTP:
  lab4-http-broker-cs/<Broker> http://localhost:8080/
  lab4-http-subscriber-cs/<Subscriber> http://localhost:8080 grades
  lab4-http-publisher-cs/<Publisher> http://localhost:8080 grades teacher tok-teacher

Lab 4 gRPC:
  lab4-grpc-broker-cs/<Broker>
  lab4-grpc-subscriber-cs/<Subscriber> http://localhost:5000 grades
  lab4-grpc-publisher-cs/<Publisher> http://localhost:5000 grades teacher tok-teacher

tokens.txt (ASCII, broker working directory):
  tok-teacher teacher grades,news
  tok-phone phone grades

See docs/tutorials/01-launch.md and docs/tutorials/02-testing.md.
EOF

echo "== checksums"
(
  cd "$DIST" || exit 1
  if command -v sha256sum >/dev/null 2>&1; then
    find . -type f ! -name SHA256SUMS.txt -print0 | sort -z | xargs -0 sha256sum >SHA256SUMS.txt
  else
    find . -type f ! -name SHA256SUMS.txt -print0 | sort -z | xargs -0 shasum -a 256 >SHA256SUMS.txt
  fi
)

echo
echo "Artifacts in $DIST"
ls -1 "$WIN" | sed 's/^/  win-x64\//'
echo "  linux-arm64/ $(ls -1 "$ARM" | wc -l) files"
echo "  $HOST/ $(ls -1 "$HOSTDIR" | wc -l) entries"

if [ "${#FAILED[@]}" -ne 0 ]; then
  echo "FAILED steps: ${FAILED[*]}" >&2
  exit 1
fi
echo "All builds succeeded."
