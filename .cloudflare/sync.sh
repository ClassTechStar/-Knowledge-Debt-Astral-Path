#!/usr/bin/env bash
# 同步单体 Web 版到 Cloudflare Pages 部署目录
# 用途：deploy/monolith-web 变更后，重建 .cloudflare/pages
# 用法：bash .cloudflare/sync.sh
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$REPO/deploy/monolith-web"
DST="$REPO/.cloudflare/pages"

[ -f "$SRC/index.html" ] || { echo "ERROR: 源目录缺少 index.html"; exit 1; }

echo ">>> 清理旧部署包"
rm -rf "$DST/ocr-engine"
mkdir -p "$DST"

echo ">>> 复制入口与引擎（仅 Web 运行时所需）"
cp "$SRC/index.html" "$DST/"
cp -r "$SRC/ocr-engine" "$DST/ocr-engine"

echo ">>> 排除 Windows 专用文件"
rm -f "$DST/ocr-engine/dl-vendor.ps1"

echo ">>> 校验"
FILES=$(find "$DST" -type f | wc -l)
SIZE=$(du -sh "$DST" | cut -f1)
MAX=$(find "$DST" -type f -exec du -k {} \; | sort -rn | head -1 | cut -f1)
echo "文件数: $FILES"
echo "总体积: $SIZE"
echo "最大文件: $((MAX / 1024)) MB (Cloudflare 上限 25 MiB)"

if [ "$MAX" -gt 25600 ]; then
  echo "ERROR: 存在超过 25 MiB 的文件，Cloudflare Pages 会拒绝"
  exit 1
fi

echo ">>> 同步完成。上传命令："
echo "    npx wrangler pages deploy pages --project-name astralpath"
