#!/usr/bin/env bash
# Refreshes the Quasar book that ships inside the plugin from a pinned commit of the public Quasar repository.
# Usage: scripts/refresh-quasar-book.sh [commit]   (default: the commit in src/QuasarAssistant/QuasarBook/COMMIT)
# Pass a new commit to move the pin; the script writes it to COMMIT. Review the diff before committing.
set -euo pipefail

book="$(cd "$(dirname "$0")/.." && pwd)/src/QuasarAssistant/QuasarBook"
commit="${1:-$(cat "$book/COMMIT")}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

curl -fsSL "https://codeload.github.com/CometWorks/quasar/tar.gz/$commit" \
    | tar -xz -C "$work" --wildcards '*/skills/quasar-book/*.md'

rm -f "$book"/*.md
cp "$work"/*/skills/quasar-book/*.md "$book/"
# SKILL.md tells a coding agent to grep the folder; the assistant has its own tools for that.
rm -f "$book/SKILL.md"
echo "$commit" > "$book/COMMIT"
echo "Quasar book: $(ls "$book"/*.md | wc -l) pages from $commit"
