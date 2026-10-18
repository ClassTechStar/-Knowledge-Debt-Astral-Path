# 智能体意图登记表 · 多方对拍（P2 单一事实源门禁；P5-1 扩展）
# ============================================================
# 唯一事实源：tools/agent-intents.json
# 对拍对象：
#   ① deploy/monolith-web/index.html 的 CRISIS/BANNED/NEGATIVE/INTENTS 字面量
#      （三端同源，另两份镜像由 sync-monolith-html.ps1 保证 md5 一致）
#   ② src/AstralPath.Core/Agent/AgentRouter.cs 的 DefaultAgentIntents.Table
#      —— P5-1 起登记表即运行时表（DefaultIntents() 委托它），故锚点/角色/描述逐字对拍
#   ③ AgentRouter.CreateDefault() 的 crisisWords / negativeWords 内联数组
#      —— K-03：危机词漏词曾是检测盲区（后端缺「活不下去」无人报警）
#   ④ src/AstralPath.Core/Formula/FormulaWeights.cs 的 BannedWords ↔ JSON bannedEthics
#      —— 路由禁语（伦理）与 JS BANNED（隐私）是两类，分别登记、分别对拍
#   ⑤ shared/integrations/p2-ai-integration.js 的 CRISIS/BANNED/NEGATIVE/INTENTS
#      —— P3（2026-10-18）：22→26 意图扩容时该副本漏同步（kb.search 少 13 个锚点等
#         17 处），而 .cloudflare/pages/* 被 gitignore 排除，漂移长期无人发现。
#         修复后脚本收编进受控目录 shared/integrations/，并纳入本对拍（第四方）。
# 用法：python scripts/verify_agent_intents.py
# 退出码：0 = ALL GREEN；1 = 漂移
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
INTENTS_JSON = ROOT / "tools" / "agent-intents.json"
INDEX_HTML = ROOT / "deploy" / "monolith-web" / "index.html"
AGENT_ROUTER = ROOT / "src" / "AstralPath.Core" / "Agent" / "AgentRouter.cs"
# P7（2026-10-18）：登记表从 AgentRouter.cs 拆到独立文件（单文件单职责），
# 运行时词表仍在 AgentRouter.CreateDefault() 内联，故两个文件分别对拍。
DEFAULT_INTENTS = ROOT / "src" / "AstralPath.Core" / "Agent" / "DefaultAgentIntents.cs"
FORMULA_WEIGHTS = ROOT / "src" / "AstralPath.Core" / "Formula" / "FormulaWeights.cs"
# P3：第四方 JS 副本（受控目录；p0/p1 不定义路由常量，故只校验定义了的一方）
P2_JS = ROOT / "shared" / "integrations" / "p2-ai-integration.js"
# P2/P3：共享内核 —— 常量块由 build 期 JSON 生成（DO NOT EDIT），仍需对拍防生成器漏跑
CORE_JS = ROOT / "shared" / "core.js"


def fail(msg):
    print(f"[DRIFT] {msg}")
    print("FAILED: agent-intents 对拍不一致")
    sys.exit(1)


def extract_js_array(source, name):
    m = re.search(r"(?:const|var|let)\s+" + name + r"\s*=\s*(\[.*?\]);", source, re.S)
    if not m:
        fail(f"找不到 {name} 字面量（const/var）")
    try:
        return json.loads(m.group(1))
    except json.JSONDecodeError as e:
        fail(f"{name} 不是严格 JSON（{e}）；请保持双引号、无尾逗号")


def extract_cs_string_array(source, marker, name):
    """从 marker 所在位置起向后找第一个字符串数组字面量，返回字符串列表。
    兼容两种 C# 写法：new[]{...} 与集合初始化器 =\n    {...};"""
    idx = source.find(marker)
    if idx < 0:
        fail(f"C# 中找不到 {name}（marker: {marker[:40]}…）")
    seg = source[idx:idx + 2000]
    m = re.search(r"new\s*\[\]\s*\{(.*?)\}", seg, re.S) or re.search(r"=\s*\{(.*?)\}\s*;", seg, re.S)
    if not m:
        fail(f"C# 中 {name} 数组解析失败")
    return re.findall(r'"([^"]*)"', m.group(1))


def main():
    if not INTENTS_JSON.exists():
        fail(f"缺少 {INTENTS_JSON}")
    canon = json.loads(INTENTS_JSON.read_text(encoding="utf-8"))

    # ── ① JS 字面量 ─────────────────────────────────────────
    html = INDEX_HTML.read_text(encoding="utf-8")
    js_crisis = extract_js_array(html, "CRISIS")
    js_banned = extract_js_array(html, "BANNED")
    js_negative = extract_js_array(html, "NEGATIVE")
    js_intents = extract_js_array(html, "INTENTS")

    if js_crisis != canon["crisis"]:
        fail(f"CRISIS 与 JSON 不一致\n  json={canon['crisis']}\n  js={js_crisis}")
    if js_banned != canon["banned"]:
        fail(f"BANNED 与 JSON 不一致\n  json={canon['banned']}\n  js={js_banned}")
    if js_negative != canon["negative"]:
        fail(f"NEGATIVE 与 JSON 不一致\n  json={canon['negative']}\n  js={js_negative}")

    js_map = {i[0]: i[1] for i in js_intents}
    canon_map = {i["id"]: i["anchors"] for i in canon["intents"]}
    if list(js_map) != list(canon_map):
        fail(f"意图清单不一致\n  仅 json 有: {sorted(set(canon_map) - set(js_map))}\n  仅 js 有: {sorted(set(js_map) - set(canon_map))}")
    for k in canon_map:
        if js_map[k] != canon_map[k]:
            fail(f"意图 {k} 锚点不一致\n  json={canon_map[k]}\n  js={js_map[k]}")

    # ── ② C# 登记表（=运行时表）：锚点 + 角色 + 描述逐字对拍 ──
    cs = AGENT_ROUTER.read_text(encoding="utf-8")
    tbl_src = DEFAULT_INTENTS.read_text(encoding="utf-8") if DEFAULT_INTENTS.exists() else cs
    table = re.search(r"DefaultAgentIntents[\s\S]*?Table\s*=\s*\{([\s\S]*?)\n    \};", tbl_src)
    if not table and tbl_src is not cs:
        table = re.search(r"Table\s*=\s*\{([\s\S]*?)\n    \};", tbl_src)
    if not table:
        fail(f"找不到 DefaultAgentIntents.Table（查过 {DEFAULT_INTENTS.name} 与 {AGENT_ROUTER.name}）")
    # P5-1：RoleMask 允许逗号多角色（旧正则 "(\w+)" 只认单角色，是检测盲区）
    entries = re.findall(
        r'new\(\s*(\d+)\s*,\s*"([a-z.]+)"\s*,\s*"([\w,]+)"\s*,\s*'
        r'(?:Array\.Empty<string>\(\)|new\[\]\s*\{([^}]*)\})\s*,\s*'
        r'(?:Array\.Empty<string>\(\)|new\[\]\s*\{([^}]*)\})\s*,\s*"([^"]*)"\s*\)',
        table.group(1),
    )
    if not entries:
        fail("DefaultAgentIntents.Table 解析出 0 条意图（正则与代码结构不符）")
    cs_map, cs_roles, cs_desc = {}, {}, {}
    for _bit, intent_id, role, anchors_raw, _slots_raw, desc in entries:
        cs_map[intent_id] = re.findall(r'"([^"]*)"', anchors_raw)
        cs_roles[intent_id] = [r.strip() for r in role.split(",") if r.strip()]
        cs_desc[intent_id] = desc
    if list(cs_map) != list(canon_map):
        fail(f"C# 意图清单与 JSON 不一致\n  仅 json 有: {sorted(set(canon_map) - set(cs_map))}\n  仅 cs 有: {sorted(set(cs_map) - set(canon_map))}")
    canon_roles = {i["id"]: i["roles"] for i in canon["intents"]}
    canon_desc = {i["id"]: i["description"] for i in canon["intents"]}
    for k in canon_map:
        if cs_map[k] != canon_map[k]:
            fail(f"意图 {k} 锚点 C# 与 JSON 不一致\n  json={canon_map[k]}\n  cs={cs_map[k]}")
        if cs_roles[k] != canon_roles[k]:
            fail(f"意图 {k} 角色不一致（登记表=运行时表）\n  json={canon_roles[k]}\n  cs={cs_roles[k]}")
        if cs_desc[k] != canon_desc[k]:
            fail(f"意图 {k} 描述不一致\n  json={canon_desc[k]!r}\n  cs={cs_desc[k]!r}")

    # ── ③ C# 运行时危机/负例词（CreateDefault 内联数组）──
    cs_crisis = extract_cs_string_array(cs, "crisisWords:", "crisisWords")
    cs_negative = extract_cs_string_array(cs, "negativeWords:", "negativeWords")
    # 词表无序语义，按集合比对（跨语言手抄顺序不强制）
    if sorted(cs_crisis) != sorted(canon["crisis"]):
        fail(f"CreateDefault 危机词与 JSON 不一致（K-03 检测盲区）\n  json={canon['crisis']}\n  cs={cs_crisis}")
    if sorted(cs_negative) != sorted(canon["negative"]):
        fail(f"CreateDefault 负例词与 JSON 不一致\n  json={canon['negative']}\n  cs={cs_negative}")

    # ── ④ 伦理禁语：FormulaWeights.BannedWords ↔ JSON bannedEthics ──
    if FORMULA_WEIGHTS.exists():
        fw = FORMULA_WEIGHTS.read_text(encoding="utf-8")
        cs_banned_ethics = extract_cs_string_array(fw, "BannedWords =", "BannedWords")
        if sorted(cs_banned_ethics) != sorted(canon.get("bannedEthics", [])):
            fail(f"FormulaWeights.BannedWords 与 JSON bannedEthics 不一致\n"
                 f"  json={canon.get('bannedEthics')}\n  cs={cs_banned_ethics}")

    # ── ⑤ 第四方 JS 副本：shared/integrations/p2-ai-integration.js ──
    p2_map = {}
    p2_checked = False
    if P2_JS.exists():
        p2 = P2_JS.read_text(encoding="utf-8")
        p2_crisis = extract_js_array(p2, "CRISIS")
        p2_banned = extract_js_array(p2, "BANNED")
        p2_negative = extract_js_array(p2, "NEGATIVE")
        p2_intents = extract_js_array(p2, "INTENTS")
        if p2_crisis != canon["crisis"]:
            fail(f"p2 CRISIS 与 JSON 不一致\n  json={canon['crisis']}\n  p2={p2_crisis}")
        if p2_banned != canon["banned"]:
            fail(f"p2 BANNED 与 JSON 不一致\n  json={canon['banned']}\n  p2={p2_banned}")
        if p2_negative != canon["negative"]:
            fail(f"p2 NEGATIVE 与 JSON 不一致\n  json={canon['negative']}\n  p2={p2_negative}")
        p2_map = {i[0]: i[1] for i in p2_intents}
        if list(p2_map) != list(canon_map):
            fail(f"p2 意图清单不一致\n  仅 json 有: {sorted(set(canon_map) - set(p2_map))}\n"
                 f"  仅 p2 有: {sorted(set(p2_map) - set(canon_map))}")
        for k in canon_map:
            if p2_map[k] != canon_map[k]:
                fail(f"p2 意图 {k} 锚点不一致（第四方副本漂移）\n  json={canon_map[k]}\n  p2={p2_map[k]}")
        p2_checked = True

    # ── ⑥ 共享内核 shared/core.js（构建期生成块）──
    core_map = {}
    core_checked = False
    if CORE_JS.exists():
        core = CORE_JS.read_text(encoding="utf-8")
        if "DO NOT EDIT" not in core:
            fail("shared/core.js 缺少 DO NOT EDIT 标记（常量块应来自 scripts/gen-shared-core.js）")
        c_crisis = extract_js_array(core, "CRISIS")
        c_banned = extract_js_array(core, "BANNED")
        c_negative = extract_js_array(core, "NEGATIVE")
        c_intents = extract_js_array(core, "INTENTS")
        if c_crisis != canon["crisis"]:
            fail(f"core.js CRISIS 与 JSON 不一致\n  json={canon['crisis']}\n  core={c_crisis}")
        if c_banned != canon["banned"]:
            fail(f"core.js BANNED 与 JSON 不一致\n  json={canon['banned']}\n  core={c_banned}")
        if c_negative != canon["negative"]:
            fail(f"core.js NEGATIVE 与 JSON 不一致\n  json={canon['negative']}\n  core={c_negative}")
        core_map = {i[0]: i[1] for i in c_intents}
        if list(core_map) != list(canon_map):
            fail(f"core.js 意图清单不一致\n  仅 json 有: {sorted(set(canon_map) - set(core_map))}\n"
                 f"  仅 core 有: {sorted(set(core_map) - set(canon_map))}")
        for k in canon_map:
            if core_map[k] != canon_map[k]:
                fail(f"core.js 意图 {k} 锚点不一致（生成器未重跑？）\n  json={canon_map[k]}\n  core={core_map[k]}")
        core_checked = True

    n_ethics = len(canon.get("bannedEthics", []))
    extra = []
    if p2_checked:
        extra.append(f"p2={len(p2_map)}")
    if core_checked:
        extra.append(f"core={len(core_map)}")
    tail = (" " + " ".join(extra)) if extra else ""
    print(f"ALL GREEN · agent-intents 多方一致（json={len(canon_map)} js={len(js_map)} cs={len(cs_map)}{tail} 意图；"
          f"crisis={len(js_crisis)} banned={len(js_banned)} negative={len(js_negative)} "
          f"bannedEthics={n_ethics} 词；角色/描述已逐字对拍）")


if __name__ == "__main__":
    main()
