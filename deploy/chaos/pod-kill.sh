#!/usr/bin/env bash
# 混沌演练：随机杀掉一个服务 Pod（§24 韧性）
# 预期：Service 继续可用（readiness 摘除故障实例，PDB 保证 minAvailable），错误率短时上升后恢复。
set -euo pipefail
NS="${NS:-astralpath}"
SVC="${1:?用法: pod-kill.sh <service-name>}"
echo "[chaos] 目标: $NS/$SVC"
kubectl -n "$NS" get pods -l "app=$SVC" --field-selector=status.phase=Running -o name | head -3
POD="$(kubectl -n "$NS" get pods -l "app=$SVC" --field-selector=status.phase=Running -o name | shuf -n 1)"
[ -z "$POD" ] && { echo "[chaos] 没有运行中的 Pod，跳过"; exit 1; }
echo "[chaos] 删除 $POD"
kubectl -n "$NS" delete "$POD" --grace-period=10
echo "[chaos] 等待 30s 观察恢复"
sleep 30
kubectl -n "$NS" get pods -l "app=$SVC"
echo "[chaos] 判定：应恢复为 2/2 Ready"
