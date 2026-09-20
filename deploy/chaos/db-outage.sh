#!/usr/bin/env bash
# 混沌演练：模拟数据库不可用（缩容 postgres 到 0）
# 预期：服务 /health/ready 转为非 ready，但 /health/live 仍存活（不被误重启）；业务接口返回明确错误而非崩溃。
set -euo pipefail
NS="${NS:-astralpath}"
echo "[chaos] 将 postgres 缩容到 0"
kubectl -n "$NS" scale statefulset/postgres --replicas=0
sleep 20
echo "[chaos] 观察服务健康态（live 应 200，ready 应非 200）"
kubectl -n "$NS" get pods -l app=concept-diffusion-svc
echo "[chaos] 恢复"
kubectl -n "$NS" scale statefulset/postgres --replicas=1
kubectl -n "$NS" rollout status statefulset/postgres --timeout=180s
echo "[chaos] 恢复完成"
