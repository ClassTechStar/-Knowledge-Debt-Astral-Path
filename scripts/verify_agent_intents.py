# 智能体意图登记表 · 三方对拍（P2 单一事实源门禁）
# ============================================================
# 唯一事实源：tools/agent-intents.json
# 对拍对象：
#   ① deploy/monolith-web/index.html 的 CRISIS/BANNED/NEGATIVE/INTENTS 字面量
#      （三端同源，另两份镜像由 sync-monolith-html.ps1 保证 md5 一致）
#   ② src/AstralPath.Core/Agent/AgentRouter.cs 的 DefaultAgentIntents.Table
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


def main():
    if not INTENTS_JSON.exists():
        fail(f"缺少 {INTENTS_JSON}")
    canon = json.loads(INTENTS_JSON.read_text(encoding="utf-8"))

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

    js_map = {i["id"]: i["anchors"] for i in js_intents}
    canon_map = {i["id"]: i["anchors"] for i in canon["intents"]}
    if list(js_map) != list(canon_map):
        fail(f"意图清单不一致\n  仅 json 有: {sorted(set(canon_map) - set(js_map))}\n  仅 js 有: {sorted(set(js_map) - set(canon_map))}")
    for k in canon_map:
        if js_map[k] != canon_map[k]:
            fail(f"意图 {k} 锚点不一致\n  json={canon_map[k]}\n  js={js_map[k]}")

    cs = AGENT_ROUTER.read_text(encoding="utf-8")
    table = re.search(r"DefaultAgentIntents[\s\S]*?Table\s*=\s*\{([\s\S]*?)\};", cs)
    if not table:
        fail("AgentRouter.cs 中找不到 DefaultAgentIntents.Table")
    entries = re.findall(
        r'new\(\s*(\d+)\s*,\s*"([a-z.]+)"\s*,\s*"(\w+)"\s*,\s*new\[\]\s*\{([^}]*)\}\s*,\s*(?:Array\.Empty<string>\(\)|new\[\]\s*\{[^}]*\})\s*,\s*"([^"]*)"\s*\)',
        table.group(1),
    )
    if not entries:
        fail("DefaultAgentIntents.Table 解析出 0 条意图（正则与代码结构不符）")
    cs_map = {}
    for _bit, intent_id, _role, anchors_raw, _desc in entries:
        anchors = re.findall(r'"([^"]*)"', anchors_raw)
        cs_map[intent_id] = anchors
    if list(cs_map) != list(canon_map):
        fail(f"C# 意图清单与 JSON 不一致\n  仅 json 有: {sorted(set(canon_map) - set(cs_map))}\n  仅 cs 有: {sorted(set(cs_map) - set(canon_map))}")
    for k in canon_map:
        if cs_map[k] != canon_map[k]:
            fail(f"意图 {k} 锚点 C# 与 JSON 不一致\n  json={canon_map[k]}\n  cs={cs_map[k]}")

    print(f"ALL GREEN · agent-intents 三方一致（json={len(canon_map)} js={len(js_map)} cs={len(cs_map)} 意图；"
          f"crisis={len(js_crisis)} banned={len(js_banned)} negative={len(js_negative)} 词）")


if __name__ == "__main__":
    main()
