#!/bin/bash
set -eu
arch=arm64
if [[ "$GOARCH" == amd64 ]]; then arch=x86_64; fi
target="$arch-apple-ios${IPHONEOS_DEPLOYMENT_TARGET:-16.0}"
if [[ "$PLATFORM_NAME" == iphonesimulator ]]; then target="$target-simulator"; fi
exec xcrun --sdk "$PLATFORM_NAME" clang -target "$target" -isysroot "$SDKROOT" "$@"
