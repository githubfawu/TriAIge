#!/usr/bin/env bash
# PostToolUse hook (Edit|MultiEdit|Write): applies .editorconfig whitespace rules to the edited .cs file.
# 'dotnet format whitespace --folder' needs no build/restore, so it stays fast. Never blocks (always exit 0).

input=$(cat)
file=$(printf '%s' "$input" \
  | grep -o '"file_path"[[:space:]]*:[[:space:]]*"[^"]*"' \
  | head -n 1 \
  | sed -e 's/^"file_path"[[:space:]]*:[[:space:]]*"//' -e 's/"$//' \
  | tr -s '\\' '/')

case "$file" in
  *.cs) ;;
  *) exit 0 ;;
esac
case "$file" in
  */Migrations/* | */obj/* | */bin/*) exit 0 ;;
esac

[ -f "$file" ] || exit 0
command -v dotnet >/dev/null 2>&1 || exit 0

dir=$(dirname "$file")
name=$(basename "$file")
dotnet format whitespace "$dir" --folder --include "$name" >/dev/null 2>&1 || true
exit 0
