#!/usr/bin/env bash
# PreToolUse hook (Edit|MultiEdit|Write): blocks edits to files Claude must never touch by hand.
# Exit 2 = block the tool call; stderr is shown to Claude so it can pick another route.
# Parses the hook JSON with grep/sed so it runs without jq (Git Bash on Windows, macOS, Linux).

input=$(cat)
file=$(printf '%s' "$input" \
  | grep -o '"file_path"[[:space:]]*:[[:space:]]*"[^"]*"' \
  | head -n 1 \
  | sed -e 's/^"file_path"[[:space:]]*:[[:space:]]*"//' -e 's/"$//' \
  | tr -s '\\' '/')

[ -z "$file" ] && exit 0

block() {
  echo "Blocked by .claude/hooks/protect-files.sh: $file — $1" >&2
  exit 2
}

case "$file" in
  */.env | */.env.* | *.pfx | */secrets.json | *appsettings.*.local.json)
    block "secrets file. Use Aspire parameters / 'dotnet user-secrets' instead." ;;
  *.db | *.db-shm | *.db-wal | *.sqlite)
    block "SQLite database file. Change the schema via EF Core migrations, data via code." ;;
  */data/*.json)
    block "hackathon input/output data. training.json/challenge.json are provided by the organizers; result.json is written by TicketTriage.Batch." ;;
  */Migrations/*.Designer.cs | */Migrations/*ModelSnapshot.cs)
    block "generated EF Core file. Run 'dotnet ef migrations remove' / 'add' instead of editing." ;;
  */bin/* | */obj/*)
    block "build output. Edit the source instead." ;;
esac

exit 0
