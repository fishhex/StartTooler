#!/usr/bin/env bash
# Build StartTooler upload-relay Go binary for Linux (amd64 + arm64).
#
# Cross-compiles from current host Go toolchain.
# Idempotent: only rebuilds when .go sources are newer than binaries,
# or when binaries don't exist. Caller (msbuild BeforeBuild target) skips
# this if Go toolchain is missing.
#
# Outputs:
#   StartTooler/Resources/relay-binaries/upload-relay-linux-amd64
#   StartTooler/Resources/relay-binaries/upload-relay-linux-arm64

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SRC_DIR="$REPO_ROOT/tools/upload-relay"
OUT_DIR="$REPO_ROOT/StartTooler/Resources/relay-binaries"

if ! command -v go >/dev/null 2>&1; then
    echo "[build-relay] go not found in PATH; skipping (will use stale binaries if present)"
    exit 0
fi

mkdir -p "$OUT_DIR"

# 同步 HTML：单一源 = $REPO_ROOT/StartTooler/Resources/upload.html，copy 到 Go embed 目录
CANONICAL_HTML="$REPO_ROOT/StartTooler/Resources/upload.html"
EMBED_HTML="$SRC_DIR/web/index.html"
if [[ -f "$CANONICAL_HTML" ]]; then
    if [[ ! -f "$EMBED_HTML" ]] || [[ "$CANONICAL_HTML" -nt "$EMBED_HTML" ]]; then
        mkdir -p "$SRC_DIR/web"
        cp "$CANONICAL_HTML" "$EMBED_HTML"
        echo "[build-relay] synced upload.html -> tools/upload-relay/web/index.html"
    fi
fi

# Targets: linux/amd64 and linux/arm64
TARGETS=(
    "amd64"
    "arm64"
)

# Find newest .go file mtime + go.mod + embedded HTML (after sync)
GO_MTIME=$(find "$SRC_DIR" -name "*.go" -type f -exec stat -f %m {} \; 2>/dev/null | sort -n | tail -1)
GO_MTIME=${GO_MTIME:-0}
HTML_MTIME=$(stat -f %m "$EMBED_HTML" 2>/dev/null || echo 0)
GO_MTIME=$(( GO_MTIME > HTML_MTIME ? GO_MTIME : HTML_MTIME ))
GO_MOD_MTIME=$(stat -f %m "$SRC_DIR/go.mod" 2>/dev/null || echo 0)
GO_MTIME=$(( GO_MTIME > GO_MOD_MTIME ? GO_MTIME : GO_MOD_MTIME ))

needs_rebuild() {
    local bin="$1"
    if [[ ! -f "$bin" ]]; then
        return 0
    fi
    local bin_mtime=$(stat -f %m "$bin")
    [[ $bin_mtime -lt $GO_MTIME ]]
}

rebuild_needed_any=false
for arch in "${TARGETS[@]}"; do
    bin="$OUT_DIR/upload-relay-linux-$arch"
    if needs_rebuild "$bin"; then
        rebuild_needed_any=true
        break
    fi
done

if [[ "$rebuild_needed_any" != "true" ]]; then
    echo "[build-relay] all binaries up-to-date"
    exit 0
fi

cd "$SRC_DIR"
for arch in "${TARGETS[@]}"; do
    bin="$OUT_DIR/upload-relay-linux-$arch"
    if needs_rebuild "$bin"; then
        echo "[build-relay] building linux/$arch -> $bin"
        GOOS=linux GOARCH="$arch" CGO_ENABLED=0 \
            go build -trimpath -ldflags="-s -w" -o "$bin" .
    fi
done

echo "[build-relay] done"
