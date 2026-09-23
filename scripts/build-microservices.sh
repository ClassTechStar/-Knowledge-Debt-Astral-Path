#!/usr/bin/env bash
# §19.11：构建十个扩展服务镜像（仓库根执行）
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
TAG="${1:-1.3.0}"
SERVICES=(
  concept-diffusion-svc exam-impact-svc peer-cohort-svc study-group-svc
  micro-lesson-svc learning-velocity-svc spaced-review-svc
  prerequisite-simulator-svc knowledge-forecast-svc lab-bench-svc
)
for s in "${SERVICES[@]}"; do
  echo "==> build astralpath/${s}:${TAG}"
  docker build -f services/Dockerfile.service --build-arg SERVICE="$s" \
    -t "astralpath/${s}:${TAG}" .
done
echo "OK: ${#SERVICES[@]} images @ ${TAG}"
