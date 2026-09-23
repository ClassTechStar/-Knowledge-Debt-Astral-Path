#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
教材上传/解析测试工具（不依赖 curl）

为什么需要它：curl 的 `-F "file=@path"` 用**逗号**分隔同一字段的多个文件，
因此文件名含逗号的教材（如 "… (Ian Goodfellow,Yoshua Bengio,Aaron Courville) .pdf"）
会导致 curl 解析失败并返回 HTTP=000。真实客户端（浏览器 / Android WebView）走
FormData，不受此限制 —— 本工具用标准库 multipart 复现真实客户端的上传方式。

用法：
    python scripts/materials-upload.py <api_base> <pdf 路径> [--ocr standard|quick] [--parse]

示例：
    python scripts/materials-upload.py http://127.0.0.1:5190 "C:/path/含,逗号的文件.pdf"
"""
from __future__ import annotations

import argparse
import json
import mimetypes
import os
import sys
import time
import urllib.request
import urllib.error
import uuid


def post_multipart(url: str, file_path: str, timeout: int = 1800) -> tuple[int, str]:
    """以 multipart/form-data 上传单个文件，字段名 file（与接口契约一致）。"""
    boundary = "----AstralPathBoundary" + uuid.uuid4().hex
    filename = os.path.basename(file_path)
    ctype = mimetypes.guess_type(filename)[0] or "application/octet-stream"

    head = (
        f"--{boundary}\r\n"
        f'Content-Disposition: form-data; name="file"; filename="{filename}"\r\n'
        f"Content-Type: {ctype}\r\n\r\n"
    ).encode("utf-8")
    tail = f"\r\n--{boundary}--\r\n".encode("utf-8")

    with open(file_path, "rb") as f:
        payload = head + f.read() + tail

    req = urllib.request.Request(
        url, data=payload,
        headers={"Content-Type": f"multipart/form-data; boundary={boundary}"},
        method="POST")
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return resp.status, resp.read().decode("utf-8", errors="replace")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", errors="replace")


def post_json(url: str, timeout: int = 1800) -> tuple[int, str]:
    req = urllib.request.Request(url, data=b"", method="POST")
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return resp.status, resp.read().decode("utf-8", errors="replace")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", errors="replace")


def get_json(url: str, timeout: int = 60):
    with urllib.request.urlopen(url, timeout=timeout) as resp:
        body = json.loads(resp.read().decode("utf-8", errors="replace"))
        return body.get("data", body)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("api_base", help="如 http://127.0.0.1:5190")
    ap.add_argument("pdf", help="PDF 路径")
    ap.add_argument("--ocr", default="standard", choices=["quick", "standard", "off"])
    ap.add_argument("--parse", action="store_true", help="上传后立即解析并轮询至完成")
    args = ap.parse_args()

    base = args.api_base.rstrip("/")
    size_mb = os.path.getsize(args.pdf) / 1048576
    print(f"文件：{os.path.basename(args.pdf)}（{size_mb:.1f} MB）")

    t0 = time.time()
    code, body = post_multipart(f"{base}/v1/materials/upload?ocr={args.ocr}", args.pdf)
    print(f"上传：HTTP={code} 用时={time.time()-t0:.1f}s")
    if code not in (200, 201):
        print("响应：", body[:400])
        return 1
    doc = json.loads(body).get("data") or json.loads(body)
    mid = doc.get("id")
    print(f"  material id={mid} name={doc.get('name')} size={doc.get('sizeBytes')}")

    if not args.parse:
        return 0

    code, body = post_json(f"{base}/v1/materials/{mid}/parse")
    print(f"解析调用：HTTP={code}")

    status = None
    for _ in range(120):
        d = get_json(f"{base}/v1/materials/{mid}")
        status = d.get("status")
        if status in ("ready", "failed", "error"):
            break
        time.sleep(5)

    d = get_json(f"{base}/v1/materials/{mid}")
    print(f"状态={d.get('status')} 页={d.get('pageCount')} 字符={d.get('extractedChars')} "
          f"节点={d.get('nodeCount')} 边={d.get('edgeCount')} 图谱={d.get('graphId')} "
          f"OCR={d.get('ocrUsed')} 错误={d.get('error')}")
    try:
        tasks = get_json(f"{base}/v1/materials/{mid}/tasks")
        ques = get_json(f"{base}/v1/materials/{mid}/textbook-questions")
        nt = len(tasks.get("tasks", tasks) if isinstance(tasks, dict) else tasks)
        nq = len(ques.get("questions", ques) if isinstance(ques, dict) else ques)
        print(f"教材任务={nt} 章节真题={nq}")
    except Exception as e:
        print("附属查询失败：", e)

    return 0 if d.get("status") == "ready" else 1


if __name__ == "__main__":
    sys.exit(main())
