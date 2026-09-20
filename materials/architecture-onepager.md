# 知债：星穹学途 · 一页架构

```text
[Web 演示台 / Desktop 复算 / Mobile 共享]
              │
         API Gateway 契约层（统一 /v1）
              │
   ┌──────────┼──────────┬─────────────┐
   │          │          │             │
ingest     diagnose    plan/today   consent/teacher
   │          │          │             │
   ▼          ▼          ▼             ▼
mastery    debt+narr   planner+coach  progress+consent
(score)    (impact)    (K1–K5)        (cleared 唯一入口)
   │          │          │             │
   └──────────┴──────────┴─────────────┘
              │
     AstralPath.Core 纯函数（金样 1e-6）
              │
     graph-packs/accounting-v1（36 KP / 51 边 / 无环）
```

## 伦理一页

- 默认教师不可见
- 学生本人可 grant/revoke
- revoke 即时 purge
- 非处分依据
- 禁词门禁 + 模板降级
- 数字不来自 LLM
