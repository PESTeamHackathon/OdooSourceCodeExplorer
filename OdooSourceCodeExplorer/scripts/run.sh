#!/bin/bash
set +x
source "$(dirname "$0")/common.sh"
load_configuration
progress 'Checking authentication configuration'
# Presence only: account access, expiry and quota are checked by Copilot on requests.
token="${COPILOT_GITHUB_TOKEN:-${GH_TOKEN:-${GITHUB_TOKEN:-}}}"
[[ "$token" =~ [^[:space:]] ]] || fail 'Set COPILOT_GITHUB_TOKEN in .env.local (Copilot Requests permission). Never print its value.'
[[ ! "$token" =~ [[:space:]] ]] || fail 'Authentication token contains whitespace. Correct .env.local without printing the token.'
unset token
require_build
[ -f "$STATE" ] || fail 'Prepared knowledge missing. Run ./scripts/prepare.sh first.'
jq -e '.schemaVersion == 1 and (.searchIndex | type == "string") and (.applicationVersion | type == "string" and length > 0)' "$STATE" >/dev/null || fail 'Prepared state is invalid. Run ./scripts/prepare.sh first.'
export Retrieval__IndexPath="$(jq -r '.searchIndex' "$STATE")"
export KnowledgeChat__ExpectedApplicationVersion="$(jq -r '.applicationVersion' "$STATE")"
[ -f "$Retrieval__IndexPath/search-manifest.json" ] && [ -f "$Retrieval__IndexPath/knowledge.sqlite" ] || fail 'Prepared index files missing. Run ./scripts/prepare.sh first.'
[ "${APPLICATION_VERSION:-$KnowledgeChat__ExpectedApplicationVersion}" == "$KnowledgeChat__ExpectedApplicationVersion" ] || fail 'Configured application version differs from prepared knowledge. Run ./scripts/prepare.sh first.'
start_services
check_model runtime
validate_retrieval "$Retrieval__IndexPath" "$KnowledgeChat__ExpectedApplicationVersion" || fail 'Prepared knowledge is unavailable or incompatible. Restore matching services/model or run ./scripts/prepare.sh first.'
# No source lookup, build, document embedding, or search-index creation in this workflow.
progress 'Starting web application'
WEB_URL="${WEB_URL:-http://localhost:5112}"
[[ "$WEB_URL" =~ ^http://(localhost|127\.0\.0\.1):[0-9]+$ ]] || fail 'WEB_URL must be a local HTTP URL with an explicit port.'
exec dotnet run --project "$ROOT/Chat.Web" --no-build --no-restore --launch-profile http -- --urls "$WEB_URL"
