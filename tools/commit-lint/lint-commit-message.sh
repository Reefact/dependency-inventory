#!/bin/sh
# Validate a commit message against the Conventional Commits 1.0.0 convention.
#
# Single source of truth shared by the local `commit-msg` hook (.githooks/commit-msg)
# and the CI check (.github/workflows/commit-lint.yml), so the two cannot diverge.
#
# Usage:
#   lint-commit-message.sh <path-to-message-file>                 # the hook passes $1
#   git log -1 --format=%B <sha> | lint-commit-message.sh --ci -   # CI, per commit
#
# Options:
#   --ci   reject autosquash placeholders (fixup!/squash!/amend!) instead of skipping them.
#
# Exit status: 0 = conforming (or exempt), 1 = violations found, 2 = usage error.
#
# Only the header is validated, plus the spelling of a breaking-change footer. Bodies are
# prose and are left alone. Whether the message is written in English is not checked.
# Tests: tools/commit-lint/test-lint-commit-message.sh.

set -u

TYPES='feat|fix|build|chore|ci|docs|perf|refactor|revert|style|test'
TYPES_HUMAN='feat, fix, build, chore, ci, docs, perf, refactor, revert, style, test'
SCOPE='[a-z0-9][a-z0-9-]*'
MAX=72

strict=0
case "${1:-}" in
  --ci) strict=1; shift ;;
  *) ;; # any other first argument is the message file or '-'
esac

if [ "$#" -lt 1 ] || [ "$1" = "-" ]; then
  msg="$(cat)"
elif [ -f "$1" ]; then
  msg="$(cat "$1")"
else
  printf 'commit-lint: message file not found: %s\n' "$1" >&2
  exit 2
fi

# Strip what git itself strips: the scissors block (verbose commits) and comment lines.
msg="$(printf '%s\n' "$msg" | sed -E -e '/^# -+ >8 -+$/,$d' -e '/^#/d')"
subject="$(printf '%s\n' "$msg" | sed -n '1p' | sed 's/[[:space:]]*$//')"

# Merge commits carry a git/GitHub-generated message.
case "$subject" in
  'Merge '*) exit 0 ;;
  *) ;;
esac

# Autosquash placeholders are rewritten by `git rebase --autosquash`: allowed locally,
# rejected in CI so they cannot reach main unsquashed.
case "$subject" in
  'fixup! '*|'squash! '*|'amend! '*)
    if [ "$strict" = 0 ]; then
      exit 0
    fi
    printf 'commit-lint: autosquash placeholder must be squashed away before merge: %s\n' "$subject" >&2
    exit 1
    ;;
  *) ;;
esac

errors=0
errmsgs=''
err() {
  errmsgs="${errmsgs}  - ${1}
"
  errors=$((errors + 1))
}

if [ -z "$subject" ]; then
  err "the commit message is empty"
else
  len=${#subject}
  if [ "$len" -gt "$MAX" ]; then
    err "the header is ${len} characters; keep it within ${MAX}"
  fi

  # <type>[(<scope>[,<scope>...])][!]: <lowercase description>
  if ! printf '%s' "$subject" | grep -Eq "^(${TYPES})(\(${SCOPE}(,${SCOPE})*\))?!?: [a-z]"; then
    # The specific diagnostics below explain the usual mistakes; the generic error at the
    # end guarantees a rejection when none of them applies.
    errors_before=$errors
    has_colon=1
    if ! printf '%s' "$subject" | grep -Eq '^[^:]+: .'; then
      has_colon=0
      err "expected '<type>[(scope)][!]: <description>' — no ': ' after the type"
    fi
    typ="$(printf '%s' "$subject" | sed -E 's/^([a-zA-Z]+).*/\1/')"
    if ! printf '%s' "$typ" | grep -Eq "^(${TYPES})$"; then
      err "unknown or malformed type '${typ}' — use one of: ${TYPES_HUMAN}"
    fi
    if printf '%s' "$subject" | grep -Eq '^[a-zA-Z]+\(' &&
      ! printf '%s' "$subject" | grep -Eq "^[a-zA-Z]+\(${SCOPE}(,${SCOPE})*\)"; then
      err "a scope is lowercase words separated by commas with no space, e.g. '(cli)' or '(cli,report)'"
    fi
    if [ "$has_colon" = 1 ]; then
      desc_start="$(printf '%s' "$subject" | sed -E 's/^[^:]*: ?//' | cut -c1)"
      case "$desc_start" in
        [A-Z]) err "the description must start with a lowercase letter (imperative: 'add', not 'Add'/'Added')" ;;
        *) ;;
      esac
    fi
    if [ "$errors" = "$errors_before" ]; then
      err "expected '<type>[(scope)][!]: <description>', with the description starting with a lowercase letter"
    fi
  fi

  case "$subject" in
    *.) err "the header must not end with a period" ;;
    *) ;;
  esac
fi

nlines="$(printf '%s\n' "$msg" | awk 'END { print NR }')"
line2="$(printf '%s\n' "$msg" | sed -n '2p')"
if [ "$nlines" -ge 2 ] && [ -n "$line2" ]; then
  err "leave a blank line between the header and the body"
fi

# A breaking change is signalled by a '!' in the header, a 'BREAKING CHANGE:' footer, or both;
# 'BREAKING-CHANGE:' is a synonym. Only misspelled footer tokens are rejected.
if printf '%s\n' "$msg" | grep -Eq '^(BREAKING_CHANGE|[Bb]reaking[ _-][Cc]hange):'; then
  err "the breaking-change footer must read 'BREAKING CHANGE:' or 'BREAKING-CHANGE:', in uppercase"
fi

if [ "$errors" -gt 0 ]; then
  printf 'commit-lint: this message does not follow Conventional Commits\n\n%s\n  subject: %s\n' "$errmsgs" "$subject" >&2
  exit 1
fi
exit 0
