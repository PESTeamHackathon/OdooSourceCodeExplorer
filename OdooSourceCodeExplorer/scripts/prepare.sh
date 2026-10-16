#!/bin/bash
set +x
source "$(dirname "$0")/common.sh"
load_configuration
need git
[ -d "${SourceRepository__Path:-}" ] || fail 'Configure SourceRepository__Path to the separate Odoo Git checkout.'
[ -n "${APPLICATION_VERSION:-}" ] || fail 'Set APPLICATION_VERSION in .env.local.'
declare -p INDEX_PATHS >/dev/null 2>&1 || fail 'Configure INDEX_PATHS as a Bash array in .env.local.'
[ "${#INDEX_PATHS[@]}" -gt 0 ] || fail 'Configure a nonempty INDEX_PATHS Bash array in .env.local.'
case "$DEMO_ROOT/" in "${SourceRepository__Path%/}/"*) fail 'DEMO_ROOT must be outside the source checkout.';; esac
mkdir "$DEMO_ROOT/prepare.lock" 2>/dev/null || fail 'Preparation is already running (or prepare.lock is stale).'
prepare_lock_owned=1
progress 'Building solution'
dotnet build OdooSourceCodeExplorer.sln --disable-build-servers
start_services
check_model prepare
config="$DEMO_ROOT/indexing-config.json"
printf '%s\n' "${INDEX_PATHS[@]}" | jq -Rn '[inputs] | {Indexing:{IncludePaths:.,MaxFileBytes:2097152,MaxChunkCharacters:12000},Retrieval:{BatchSize:8,WindowCharacters:1800,WindowOverlap:200}}' >"$config"
fingerprint() {
  {
    printf '%s\n' "$SourceRepository__Path" "$APPLICATION_VERSION"
    cat "$config" "$ROOT/Chat.Indexer/appsettings.json"
    shasum -a 256 "$ROOT/Chat.Indexer/bin/Debug/net10.0/Chat.Indexer.dll" "$ROOT/Chat.Indexer/bin/Debug/net10.0/Chat.App.dll"
    git -C "$SourceRepository__Path" rev-parse HEAD
    # Includes staged/unstaged edits and module metadata, without printing source.
    git -C "$SourceRepository__Path" diff --no-ext-diff --no-textconv --binary HEAD --
    git -C "$SourceRepository__Path" status --porcelain=v1 --untracked-files=normal
  } | shasum -a 256 | cut -d ' ' -f 1
}
before="$(fingerprint)"
source_index=''
search_index=''
if [ -f "$STATE" ] && [ "$(jq -r '.sourceFingerprint // empty' "$STATE")" == "$before" ]; then
  source_index="$(jq -r '.sourceIndex // empty' "$STATE")"
  search_index="$(jq -r '.searchIndex // empty' "$STATE")"
fi
if [ -z "$source_index" ] || [ ! -f "$source_index/manifest.json" ] || [ ! -f "$source_index/chunks.jsonl" ] ||
   [ "$(shasum -a 256 "$source_index/chunks.jsonl" 2>/dev/null | cut -d ' ' -f 1)" != "$(jq -r '.chunksSha256' "$source_index/manifest.json" 2>/dev/null)" ]; then
  progress 'Indexing source code'
  output="$(indexer --config "$config" --source "$SourceRepository__Path" --version "$APPLICATION_VERSION" --output "$DEMO_ROOT/source-indexes")"
  source_index="$(printf '%s\n' "$output" | sed -n 's/^Index: //p')"
  [ -f "$source_index/manifest.json" ] || fail 'Indexer did not produce a completed source generation.'
  search_index=''
else progress 'Reusing unchanged source index'; fi
search_fingerprint="$( { printf '%s\n' "$before" "$Retrieval__OllamaUrl" "$Retrieval__QdrantUrl" "$Retrieval__EmbeddingModel"; shasum -a 256 "$ROOT/Chat.Indexer/bin/Debug/net10.0/Chat.Infrastructure.dll"; } | shasum -a 256 | cut -d ' ' -f 1)"
if [ -n "$search_index" ] && [ "$(jq -r '.searchFingerprint // empty' "$STATE")" == "$search_fingerprint" ] && validate_retrieval "$search_index" "$APPLICATION_VERSION"; then
  progress 'Reusing validated searchable index (no document embeddings generated)'
else
  progress 'Generating embeddings and building searchable index'
  output="$(indexer build-search --config "$config" --artifact "$source_index" --search-output "$DEMO_ROOT/search-indexes" | tee /dev/stderr)"
  search_index="$(printf '%s\n' "$output" | sed -n 's/^Search index: //p')"
  [ -f "$search_index/search-manifest.json" ] || fail 'No completed search generation was produced.'
  validate_retrieval "$search_index" "$APPLICATION_VERSION" || fail 'Retrieval validation failed; previous prepared state was retained.'
fi
[ "$before" == "$(fingerprint)" ] || fail 'Source changed during preparation. Run preparation again.'
# Publish local selection only after the new generation passes validation.
jq -n --arg source "$source_index" --arg search "$search_index" --arg version "$APPLICATION_VERSION" \
  --arg sourceFingerprint "$before" --arg searchFingerprint "$search_fingerprint" \
  --slurpfile manifest "$search_index/search-manifest.json" \
  '{schemaVersion:1,sourceIndex:$source,searchIndex:$search,applicationVersion:$version,sourceFingerprint:$sourceFingerprint,searchFingerprint:$searchFingerprint,gitCommit:$manifest[0].info.gitCommit,workingTreeDirty:$manifest[0].info.workingTreeDirty}' >"$STATE.tmp"
mv "$STATE.tmp" "$STATE"
progress 'Preparation complete. Start the chatbot with ./scripts/run.sh'
