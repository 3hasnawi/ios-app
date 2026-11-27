#!/bin/bash
set -eu
cd "$SRCROOT/sync/workspace"
export GOOS=ios CGO_ENABLED=1
export CC="$PWD/apple-clang.sh"
parts=()
for arch in $ARCHS; do
  case "$arch" in arm64) export GOARCH=arm64;; x86_64) export GOARCH=amd64;; *) exit 1;; esac
  part="$DERIVED_FILE_DIR/libpayrollsync-$arch.a"
  go build -trimpath -buildmode=c-archive -o "$part" ./cmd/apple
  parts+=("$part")
done
xcrun lipo -create "${parts[@]}" -output "$DERIVED_FILE_DIR/libpayrollsync.a"
