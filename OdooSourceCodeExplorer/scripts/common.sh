#!/bin/bash
# Shared by prepare.sh and run.sh; compatible with macOS Bash 3.2.
set +x
set -euo pipefail
umask 077
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
fail() { printf 'Error: %s\n' "$*" >&2; exit 1; }
progress() { printf '\n==> %s\n' "$*"; }
need() { command -v "$1" >/dev/null 2>&1 || fail "Install prerequisite: $1"; }
cleanup() {
  if [ "${service_lock_owned:-0}" == 1 ]; then rmdir "$DEMO_ROOT/service-start.lock" 2>/dev/null || true; fi
  if [ "${prepare_lock_owned:-0}" == 1 ]; then rmdir "$DEMO_ROOT/prepare.lock" 2>/dev/null || true; fi
}
load_configuration() {
  local configuration_mode="${1:-rag}"
  local env_file="${BUSINESSCHAT_ENV_FILE:-$ROOT/.env.local}"
  [ -f "$env_file" ] || fail 'Copy .env.example to .env.local and configure it first.'
  # This is deliberately a trusted, user-owned Bash file, never repository content from Odoo.
  set -a
  source "$env_file"
  set +a
  export KnowledgeChat__UseAgenticExploration="${KnowledgeChat__UseAgenticExploration:-false}"
  case "$(printf '%s' "$KnowledgeChat__UseAgenticExploration" | tr '[:upper:]' '[:lower:]')" in
    true) KnowledgeChat__UseAgenticExploration=true;;
    false) KnowledgeChat__UseAgenticExploration=false;;
    *) fail 'KnowledgeChat__UseAgenticExploration must be true or false.';;
  esac
  need dotnet
  if [ "$configuration_mode" == runtime ] && [ "$KnowledgeChat__UseAgenticExploration" == true ]; then
    [[ "$(dotnet --version)" == 10.* ]] || fail '.NET 10 SDK is required.'
    return
  fi
  for prerequisite in curl jq shasum; do need "$prerequisite"; done
  [[ "$(dotnet --version)" == 10.* ]] || fail '.NET 10 SDK is required.'
  DEMO_ROOT="${DEMO_ROOT:-$HOME/BusinessChatDemo}"
  [[ "$DEMO_ROOT" == /* ]] || fail 'DEMO_ROOT must be absolute.'
  mkdir -p "$DEMO_ROOT"
  DEMO_ROOT="$(cd "$DEMO_ROOT" && pwd -P)"
  local chatbot_repository="$ROOT"
  while [ "$chatbot_repository" != / ] && [ ! -e "$chatbot_repository/.git" ]; do chatbot_repository="$(dirname "$chatbot_repository")"; done
  [ "$chatbot_repository" != / ] || chatbot_repository="$ROOT"
  case "$DEMO_ROOT/" in "$chatbot_repository/"*) fail 'DEMO_ROOT must be outside the chatbot repository.';; esac
  mkdir -p "$DEMO_ROOT/logs"
  trap cleanup EXIT
  STATE="$DEMO_ROOT/prepared.json"
  Retrieval__OllamaUrl="${Retrieval__OllamaUrl:-http://localhost:11434/}"
  Retrieval__QdrantUrl="${Retrieval__QdrantUrl:-http://localhost:6333/}"
  Retrieval__EmbeddingModel="${Retrieval__EmbeddingModel:-nomic-embed-text:v1.5}"
  # Service autostart is intentionally local-only. No credentials in URL/logged arguments.
  [[ "$Retrieval__OllamaUrl" =~ ^http://(localhost|127\.0\.0\.1):[0-9]+/?$ ]] || fail 'Use a local Ollama HTTP URL with an explicit port.'
  [[ "$Retrieval__QdrantUrl" =~ ^http://(localhost|127\.0\.0\.1):[0-9]+/?$ ]] || fail 'Use a local Qdrant HTTP URL with an explicit port.'
  export Retrieval__OllamaUrl Retrieval__QdrantUrl Retrieval__EmbeddingModel
  # The console's environment prefix differs from the web host's.
  export BUSINESSCHAT_Retrieval__OllamaUrl="$Retrieval__OllamaUrl"
  export BUSINESSCHAT_Retrieval__QdrantUrl="$Retrieval__QdrantUrl"
  export BUSINESSCHAT_Retrieval__EmbeddingModel="$Retrieval__EmbeddingModel"
  export BUSINESSCHAT_Retrieval__QdrantApiKey="${Retrieval__QdrantApiKey:-}"
  # Avoid accidental console configuration overrides not represented by these scripts.
  for key in $(compgen -v BUSINESSCHAT_); do
    case "$key" in BUSINESSCHAT_Retrieval__OllamaUrl|BUSINESSCHAT_Retrieval__QdrantUrl|BUSINESSCHAT_Retrieval__EmbeddingModel|BUSINESSCHAT_Retrieval__QdrantApiKey) ;; *) unset "$key";; esac
  done
}
healthy() { curl --silent --fail --max-time 3 "$1" >/dev/null 2>&1; }
wait_service() {
  local count=0
  until healthy "$1"; do
    count=$((count + 1)); [ "$count" -lt 60 ] || fail "$2 did not become ready. Check private service logs or Docker Desktop."
    sleep 1
  done
}
start_services() {
  # Serialize service startup between preparation and runtime, without holding a lock during chat.
  local count=0
  until mkdir "$DEMO_ROOT/service-start.lock" 2>/dev/null; do
    count=$((count + 1)); [ "$count" -lt 65 ] || fail 'Service startup is locked. If no script is running, remove the stale service-start.lock directory.'
    sleep 1
  done
  service_lock_owned=1
  progress 'Checking Ollama'
  if ! healthy "${Retrieval__OllamaUrl%/}/api/tags"; then
    need ollama
    progress 'Starting Ollama'
    OLLAMA_HOST="${Retrieval__OllamaUrl%/}" nohup ollama serve >"$DEMO_ROOT/logs/ollama.log" 2>&1 </dev/null &
    wait_service "${Retrieval__OllamaUrl%/}/api/tags" Ollama
  fi
  progress 'Checking Qdrant'
  if ! healthy "${Retrieval__QdrantUrl%/}/healthz"; then
    progress 'Starting Qdrant'
    if [ -n "${QDRANT_BINARY:-}" ]; then
      [[ "$QDRANT_BINARY" == /* && -x "$QDRANT_BINARY" && "${QDRANT_CONFIG:-}" == /* && -f "${QDRANT_CONFIG:-}" && "${QDRANT_WORKDIR:-}" == /* && -d "${QDRANT_WORKDIR:-}" ]] || fail 'Configure absolute QDRANT_BINARY, QDRANT_CONFIG and QDRANT_WORKDIR for native startup.'
      (cd "$QDRANT_WORKDIR"; nohup "$QDRANT_BINARY" --config-path "$QDRANT_CONFIG" >"$DEMO_ROOT/logs/qdrant.log" 2>&1 </dev/null &)
    else
      need docker
      [[ "$Retrieval__QdrantUrl" == http://localhost:6333* || "$Retrieval__QdrantUrl" == http://127.0.0.1:6333* ]] || fail 'Docker Compose binds Qdrant to port 6333; configure native startup for another port.'
      docker compose up -d qdrant || fail 'Start Docker Desktop or configure native Qdrant.'
    fi
    wait_service "${Retrieval__QdrantUrl%/}/healthz" Qdrant
  fi
  rmdir "$DEMO_ROOT/service-start.lock"
  service_lock_owned=0
}
check_model() {
  progress 'Checking embedding model'
  if ! curl --silent --fail --max-time 10 "${Retrieval__OllamaUrl%/}/api/tags" | jq -e --arg model "$Retrieval__EmbeddingModel" '.models | any(.name == $model)' >/dev/null; then
    if [ "$1" == prepare ] && [ "${PULL_EMBEDDING_MODEL:-1}" == 1 ]; then
      need ollama
      progress 'Downloading embedding model'
      OLLAMA_HOST="$Retrieval__OllamaUrl" ollama pull "$Retrieval__EmbeddingModel"
    else fail 'Embedding model unavailable. Run ./scripts/prepare.sh first.'; fi
  fi
}
indexer() { dotnet "$ROOT/Chat.Indexer/bin/Debug/net10.0/Chat.Indexer.dll" "$@"; }
validate_retrieval() {
  progress 'Validating retrieval'
  # JSON contains source evidence: pipe directly to a boolean check, never print or log it.
  indexer search --index "$1" --expected-version "$2" \
    --question "${RETRIEVAL_TEST_QUESTION:-Can I cancel a locked sales order?}" --limit 5 --format json 2>/dev/null |
    jq -e '.hits | length > 0' >/dev/null
}
require_build() {
  [ -f "$ROOT/Chat.Indexer/bin/Debug/net10.0/Chat.Indexer.dll" ] && [ -f "$ROOT/Chat.Web/bin/Debug/net10.0/Chat.Web.dll" ] || fail 'Built applications missing. Run ./scripts/prepare.sh first.'
}
