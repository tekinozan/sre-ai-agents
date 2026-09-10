#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
expected="$repository_root/deploy/k8s/generated-development.yaml"
chart_path="deploy/helm/sre-ai-agent"
values_path="deploy/helm/sre-ai-agent/values.development.yaml"

resolve_helm() {
  if [[ -n "${HELM_BIN:-}" ]]; then
    if command -v "$HELM_BIN" >/dev/null 2>&1; then
      echo "$HELM_BIN"
      return 0
    fi
    echo "Specified HELM_BIN '$HELM_BIN' not found." >&2
    return 1
  fi

  if command -v helm >/dev/null 2>&1; then
    echo "helm"
    return 0
  fi

  if command -v docker >/dev/null 2>&1; then
    echo "docker-helm"
    return 0
  fi

  echo "Neither helm nor docker is available to render Helm templates." >&2
  return 1
}

actual="$(mktemp)"
trap 'rm -f "$actual"' EXIT

helm_cmd="$(resolve_helm)"

if [[ "$helm_cmd" == "docker-helm" ]]; then
  docker run --rm \
    -v "$repository_root:/repo" \
    -w /repo \
    alpine/helm:3.17.1 template sre-ai-agent "$chart_path" \
      --namespace sre-ai-agent \
      -f "$values_path" \
      > "$actual"
else
  (cd "$repository_root" && "$helm_cmd" template sre-ai-agent "$chart_path" \
    --namespace sre-ai-agent \
    -f "$values_path") \
    > "$actual"
fi

chmod --reference="$expected" "$actual" 2>/dev/null || true
git diff --no-index --ignore-cr-at-eol -- "$expected" "$actual"