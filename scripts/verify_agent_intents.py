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
FORMULA_WEIGHTS = ROOT / "src" / "AstralPath.Core" / "Formula" / "FormulaWeights.cs"


def fail(msg):
    print(f"[DRIFT] {msg}")
    print("FAILED: agent-intents 对拍不一致")
    sys.exit(1)


def extract_js_array(source, name):
    m = re.search(r"const " + name + r"=(\[.*?\]);", source, re.S)
    if not m:
        fail(f"index.html 中找不到 const {name} 字面量")
    try:
        return json.loads(m.group(1))
    except json.JSONDecodeError as e:
        fail(f"const {name} 不是严格 JSON（{e}）；请保持双引号、无尾逗号")


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
    table = re.search(r"DefaultAgentIntents[\s\S]*?Table\s*=\s*\{([\s\S]*?)\n    \};", cs)
    if not table:
        fail("AgentRouter.cs 中找不到 DefaultAgentIntents.Table")
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

    n_ethics = len(canon.get("bannedEthics", []))
    print(f"ALL GREEN · agent-intents 四方一致（json={len(canon_map)} js={len(js_map)} cs={len(cs_map)} 意图；"
          f"crisis={len(js_crisis)} banned={len(js_banned)} negative={len(js_negative)} "
          f"bannedEthics={n_ethics} 词；角色/描述已逐字对拍）")


if __name__ == "__main__":
    main()
