#!/bin/bash
# Offline boundary tests. Build the solution first; no services or real credentials needed.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
test_root="$(mktemp -d "${TMPDIR:-/tmp}/businesschat-script-tests.XXXXXX")"
trap 'rm -rf "$test_root"' EXIT
mkdir "$test_root/bin" "$test_root/data" "$test_root/index"
cat >"$test_root/bin/dotnet" <<'EOF'
#!/bin/bash
set -eu
if [ "$1" == --version ]; then echo 10.0.100; exit; fi
if [[ "$1" == */Chat.Indexer.dll ]] && [ "$2" == search ]; then
  echo search >>"$TEST_CALLS"
  if [ "${TEST_BAD_INDEX:-0}" == 1 ]; then exit 1; fi
  if [ "${TEST_NO_HITS:-0}" == 1 ]; then echo '{"hits":[]}'; else echo '{"hits":[{}]}'; fi
elif [ "$1" == run ] && [[ " $* " == *' --no-build '* ]] && [[ " $* " == *' --no-restore '* ]]; then
  [ "$Retrieval__IndexPath" == "$TEST_INDEX" ]
  [ "$KnowledgeChat__ExpectedApplicationVersion" == test-version ]
  echo web >>"$TEST_CALLS"
else
  echo 'Unexpected build/indexing/document embedding operation' >&2; exit 99
fi
EOF
cat >"$test_root/bin/curl" <<'EOF'
#!/bin/bash
echo '{"models":[{"name":"nomic-embed-text:v1.5"}]}'
EOF
chmod +x "$test_root/bin/"*
export PATH="$test_root/bin:$PATH" TEST_CALLS="$test_root/calls" TEST_INDEX="$test_root/index"
export BUSINESSCHAT_ENV_FILE="$test_root/test.env"
cat >"$BUSINESSCHAT_ENV_FILE" <<EOF
DEMO_ROOT='$test_root/data'
SourceRepository__Path='/nonexistent/source-must-not-be-read'
COPILOT_GITHUB_TOKEN=''
GH_TOKEN=''
GITHUB_TOKEN=''
EOF
expect_failure() {
  if "$ROOT/scripts/run.sh" >"$test_root/output" 2>&1; then echo 'Expected rejection' >&2; exit 1; fi
  grep -q "$1" "$test_root/output"
}
expect_failure 'Set COPILOT_GITHUB_TOKEN'
echo "COPILOT_GITHUB_TOKEN='test-secret-sentinel-not-a-real-token'" >>"$BUSINESSCHAT_ENV_FILE"
expect_failure 'Run ./scripts/prepare.sh first'
touch "$TEST_INDEX/search-manifest.json" "$TEST_INDEX/knowledge.sqlite"
jq -n --arg path "$TEST_INDEX" '{schemaVersion:1,searchIndex:$path,applicationVersion:"test-version"}' >"$test_root/data/prepared.json"
TEST_BAD_INDEX=1 expect_failure 'unavailable or incompatible'
TEST_NO_HITS=1 expect_failure 'unavailable or incompatible'
echo "APPLICATION_VERSION='wrong-version'" >>"$BUSINESSCHAT_ENV_FILE"
expect_failure 'differs from prepared knowledge'
echo "APPLICATION_VERSION='test-version'" >>"$BUSINESSCHAT_ENV_FILE"
"$ROOT/scripts/run.sh" >"$test_root/output" 2>&1
grep -q '^web$' "$TEST_CALLS"
if grep -q 'test-secret-sentinel' "$test_root/output"; then echo 'Secret leaked' >&2; exit 1; fi
if [ -d "$test_root/data/service-start.lock" ]; then echo 'Service lock leaked' >&2; exit 1; fi
# Simulate stopped local services, then ensure repeat startup reuses them.
export TEST_SERVICE_ROOT="$test_root"
cat >"$test_root/bin/curl" <<'EOF'
#!/bin/bash
if [[ " $* " == *'/api/tags'* ]]; then
  [ -f "$TEST_SERVICE_ROOT/ollama-ready" ] || exit 22
  echo '{"models":[{"name":"nomic-embed-text:v1.5"}]}'
else [ -f "$TEST_SERVICE_ROOT/qdrant-ready" ] || exit 22; fi
EOF
cat >"$test_root/bin/ollama" <<'EOF'
#!/bin/bash
echo ollama >>"$TEST_CALLS"
touch "$TEST_SERVICE_ROOT/ollama-ready"
EOF
cat >"$test_root/bin/qdrant" <<'EOF'
#!/bin/bash
echo qdrant >>"$TEST_CALLS"
touch "$TEST_SERVICE_ROOT/qdrant-ready"
EOF
chmod +x "$test_root/bin/"*
touch "$test_root/qdrant.yaml"
cat >>"$BUSINESSCHAT_ENV_FILE" <<EOF
QDRANT_BINARY='$test_root/bin/qdrant'
QDRANT_CONFIG='$test_root/qdrant.yaml'
QDRANT_WORKDIR='$test_root/data'
EOF
"$ROOT/scripts/run.sh" >"$test_root/output" 2>&1
"$ROOT/scripts/run.sh" >"$test_root/output" 2>&1
[ "$(grep -c '^ollama$' "$TEST_CALLS")" == 1 ]
[ "$(grep -c '^qdrant$' "$TEST_CALLS")" == 1 ]
printf 'Passed: authentication, missing state, invalid/empty retrieval, version mismatch, source-independent runtime, no runtime indexing/build, secret-safe output, service autostart and reuse.\n'
