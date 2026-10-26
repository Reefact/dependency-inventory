#!/bin/sh
# Tests for lint-commit-message.sh. Run from anywhere: sh tools/commit-lint/test-lint-commit-message.sh
# Exit status: 0 when every case gives the expected result, 1 otherwise.

set -u

lint="$(dirname "$0")/lint-commit-message.sh"
failures=0

# check <expected exit status> <mode: ci|local> <message, printf-style escapes allowed>
check() {
  if [ "$2" = ci ]; then
    printf '%b\n' "$3" | sh "$lint" --ci - >/dev/null 2>&1
  else
    printf '%b\n' "$3" | sh "$lint" - >/dev/null 2>&1
  fi
  actual=$?
  if [ "$actual" != "$1" ]; then
    printf 'FAIL (expected %s, got %s): %s\n' "$1" "$actual" "$3"
    failures=$((failures + 1))
  fi
}

# Valid headers.
check 0 ci 'feat: add an inventory option'
check 0 ci 'fix(cli,report): handle 429 responses'
check 0 ci 'docs: describe the hook\n\nWith a body.'
check 0 ci 'Merge pull request #1 from branch'

# Breaking changes: header only, footer only, both, hyphenated footer.
check 0 ci 'feat!: remove legacy support'
check 0 ci 'feat: remove legacy support\n\nBREAKING CHANGE: migrate to the new API'
check 0 ci 'feat!: remove legacy support\n\nBREAKING CHANGE: migrate to the new API'
check 0 ci 'feat: remove legacy support\n\nBREAKING-CHANGE: migrate to the new API'

# Misspelled breaking-change footers.
check 1 ci 'feat: remove legacy support\n\nBREAKING_CHANGE: migrate'
check 1 ci 'feat: remove legacy support\n\nBreaking change: migrate'
check 1 ci 'feat: remove legacy support\n\nbreaking-change: migrate'

# Malformed headers.
check 1 ci ''
check 1 ci 'Initial commit'
check 1 ci 'Feat: add thing'
check 1 ci 'feature: add thing'
check 1 ci 'feat: Add thing'
check 1 ci 'feat: add thing.'
check 1 ci 'feat(Cli): add thing'
check 1 ci 'feat(cli, report): add thing'
check 1 ci 'feat: add something with a really long header that clearly exceeds the limit'
check 1 ci 'feat: add\nbody without a blank line'

# Headers that fail the full format without any specific diagnostic.
check 1 ci 'fix garbage: accept this'
check 1 ci 'fix(cli)garbage: accept this'
check 1 ci 'fix: 123'

# Autosquash placeholders: allowed locally, rejected in CI.
check 0 local 'fixup! feat: add thing'
check 1 ci 'fixup! feat: add thing'

if [ "$failures" -gt 0 ]; then
  printf '%s case(s) failed\n' "$failures"
  exit 1
fi
echo 'all commit-lint cases pass'
