#eval/synth 说明

## synth-30

30 名合成学生：

- 20 名预埋债边画像（先修弱 + 后继出错频次）
- 10 名健康对照（无债）

评测口径：

```text
recall = |predicted ∩ ground_truth| / |ground_truth|
门禁：recall ≥ 0.80
ground_truth：会计学图包标注债边（含跨课 transfer_gap）
```

当前 `GraphAndRecallTests.Recall_OnSeededDebts_IsAtLeast080` 已通过。

## 金样例库

| 文件 | 覆盖 |
|---|---|
| eval/golden/score.json | 掌握度公式 |
| eval/golden/impact.json | 债边 impact 与门禁 |
| eval/golden/plan_k.json | K1/K3 约束 |
| eval/golden/sale.json | 销账状态机 |
| eval/golden/narrative.json | 禁词与槽位校验 |

CI 门禁：任一金样失败即阻断合并。
