#!/usr/bin/env sh
set -eu

DIR="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
VENDOR="$DIR/public/vendor"
mkdir -p "$VENDOR"

curl -L "https://unpkg.com/react@18.3.1/umd/react.production.min.js" -o "$VENDOR/react.js"
curl -L "https://unpkg.com/react-dom@18.3.1/umd/react-dom.production.min.js" -o "$VENDOR/react-dom.js"
curl -L "https://unpkg.com/htm@3.1.1/dist/htm.js" -o "$VENDOR/htm.js"

echo "DeskZone vendor dependencies downloaded."
