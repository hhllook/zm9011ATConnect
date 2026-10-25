#!/bin/sh
set -eu

ROOT=$(cd "$(dirname "$0")" && pwd)
PROJECT="$ROOT/zm9011d"
VERSION="${VERSION:-0.1.0}"

cd "$PROJECT"
mkdir -p dist

echo "==> go test"
go test ./...

echo "==> build linux/arm64"
CGO_ENABLED=0 GOOS=linux GOARCH=arm64 \
  go build -trimpath -ldflags "-s -w -X main.version=$VERSION" \
  -o dist/zm9011d-linux-arm64 ./cmd/zm9011d

echo "==> package ipk + tar.gz"
go run ./tools/mkpackage \
  -binary dist/zm9011d-linux-arm64 \
  -config ../files/etc/config/zm9011 \
  -init ../files/etc/init.d/zm9011 \
  -version "$VERSION" \
  -arch aarch64_cortex-a53 \
  -outdir dist \
  -verify

echo "==> sync SDK package files"
mkdir -p "$ROOT/package/zm9011d/files"
cp -f dist/zm9011d-linux-arm64 "$ROOT/package/zm9011d/files/zm9011d"
cp -f ../files/etc/config/zm9011 "$ROOT/package/zm9011d/files/zm9011.config"
cp -f ../files/etc/init.d/zm9011 "$ROOT/package/zm9011d/files/zm9011.init"

echo "==> build linux/armv7"
CGO_ENABLED=0 GOOS=linux GOARCH=arm GOARM=7 \
  go build -trimpath -ldflags "-s -w -X main.version=$VERSION" \
  -o dist/zm9011d-linux-armv7 ./cmd/zm9011d

ls -lh dist
