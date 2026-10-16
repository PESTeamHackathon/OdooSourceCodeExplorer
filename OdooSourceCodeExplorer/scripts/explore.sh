#!/bin/bash
# Isolated hackathon experiment. No retrieval services, indexing, or web changes.
set +x
set -euo pipefail
cd "$(dirname "$0")/.."
env_file="${BUSINESSCHAT_ENV_FILE:-.env.local}"
if [ -f "$env_file" ]; then
  # Trusted developer configuration, never a file from the analyzed Odoo checkout.
  set -a
  source "$env_file"
  set +a
fi
if [ -z "${SourceRepository__Path:-${BUSINESSCHAT_SourceRepository__Path:-}}" ]; then
  printf 'Set SourceRepository__Path in .env.local or the environment.\n' >&2
  exit 1
fi
question="${1:-What happens when I cancel an unlocked sales order with draft and posted invoices, plus completed and unfinished deliveries?}"
export BUSINESSCHAT_Copilot__Model="${Copilot__Model:-${BUSINESSCHAT_Copilot__Model:-}}"
exec dotnet run --project Chat.Indexer --no-build --no-restore -- explore \
  --source "${SourceRepository__Path:-$BUSINESSCHAT_SourceRepository__Path}" --question "$question"
