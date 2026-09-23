#!/usr/bin/env bash
# =============================================================================
#  13 份教材 PDF 全量管线测试（§19.5 / 藏书阁）
#
#  覆盖链路：上传 → 解析（文本层优先，扫描版走 OCR）→ 建图 → 教材任务 → 章节真题
#  用法：
#    bash scripts/materials-13pdf-test.sh            # 默认 standard OCR
#    OCR=quick bash scripts/materials-13pdf-test.sh  # 快速模式
#
#  产物：
#    docs/materials-13pdf-results.tsv  原始结果（可 diff）
#    docs/materials-13pdf-report.md    人类可读报告
#
#  注意（本机踩过的坑）：
#    · curl 是原生 Windows 二进制，`-F file=@` 必须给 **Windows 路径**，MSYS 的 /c/... 会静默失败（HTTP=000）
#    · 后端需监听 0.0.0.0 才能被手机/局域网访问
#    · OCR 依赖 Python 环境（ASTRALPATH_OCR_PYTHON）与 tesseract + tessdata
# =============================================================================
set -u

REPO="${REPO:-/c/Users/18948/Documents/GitHub/-Knowledge-Debt-Astral-Path}"
REPO_WIN="${REPO_WIN:-C:\\Users\\18948\\Documents\\GitHub\\-Knowledge-Debt-Astral-Path}"
DL_WIN="C:/Users/18948/Downloads"
PORT="${PORT:-5190}"
OCR_MODE="${OCR:-standard}"
API="http://127.0.0.1:$PORT"
CURL="curl -s --noproxy *"

export DOTNET_ROOT="C:/Program Files/dotnet"
export NUGET_PACKAGES="C:/Temp/ngp"
export ASTRALPATH_GRAPH_PACK="$REPO_WIN\\graph-packs\\accounting-v1"
export ASTRALPATH_TESSERACT="C:\\Program Files\\Tesseract-OCR\\tesseract.exe"
export ASTRALPATH_TESSDATA="$REPO_WIN\\tools\\tessdata"
export ASTRALPATH_OCR_PYTHON="C:\\Users\\18948\\XiaomiMiMoProjects\\Knowledge Debt Astral Path\\tools\\ocr-venv\\Scripts\\python.exe"

# 13 份测试用教材（文件名即材料标题）
FILES=(
  "图灵程序设计丛书--深度学习入门4：强化学习 ([日] 斋藤康毅) (1).pdf"
  "C#从入门到精通（第7版）+(明日科技)+.pdf"
  "深度学习进阶：自然语言处理 (斋藤康毅) .pdf"
  "黄仁勋：英伟达之芯_【美】斯蒂芬·威特.pdf"
  "Java从入门到精通（第6版） (明日科技) .pdf"
  "DeepLearning-Goodfellow-花书.pdf"
  "深度学习 Deep Learning [花书] (Ian Goodfellow,Yoshua Bengio,Aaron Courville) .pdf"
  "Python编程：从入门到实践（第3版）.pdf"
  "深度学习入门：基于Python的理论与实现+(斋藤康毅)+.pdf"
  "Kotlin编程实践：Kotlin从入门到实战.pdf"
  "Go语言从入门到精通.pdf"
  "大模型应用开发：动手做 AI Agent (黄佳) .pdf"
  "深度学习入门2：自制框架 (斋藤康毅)-扫描版 (1).PDF"
)

OUT_TSV="$REPO/docs/materials-13pdf-results.tsv"
mkdir -p "$REPO/docs"

echo "=== 启动后端（0.0.0.0:$PORT，OCR=$OCR_MODE）==="
cd "$REPO"
dotnet run --project src/AstralPath.Api -c Release --no-build --urls "http://0.0.0.0:$PORT" \
  --Logging:LogLevel:Microsoft.AspNetCore=Warning > /tmp/pdf-test-api.log 2>&1 &
API_PID=$!
trap 'kill $API_PID 2>/dev/null' EXIT
for i in $(seq 1 60); do
  curl -s --noproxy '*' --max-time 2 "$API/health/ready" >/dev/null 2>&1 && break
  sleep 1
done
echo "  就绪：$(curl -s --noproxy '*' --max-time 5 "$API/health/ready" | head -c 90)"
grep -a "手机端请在" /tmp/pdf-test-api.log | head -1 | sed 's/^/  /'

printf 'file\tsize_mb\tupload_http\tmaterial_id\tstatus\tpages\tchars\tnodes\tedges\tgraph\ttasks\tquestions\telapsed_s\tnote\n' > "$OUT_TSV"

IDX=0
for NAME in "${FILES[@]}"; do
  IDX=$((IDX+1))
  SRC="$DL_WIN/$NAME"
  SIZE_MB=$(stat -c%s "/c/Users/18948/Downloads/$NAME" 2>/dev/null | awk '{printf "%.1f", $1/1048576}')
  echo ""
  echo "── [$IDX/13] $NAME （${SIZE_MB:-?} MB）"

  T0=$(date +%s)
  UP=$(curl -s --noproxy '*' -w '\n%{http_code}' -X POST "$API/v1/materials/upload?ocr=$OCR_MODE" \
       -F "file=@$SRC" --max-time 1800)
  UP_HTTP=$(printf '%s' "$UP" | tail -n1)
  UP_BODY=$(printf '%s' "$UP" | sed '$d')
  MID=$(printf '%s' "$UP_BODY" | sed -n 's/.*"id":"\([^"]*\)".*/\1/p')
  if [ -z "$MID" ]; then
    echo "     ❌ 上传失败 HTTP=$UP_HTTP：$(printf '%s' "$UP_BODY" | head -c 160)"
    printf '%s\t%s\t%s\t-\tfailed_upload\t0\t0\t0\t0\t-\t0\t0\t%s\t%s\n' \
      "$NAME" "$SIZE_MB" "$UP_HTTP" "$(( $(date +%s) - T0 ))" "$(printf '%s' "$UP_BODY" | head -c 120 | tr '\t' ' ')" >> "$OUT_TSV"
    continue
  fi
  echo "     ✅ 上传 HTTP=$UP_HTTP id=$MID"

  PARSE=$(curl -s --noproxy '*' -X POST "$API/v1/materials/$MID/parse" --max-time 1800)
  echo "     解析调用：$(printf '%s' "$PARSE" | head -c 120)"

  # 轮询解析状态（解析可能异步进行）
  STATUS=""
  for w in $(seq 1 120); do
    DOC=$(curl -s --noproxy '*' "$API/v1/materials/$MID" --max-time 30)
    STATUS=$(printf '%s' "$DOC" | sed -n 's/.*"status":"\([^"]*\)".*/\1/p')
    case "$STATUS" in ready|failed|error) break ;; esac
    sleep 5
  done

  DOC=$(curl -s --noproxy '*' "$API/v1/materials/$MID" --max-time 30)
  PAGES=$(printf '%s' "$DOC"  | sed -n 's/.*"pageCount":\([0-9]*\).*/\1/p')
  CHARS=$(printf '%s' "$DOC"  | sed -n 's/.*"extractedChars":\([0-9]*\).*/\1/p')
  NODES=$(printf '%s' "$DOC"  | sed -n 's/.*"nodeCount":\([0-9]*\).*/\1/p')
  EDGES=$(printf '%s' "$DOC"  | sed -n 's/.*"edgeCount":\([0-9]*\).*/\1/p')
  GRAPH=$(printf '%s' "$DOC"  | sed -n 's/.*"graphId":\("[^"]*"\|null\).*/\1/p')
  OCRC=$(printf '%s' "$DOC"  | sed -n 's/.*"ocrUsed":\(true\|false\).*/\1/p')

  TASKS=$(curl -s --noproxy '*' "$API/v1/materials/$MID/tasks" --max-time 60 | grep -o '"id"' | wc -l)
  QUES=$(curl -s --noproxy '*' "$API/v1/materials/$MID/textbook-questions" --max-time 60 | grep -o '"id"' | wc -l)

  DUR=$(( $(date +%s) - T0 ))
  echo "     状态=$STATUS 页=$PAGES 字数=$CHARS 节点=$NODES 边=$EDGES OCR=$OCRC 任务=$TASKS 真题=$QUES 用时=${DUR}s"
  printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\tocr=%s\n' \
    "$NAME" "$SIZE_MB" "$UP_HTTP" "$MID" "${STATUS:-unknown}" \
    "${PAGES:-0}" "${CHARS:-0}" "${NODES:-0}" "${EDGES:-0}" "${GRAPH:-null}" \
    "$TASKS" "$QUES" "$DUR" "$OCRC" >> "$OUT_TSV"
done

echo ""
echo "=== 汇总 ==="
echo "  原始结果：$OUT_TSV"
awk -F'\t' 'NR>1 {t++; if ($5=="ready") ok++; else bad++} END {printf "  成功 %d / 总计 %d（非 ready %d）\n", ok+0, t+0, bad+0}' "$OUT_TSV"
