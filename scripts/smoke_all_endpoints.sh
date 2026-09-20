#!/usr/bin/env bash
# 全量接口端到端验证（动态取真实 ID 与凭据，避免假数据造成的误判）
set -u
BASE="http://127.0.0.1:5190"
PY="C:/Users/18948/.workbuddy/binaries/python/versions/3.13.12/python.exe"
WORK="/tmp/astralpath_smoke"
mkdir -p "$WORK"
PASS=0; FAIL=0; FAILED_LIST=""

# 注意：本机 curl 为原生 Windows 二进制，无法识别 MSYS 的 /tmp 路径，
#       故不用 curl -o 写文件，而是用命令替换取回响应体后由 bash 重定向落盘。
capture() { # method path [json] -> 设置全局 CODE / 写 WORK/body.json
  local m="$1" p="$2" body="${3:-}" resp code bodytext
  if [ -n "$body" ]; then
    resp=$(curl -s -w "\n%{http_code}" -X "$m" "$BASE$p" \
           -H "Content-Type: application/json" ${TOKEN:+-H "Authorization: Bearer $TOKEN"} \
           -d "$body" --max-time 25)
  else
    resp=$(curl -s -w "\n%{http_code}" -X "$m" "$BASE$p" \
           ${TOKEN:+-H "Authorization: Bearer $TOKEN"} --max-time 25)
  fi
  code="${resp##*$'\n'}"
  bodytext="${resp%$'\n'*}"
  printf '%s' "$bodytext" > "$WORK/body.json"
  CODE="$code"
}

probe() { # method path [json] [expect]
  local m="$1" p="$2" body="${3:-}" expect="${4:-}" code
  capture "$m" "$p" "$body"
  code="$CODE"
  if [ -n "$expect" ]; then
    if [ "$code" = "$expect" ]; then PASS=$((PASS+1)); printf "  [OK]   %-3s %s\n" "$code" "$m $p"
    else FAIL=$((FAIL+1)); printf "  [FAIL] %-3s %s (期望 %s)\n" "$code" "$m $p" "$expect"; FAILED_LIST="$FAILED_LIST\n  $m $p -> $code 期望 $expect"; fi
  else
    case "$code" in 2*|3*) PASS=$((PASS+1)); printf "  [OK]   %-3s %s\n" "$code" "$m $p";;
    *) FAIL=$((FAIL+1)); printf "  [FAIL] %-3s %s\n" "$code" "$m $p"; FAILED_LIST="$FAILED_LIST\n  $m $p -> $code";; esac
  fi
}

# 从上次响应的 data 层取字段（支持 a.b.0.c 形式）
# 经 stdin 传 JSON（Python 为原生 Windows 二进制，无法打开 MSYS /tmp 路径）
data_get() { $PY -c "
import json,sys
try:
    d=json.load(sys.stdin)
    if isinstance(d,dict) and 'data' in d: d=d['data']
    for k in '$1'.split('.'):
        d = d[int(k)] if k.lstrip('-').isdigit() else d[k]
    print(d)
except Exception:
    print('')
" < "$WORK/body.json"; }

echo "=== 1) 基础与元信息 ==="
probe GET /health/ready
probe GET /api/meta
probe GET /api/demo/students

echo "=== 2) 学生主链路 ==="
probe POST "/v1/students/demo-student-a/ingest/scores" '{"graphVersion":1,"source":"smoke","rows":[{"kpId":"K12","recentAcc":0.4,"sev":0.3,"selfConf":2}]}'
probe POST "/v1/students/demo-student-a/diagnose?graph_ver=1&top_n=10"
FROM_KP=$(data_get "topDebts.0.fromKp"); TO_KP=$(data_get "topDebts.0.toKp")
echo "     诊断真实债边：$FROM_KP -> $TO_KP"
probe GET  "/v1/students/demo-student-a/graph-view?graph_ver=1"
probe GET  "/v1/students/demo-student-a/mastery"
probe POST "/v1/students/demo-student-a/plans" '{"graphVersion":1}'
probe POST "/v1/students/demo-student-a/today" '{}'
QID=$(data_get "tasks.0.questionId")
echo "     今日任务真实题目：$QID"

echo "=== 3) 尝试 / 销账 / 教师端 / consent ==="
probe POST /v1/attempts "{\"studentId\":\"demo-student-a\",\"kpId\":\"$FROM_KP\",\"questionId\":\"${QID:-Q}\",\"correct\":true,\"selfConf\":4}"
probe POST /v1/debt-edges/sale-check "{\"studentId\":\"demo-student-a\",\"fromKp\":\"$FROM_KP\",\"toKp\":\"$TO_KP\"}"
probe GET  "/v1/teachers/demo-teacher/hotspots"
probe GET  "/v1/consents/demo-student-a"
probe POST "/v1/consents/demo-student-a/grant" '{"teacherId":"demo-teacher","purpose":"kb_read"}'
probe POST "/v1/consents/demo-student-a/revoke" '{"teacherId":"demo-teacher","purpose":"kb_read"}'

echo "=== 4) 图包与 What-if ==="
probe POST "/v1/graphs/accounting-v1/validate"
probe GET  "/v1/knowledge-graphs"
probe POST "/v1/what-if" "{\"studentId\":\"demo-student-a\",\"fromKp\":\"$FROM_KP\",\"toKp\":\"$TO_KP\"}"
probe POST "/v1/demo/advance" '{"days":1}'

echo "=== 5) 题库与教材 ==="
probe GET  "/v1/question-banks"
probe POST "/v1/materials/seed-samples"
probe POST "/v1/materials/parse-all"
probe GET  "/v1/materials"
MAT_ID=$(data_get "0.id")
echo "     材料 ID：$MAT_ID"
if [ -n "$MAT_ID" ]; then
  probe GET "/v1/materials/$MAT_ID"
  probe GET "/v1/materials/$MAT_ID/tasks"
  probe GET "/v1/materials/$MAT_ID/textbook-questions"
fi
probe GET  "/v1/materials/today-from-books"
if [ -n "$QID" ]; then probe GET "/v1/questions/$QID"; fi

echo "=== 6) 账户（真实演示凭据 demo@astralpath.local / demo123456）==="
probe POST "/api/v1/auth/register" '{"email":"smoke@astralpath.local","password":"smoke123456","displayName":"冒烟账号"}'
probe POST "/api/v1/auth/sessions" '{"email":"demo@astralpath.local","password":"demo123456"}'
TOKEN=$(data_get "accessToken")
echo "     取得令牌：${TOKEN:0:12}…"
probe GET  "/api/v1/auth/me"
probe PUT  "/api/v1/auth/profile" '{"displayName":"演示学习者"}'
probe POST "/api/v1/auth/material-today" '{}'
probe POST "/api/v1/auth/logout" '{}'

echo "=== 7) §44 智能体 ==="
probe POST "/v1/agent/turns" '{"userId":"demo-student-a","role":"student","utterance":"帮我看看我线代为什么总错"}'
SID=$(data_get "sessionId")
probe POST "/v1/agent/turns" '{"userId":"demo-student-a","role":"student","utterance":"我不想活了"}'
probe GET  "/v1/agent/tools?role=student"
probe GET  "/v1/agent/tools?role=hacker" "" 400
if [ -n "$SID" ]; then
  probe GET "/v1/agent/sessions/$SID"
  probe DELETE "/v1/agent/sessions/$SID" "" 204
  probe GET "/v1/agent/sessions/$SID" "" 404
fi

echo "=== 8) §45 知识库 ==="
probe POST "/v1/kb/documents" '{"title":"冒烟文档","ownerUserId":"demo-student-a","visibility":"private","courseCode":"LINALG","text":"冒烟正文","tags":["冒烟"]}'
DOC_ID=$(data_get "id")
if [ -n "$DOC_ID" ]; then
  probe GET   "/v1/kb/documents/$DOC_ID?userId=demo-student-a&role=student"
  probe GET   "/v1/kb/documents/$DOC_ID?userId=demo-student-b&role=student" "" 404
  probe PATCH "/v1/kb/documents/$DOC_ID?userId=demo-student-a&role=student" '{"title":"冒烟文档改名"}'
  probe PUT   "/v1/kb/documents/$DOC_ID/tags?userId=demo-student-a&role=student" '{"tags":["冒烟","改名"]}'
  probe POST  "/v1/kb/documents/$DOC_ID/versions" '{"text":"v1 正文","note":"初稿","actorId":"demo-student-a"}'
  probe GET   "/v1/kb/documents/$DOC_ID/versions?userId=demo-student-a&role=student"
  probe POST  "/v1/kb/documents/$DOC_ID/publish" '{"actorId":"demo-student-a"}'
  probe POST  "/v1/kb/documents/$DOC_ID/rollback" '{"version":"v1","actorId":"demo-student-a"}'
  probe POST  "/v1/kb/documents/$DOC_ID/archive?userId=demo-student-a&role=student"
fi
probe GET  "/v1/kb/tags?userId=demo-student-a&role=student"
probe POST "/v1/kb/search" '{"query":"冒烟","userId":"demo-student-a","role":"student"}'
# 以下 4 项原为显式 501 延后项，现已实现；此处校验入参缺失时的 400（完整流程见第 11 节）
probe POST "/v1/kb/uploads" "" 400
probe POST "/v1/kb/uploads/u-1/commit" "" 400
probe GET  "/v1/kb/chunks/c-1" "" 400
probe POST "/internal/v1/kb/import" "" 400

echo "=== 9) §46 用户画像 ==="
probe GET  "/v1/profile/demo-student-a"
probe GET  "/v1/profile/demo-student-a/features"
probe GET  "/v1/profile/demo-student-a/tags"
probe GET  "/v1/profile/demo-student-a/radar"
probe GET  "/v1/profile/demo-student-a/timeline"
probe GET  "/v1/profile/no-such-student/features" "" 404
probe POST "/v1/profile/demo-student-a/tags" '{"tag":"压力大","domain":"sensitive","weight":1.0}' 422
probe POST "/v1/profile/demo-student-a/tags" '{"tag":"节律型","domain":"pace","weight":0.9}'
probe POST "/v1/profile/demo-student-a/opt-out" '{}'

echo "=== 10) 模块自检与 CSR ==="
probe GET  "/v1/modules/status"
probe GET  "/v1/knowledge-graphs/accounting-v1/csr"
probe GET  "/v1/knowledge-graphs/no-such-pack/csr" "" 404

echo "=== 11) §19 扩展服务 ==="
probe POST "/v1/diffusion/simulate" '{"studentId":"demo-student-a","intervention":{"K02":20},"alpha":0.55,"depth":6}'
probe POST "/v1/exams/midterm-1/impact" '{"studentId":"demo-student-a","daysToExam":7,"dayBudgetMin":30}'
probe POST "/v1/exams/midterm-1/preexam-plan" '{"studentId":"demo-student-a","daysToExam":7,"dayBudgetMin":30}'
probe POST "/v1/cohorts/stats" '{"k":5,"scores":[60,70,55,82,66,71]}'
probe POST "/v1/cohorts/stats" '{"k":5,"scores":[60,70]}'
probe POST "/v1/study-groups/match" '{"size":3,"members":[{"studentId":"s1","tags":["线代","矩阵"]},{"studentId":"s2","tags":["概率","统计"]},{"studentId":"s3","tags":["线代","概率"]},{"studentId":"s4","tags":["编程","算法"]}]}'
probe POST "/v1/micro-lessons/assemble" '{"kpId":"K02","minutes":10,"resources":["教材 P32 例题 3"]}'
probe POST "/v1/velocity/fit" '{"series":[{"at":"2026-09-01","score":50},{"at":"2026-09-05","score":62},{"at":"2026-09-09","score":71}]}'
probe POST "/v1/spaced-review/schedule" '{"items":[{"kpId":"K02","lapses":0,"lastScore":90},{"kpId":"K03","lapses":2,"lastScore":45}]}'
probe POST "/v1/prereq-simulator/simulate" '{"studentId":"demo-student-a","targetKp":"K02","targetScore":75}'
probe POST "/v1/forecast/student" '{"studentId":"demo-student-a","daysAhead":14}'
probe POST "/v1/forecast/student" '{"studentId":"demo-student-a","daysAhead":60}'
probe POST "/internal/v1/lab/experiments" '{"name":"score 候选回归","candidateVersion":"score-v2"}'
probe POST "/internal/v1/lab/experiments/lab-1/run" '{"cases":[{"expected":1.0,"actual":1.0000001}]}'
probe GET  "/internal/v1/lab/experiments/lab-1/report"
probe POST "/internal/v1/lab/formula-versions" '{"version":"score-v2","proposer":"P2"}'
probe POST "/internal/v1/lab/formula-versions/score-v2/promote" '{"proposer":"P2","approver":"P1"}'
probe POST "/internal/v1/lab/formula-versions/score-v2/promote" '{"proposer":"P2","approver":"P2"}' 422
probe POST "/v1/kb/uploads" '{"title":"冒烟分片文档","ownerUserId":"demo-student-a","visibility":"private","courseCode":"LINALG","partCount":2}'
UPL=$(data_get "uploadId")
if [ -n "$UPL" ]; then
  probe POST "/v1/kb/uploads/$UPL/commit" '{"ownerUserId":"demo-student-a","parts":["第一部分正文。","第二部分正文。"]}'
  DOC2=$(data_get "doc.id")
  if [ -n "$DOC2" ]; then probe GET "/v1/kb/chunks/ck-1?docId=$DOC2&userId=demo-student-a&role=student&context=1"; fi
fi
probe POST "/internal/v1/kb/import" '{"ownerUserId":"demo-student-a","items":[{"title":"导入 A","visibility":"public","courseCode":"LINALG","text":"导入正文 A"}]}'

echo "=== 12) 前端页面与静态资源 ==="
_ui=$(curl -s -w "\n%{http_code}" "$BASE/" --max-time 25)
UI_CODE="${_ui##*$'\n'}"
printf '%s' "${_ui%$'\n'*}" > "$WORK/ui.html"
UI_SIZE=$([ -f "$WORK/ui.html" ] && wc -c < "$WORK/ui.html" || echo 0)
UI_TITLE=$(grep -o "<title>[^<]*</title>" "$WORK/ui.html" 2>/dev/null | head -1)
UI_NAV=$(grep -c "data-page=" "$WORK/ui.html" 2>/dev/null || echo 0)
UI_BRAND=$(grep -c "Knowledge Debt: Astral Path" "$WORK/ui.html" 2>/dev/null || echo 0)
echo "  状态码=$UI_CODE 字节=$UI_SIZE 导航项=$UI_NAV 品牌全称出现次数=$UI_BRAND"
echo "  $UI_TITLE"
if [ "$UI_CODE" = "200" ] && [ "$UI_NAV" -gt 5 ] && [ "$UI_BRAND" -gt 0 ]; then
  PASS=$((PASS+1)); echo "  [OK]   前端页面可访问、导航完整、品牌口径完整"
else
  FAIL=$((FAIL+1)); echo "  [FAIL] 前端页面异常"; FAILED_LIST="$FAILED_LIST\n  GET / -> $UI_CODE"
fi
SW=$(curl -s -o /dev/null -w "%{http_code}" "$BASE/swagger/index.html" --max-time 25)
echo "  Swagger=$SW"
if [ "$SW" = "200" ]; then PASS=$((PASS+1)); else FAIL=$((FAIL+1)); FAILED_LIST="$FAILED_LIST\n  GET /swagger -> $SW"; fi

echo
echo "=============================================="
echo " 端到端验证：通过 $PASS · 失败 $FAIL"
echo "=============================================="
if [ "$FAIL" -gt 0 ]; then echo -e "失败明细：$FAILED_LIST"; fi
rm -rf "$WORK"
