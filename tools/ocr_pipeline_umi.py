# -*- coding: utf-8 -*-
"""
AstralPath OCR Pipeline
对齐 Umi-OCR (mission_doc + tbpu) / PaddleOCR 检测-识别思路 / tesseract 阅读顺序。
来源：
  C:\\Users\\18948\\Documents\\GitHub\\Umi-OCR\\UmiOCR-data\\py_src\\mission\\mission_doc.py
  ...\\ocr\\tbpu\\parser_tools\\{gap_tree,line_preprocessing,paragraph_parse}.py
"""
from __future__ import annotations

import math
import os
import re
import sys
import unicodedata
from dataclasses import dataclass, field
from io import BytesIO
from statistics import median
from typing import Callable, List, Optional, Tuple

import pymupdf  # PyMuPDF
from PIL import Image

# ========== Umi-OCR mission_doc 常量 ==========
MinSize = 1080  # 最小渲染分辨率

DocSuf = [".pdf", ".xps", ".epub", ".mobi", ".fb2", ".cbz"]

# ========== Umi-OCR line_preprocessing ==========
angle_threshold = 3
angle_threshold_rad = math.radians(angle_threshold)


def _distance(p1, p2):
    return math.sqrt((p2[0] - p1[0]) ** 2 + (p2[1] - p1[1]) ** 2)


def _calculate_angle(box):
    width = _distance(box[0], box[1])
    height = _distance(box[1], box[2])
    if width < height:
        angle_rad = math.atan2(box[2][1] - box[1][1], box[2][0] - box[1][0])
    else:
        angle_rad = math.atan2(box[1][1] - box[0][1], box[1][0] - box[0][0])
    if angle_rad < -math.pi / 2 + angle_threshold_rad:
        angle_rad += math.pi
    elif angle_rad >= math.pi / 2 + angle_threshold_rad:
        angle_rad -= math.pi
    return angle_rad


def _estimate_rotation(text_blocks):
    angle_rads = (_calculate_angle(b["box"]) for b in text_blocks if b.get("box"))
    try:
        return median(angle_rads)
    except Exception:
        return 0.0


def _get_bboxes(text_blocks, rotation_rad):
    if abs(rotation_rad) <= angle_threshold_rad:
        return [
            (
                min(x for x, y in tb["box"]),
                min(y for x, y in tb["box"]),
                max(x for x, y in tb["box"]),
                max(y for x, y in tb["box"]),
            )
            for tb in text_blocks
        ]
    bboxes = []
    min_x = min_y = float("inf")
    cos_a = math.cos(-rotation_rad)
    sin_a = math.sin(-rotation_rad)
    for tb in text_blocks:
        rotated = [(cos_a * x - sin_a * y, sin_a * x + cos_a * y) for x, y in tb["box"]]
        xs, ys = zip(*rotated)
        bbox = (min(xs), min(ys), max(xs), max(ys))
        bboxes.append(bbox)
        min_x, min_y = min(min_x, bbox[0]), min(min_y, bbox[1])
    if min_x < 0 or min_y < 0:
        bboxes = [(x - min_x, y - min_y, x2 - min_x, y2 - min_y) for (x, y, x2, y2) in bboxes]
    return bboxes


def line_preprocessing(text_blocks):
    """Umi-OCR linePreprocessing：标准化 bbox + 按 y 排序。"""
    text_blocks = [i for i in text_blocks if i.get("text")]
    if not text_blocks:
        return []
    for tb in text_blocks:
        if "box" not in tb:
            b = tb.get("normalized_bbox") or tb.get("bbox")
            if b:
                tb["box"] = [(b[0], b[1]), (b[2], b[1]), (b[2], b[3]), (b[0], b[3])]
    rotation_rad = _estimate_rotation(text_blocks)
    bboxes = _get_bboxes(text_blocks, rotation_rad)
    for i, tb in enumerate(text_blocks):
        tb["normalized_bbox"] = bboxes[i]
    text_blocks.sort(key=lambda tb: tb["normalized_bbox"][1])
    return text_blocks


# ========== Umi-OCR paragraph_parse ==========
TH = 1.2


def is_cjk(character: str) -> bool:
    cjk_ranges = [
        (0x4E00, 0x9FFF),
        (0x3040, 0x30FF),
        (0x1100, 0x11FF),
        (0x3130, 0x318F),
        (0xAC00, 0xD7AF),
        (0x3000, 0x303F),
        (0xFE30, 0xFE4F),
        (0xFF00, 0xFFEF),
    ]
    return any(a <= ord(character) <= b for a, b in cjk_ranges)


def word_separator(letter1: str, letter2: str) -> str:
    """Umi-OCR word_separator：上下句间隔符。"""
    if is_cjk(letter1) and is_cjk(letter2):
        return ""
    if letter1 == "-":
        return ""
    if letter2 and unicodedata.category(letter2).startswith("P"):
        return ""
    return " "


class ParagraphParse:
    """Umi-OCR ParagraphParse：段落关系与行尾分隔符预测。"""

    def __init__(self, get_info: Callable, set_end: Callable):
        self.get_info = get_info
        self.set_end = set_end

    def run(self, text_blocks: list):
        if not text_blocks:
            return text_blocks
        units = self._get_units(text_blocks)
        self._parse(units)
        return text_blocks

    def _get_units(self, text_blocks):
        units = []
        for tb in text_blocks:
            bbox, text = self.get_info(tb)
            if not text:
                continue
            units.append((bbox, (text[0], text[-1]), tb))
        return units

    def _parse(self, units):
        if not units:
            return
        units.sort(key=lambda a: a[0][1])
        para_l, para_top, para_r, para_bottom = units[0][0]
        para_line_h = para_bottom - para_top
        para_line_s = None
        now_para = [units[0]]
        paras, paras_line_space = [], []
        for i in range(1, len(units)):
            l, top, r, bottom = units[i][0]
            h = bottom - top
            ls = top - para_bottom
            if (
                abs(para_l - l) <= para_line_h * TH
                and abs(para_r - r) <= para_line_h * TH
                and (para_line_s is None or ls < para_line_s + para_line_h * 0.5)
            ):
                para_l = (para_l + l) / 2
                para_r = (para_r + r) / 2
                para_line_h = (para_line_h + h) / 2
                para_line_s = ls if para_line_s is None else (para_line_s + ls) / 2
                now_para.append(units[i])
            else:
                paras.append(now_para)
                paras_line_space.append(para_line_s)
                now_para = [units[i]]
                para_l, para_r, para_line_h = l, r, bottom - top
                para_line_s = None
            para_bottom = bottom
        paras.append(now_para)
        paras_line_space.append(para_line_s)

        for pi, para in enumerate(paras):
            for j in range(len(para) - 1):
                end = word_separator(para[j][1][1], para[j + 1][1][0])
                self.set_end(para[j][2], end)
            if pi < len(paras) - 1 and paras[pi + 1]:
                end = word_separator(para[-1][1][1], paras[pi + 1][0][1][0])
            else:
                end = "\n"
            self.set_end(para[-1][2], end)


# ========== Umi-OCR GapTree 阅读顺序（核心排序） ==========
class GapTree:
    """GapTree_Sort_Algorithm — hiroi-sora / Umi-OCR。"""

    def __init__(self, get_bbox: Callable):
        self.get_bbox = get_bbox

    def sort(self, text_blocks: list):
        if not text_blocks:
            return []
        units, page_l, page_r = self._get_units(text_blocks)
        cuts, rows = self._get_cuts_rows(units, page_l, page_r)
        root = self._get_layout_tree(cuts, rows)
        nodes = self._preorder_traversal(root)
        return self._get_text_blocks(nodes)

    def _get_units(self, text_blocks):
        units = []
        page_l, page_r = float("inf"), -1.0
        for tb in text_blocks:
            x0, y0, x2, y2 = self.get_bbox(tb)
            units.append(((x0, y0, x2, y2), tb))
            page_l = min(page_l, x0)
            page_r = max(page_r, x2)
        units.sort(key=lambda a: a[0][1])
        return units, page_l, page_r

    def _get_cuts_rows(self, units, page_l, page_r):
        def merge_gaps(gaps1, gaps2):
            result = []
            for l1, r1 in gaps1:
                for l2, r2 in gaps2:
                    il, ir = max(l1, l2), min(r1, r2)
                    if il < ir:
                        result.append((il, ir))
            # 合并重叠
            result.sort()
            merged = []
            for g in result:
                if merged and g[0] <= merged[-1][1]:
                    merged[-1] = (merged[-1][0], max(merged[-1][1], g[1]))
                else:
                    merged.append(g)
            return merged or gaps1

        rows = []
        for unit in units:
            if not rows:
                rows.append([unit])
                continue
            row = rows[-1]
            _, top, _, bottom = unit[0]
            row_tops = [u[0][1] for u in row]
            row_bots = [u[0][3] for u in row]
            mid = (top + bottom) / 2
            if mid >= min(row_tops) and mid <= max(row_bots):
                row.append(unit)
            else:
                rows.append([unit])
        for row in rows:
            row.sort(key=lambda a: a[0][0])

        # 竖切线：跨行连续列间隙
        gaps_all = [(page_l, page_r)]
        for row in rows:
            row_gaps = []
            cur = page_l
            for u in row:
                l, _, r, _ = u[0]
                if l > cur + 1:
                    row_gaps.append((cur, l))
                cur = max(cur, r)
            if page_r > cur + 1:
                row_gaps.append((cur, page_r))
            if row_gaps:
                gaps_all = merge_gaps(gaps_all, row_gaps)
        cuts = [(l, r) for l, r in gaps_all]
        return cuts, rows

    def _get_layout_tree(self, cuts, rows):
        # 用切线把行切成列块，再组成树（简化但保持阅读序：自上而下、自左而右）
        nodes = []
        for row in rows:
            for unit in row:
                nodes.append({"units": [unit], "children": []})
        return {"units": [], "children": nodes}

    def _preorder_traversal(self, root):
        out = []

        def walk(n):
            if n.get("units"):
                out.append(n)
            for c in n.get("children") or []:
                walk(c)

        walk(root)
        return out

    def _get_text_blocks(self, nodes):
        result = []
        for node in nodes:
            for unit in node["units"]:
                result.append(unit[1])
        return result


def gap_tree_sort(text_blocks: list) -> list:
    def get_bbox(tb):
        if "normalized_bbox" in tb:
            return tb["normalized_bbox"]
        if "box" in tb:
            xs = [p[0] for p in tb["box"]]
            ys = [p[1] for p in tb["box"]]
            return (min(xs), min(ys), max(xs), max(ys))
        b = tb.get("bbox")
        return (b[0], b[1], b[2], b[3])

    return GapTree(get_bbox).sort(text_blocks)


def parse_single_para(text_blocks: list) -> list:
    """Umi-OCR parser_single_para：单栏自然段 + 行尾分隔符。"""
    text_blocks = line_preprocessing(text_blocks)
    if not text_blocks:
        return []

    get_info = lambda tb: (tb["normalized_bbox"], tb["text"])

    def set_end(tb, end):
        tb["end"] = end

    pp = ParagraphParse(get_info, set_end)
    pp.run(text_blocks)
    return text_blocks


def blocks_to_text(text_blocks: list) -> str:
    """按阅读顺序拼接全文，保留段落。"""
    ordered = gap_tree_sort(list(text_blocks))
    ordered = parse_single_para(ordered)
    parts = []
    for tb in ordered:
        t = tb.get("text", "")
        if not t:
            continue
        end = tb.get("end", "\n")
        if end == "":
            parts.append(t)
        elif end == " ":
            parts.append(t + " ")
        else:
            parts.append(t + ("\n" if end != "\n" else "\n"))
    text = "".join(parts)
    text = re.sub(r"[ \t]+\n", "\n", text)
    text = re.sub(r"\n{3,}", "\n\n", text)
    return text.strip() + "\n"


# ========== mission_doc 抽取（mixed / fullPage / textOnly / imageOnly） ==========
def transform_to_rotation(matrix):
    a, b, c, d, _, _ = matrix
    scale = math.sqrt(a * a + b * b)
    if scale < 1e-6:
        return 0
    cos_theta = a / scale
    sin_theta = b / scale
    if a * d - b * c < 0:
        cos_theta = -cos_theta
    return round(math.degrees(math.atan2(sin_theta, cos_theta))) % 360


@dataclass
class PageExtract:
    page_no: int  # 1-based
    text: str
    chars: int
    blocks: int
    mode: str
    ocr_used: bool = False
    ocr_chars: int = 0
    text_chars: int = 0
    images: int = 0


@dataclass
class DocExtract:
    path: str
    pages: List[PageExtract] = field(default_factory=list)
    text: str = ""
    total_chars: int = 0
    page_count: int = 0
    empty_pages: List[int] = field(default_factory=list)
    ocr_pages: List[int] = field(default_factory=list)


class OcrEngine:
    """RapidOCR（对齐 Umi-OCR Rapid 引擎 / PaddleOCR 检测-识别）。"""

    def __init__(self):
        self._engine = None

    def _ensure(self):
        if self._engine is None:
            from rapidocr_onnxruntime import RapidOCR

            self._engine = RapidOCR()
        return self._engine

    def run(self, image) -> List[dict]:
        """返回 [{box:[[x,y]x4], text, score}]。支持 PIL / ndarray / bytes / path。"""
        eng = self._ensure()
        import numpy as np

        if isinstance(image, Image.Image):
            image = np.array(image.convert("RGB"))
        result, _ = eng(image)
        out = []
        if not result:
            return out
        for item in result:
            # RapidOCR: (box, text, score)
            box, text, score = item[0], item[1], item[2]
            pts = [(float(p[0]), float(p[1])) for p in box]
            out.append({"box": pts, "text": str(text), "score": float(score)})
        return out


_OCR = OcrEngine()


def ocr_image(img: Image.Image) -> List[dict]:
    return _OCR.run(img)


def extract_page(page, mode: str = "mixed", pno: int = 1) -> Tuple[str, List[dict], dict]:
    """
    Umi-OCR msnTask 抽取逻辑。
    返回 (text, text_blocks, stats)
    """
    blocks: List[dict] = []
    stats = {"images": 0, "ocr_used": False, "ocr_chars": 0, "text_chars": 0}
    imgs = []
    page_rotation = page.rotation

    if mode == "fullPage":
        rect = page.rect
        w, h = abs(rect[2] - rect[0]), abs(rect[3] - rect[1])
        m = min(w, h)
        zoom = (MinSize / max(m, 1)) if m < MinSize else 1
        matrix = pymupdf.Matrix(zoom, zoom) if zoom != 1 else pymupdf.Identity
        pix = page.get_pixmap(matrix=matrix)
        img = Image.open(BytesIO(pix.tobytes("png")))
        scale = 1 / zoom
        imgs.append({"img": img, "xy": (0, 0), "scale_w": scale, "scale_h": scale})
    else:
        p = page.get_text("dict", clip=pymupdf.INFINITE_RECT())
        for t in p.get("blocks", []):
            if t.get("type") == 1 and mode in ("imageOnly", "mixed"):
                stats["images"] += 1
                transform = t.get("transform")
                img_rotation = transform_to_rotation(transform) if transform else 0
                abs_rotation = round(page_rotation + img_rotation) % 360
                try:
                    img = Image.open(BytesIO(t["image"]))
                    if abs_rotation:
                        img = img.rotate(-abs_rotation, expand=True)
                except Exception:
                    continue
                bbox = t["bbox"]
                w1, h1 = bbox[2] - bbox[0], bbox[3] - bbox[1]
                w2, h2 = t.get("width") or img.width, t.get("height") or img.height
                if w2 <= 0 or h2 <= 0:
                    continue
                imgs.append(
                    {
                        "img": img,
                        "xy": (bbox[0], bbox[1]),
                        "scale_w": w1 / w2,
                        "scale_h": h1 / h2,
                    }
                )
            elif t.get("type") == 0 and mode in ("textOnly", "mixed"):
                for line in t.get("lines", []):
                    text = "".join(span.get("text", "") for span in line.get("spans", []))
                    if not text.strip():
                        continue
                    b = line["bbox"]
                    if page_rotation == 0:
                        box = [(b[0], b[1]), (b[2], b[1]), (b[2], b[3]), (b[0], b[3])]
                    else:
                        rm = page.rotation_matrix
                        b01 = pymupdf.Point(b[0], b[1]) * rm
                        b23 = pymupdf.Point(b[2], b[3]) * rm
                        x0, x1 = min(b01.x, b23.x), max(b01.x, b23.x)
                        y0, y1 = min(b01.y, b23.y), max(b01.y, b23.y)
                        box = [(x0, y0), (x1, y0), (x1, y1), (x0, y1)]
                    blocks.append({"box": box, "text": text, "src": "text"})
                    stats["text_chars"] += len(text.strip())

    # 对图片/整页做 OCR
    for item in imgs:
        try:
            results = ocr_image(item["img"])
        except Exception:
            continue
        if results:
            stats["ocr_used"] = True
        sx, sy = item["scale_w"], item["scale_h"]
        ox, oy = item["xy"]
        for r in results:
            box = [(ox + x * sx, oy + y * sy) for x, y in r["box"]]
            blocks.append({"box": box, "text": r["text"], "score": r["score"], "src": "ocr"})
            stats["ocr_chars"] += len(r["text"].strip())

    text = blocks_to_text(blocks) if blocks else ""
    return text, blocks, stats


def extract_document(
    path: str,
    mode: str = "mixed",
    max_pages: Optional[int] = None,
    page_list: Optional[List[int]] = None,
    progress: Optional[Callable] = None,
) -> DocExtract:
    """Umi-OCR addMission + msnTask 文档级抽取。"""
    out = DocExtract(path=path)
    doc = pymupdf.open(path)
    try:
        if doc.is_encrypted and not doc.authenticate(""):
            raise RuntimeError("文档已加密")
        out.page_count = doc.page_count
        if page_list:
            pages = [p - 1 for p in page_list if 1 <= p <= doc.page_count]
        else:
            pages = list(range(doc.page_count))
        if max_pages:
            pages = pages[:max_pages]
        texts = []
        for pno in pages:
            page = doc[pno]
            text, blocks, stats = extract_page(page, mode=mode, pno=pno + 1)
            chars = len(re.sub(r"\s", "", text))
            pe = PageExtract(
                page_no=pno + 1,
                text=text,
                chars=chars,
                blocks=len(blocks),
                mode=mode,
                ocr_used=stats["ocr_used"],
                ocr_chars=stats["ocr_chars"],
                text_chars=stats["text_chars"],
                images=stats["images"],
            )
            out.pages.append(pe)
            texts.append(f"\n\n===== PAGE {pno+1} =====\n{text}")
            if chars < 5:
                out.empty_pages.append(pno + 1)
            if stats["ocr_used"]:
                out.ocr_pages.append(pno + 1)
            if progress:
                progress(pno + 1, len(pages), pe)
        out.text = "".join(texts)
        out.total_chars = len(re.sub(r"\s", "", out.text))
    finally:
        doc.close()
    return out


def detect_needs_ocr(path: str, sample: int = 5) -> dict:
    """探测是否扫描版：抽样页文本层字符数。"""
    doc = pymupdf.open(path)
    try:
        n = doc.page_count
        idxs = [0, n // 4, n // 2, (3 * n) // 4, max(0, n - 1)]
        idxs = sorted(set(i for i in idxs if 0 <= i < n))[:sample]
        counts = []
        for i in idxs:
            t = doc[i].get_text("text") or ""
            counts.append(len(re.sub(r"\s", "", t)))
        avg = sum(counts) / max(len(counts), 1)
        return {
            "page_count": n,
            "sample_chars": counts,
            "avg_chars": avg,
            "likely_scanned": avg < 40,
        }
    finally:
        doc.close()
