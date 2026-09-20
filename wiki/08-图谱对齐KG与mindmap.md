# 知识图谱对齐说明

参考仓库：
- `C:\Users\18948\Documents\GitHub\KnowledgeGraph`（三元组 / RDF / 属性图 → Neo4j）
- `C:\Users\18948\Documents\GitHub\mind-map`（simple-mind-map 逻辑结构图 / Markdown 大纲）

## 对齐成果

| 来源 | 本项目实现 |
|---|---|
| KnowledgeGraph · 知识表示 | `triples[]`：subject–predicate–object |
| KnowledgeGraph · 图存储 | `propertyGraph`：节点 labels + relationships（Neo4j 风格） |
| mind-map · 数据结构 | `mindmap`：`{data:{text}, children:[...]}` 树 |
| mind-map · 布局 | 前端「逻辑结构图」：根左、子右、纵向堆叠（LogicalStructure） |
| mind-map · Markdown | `markdownOutline`：`# 章 / ## 节 / ### 小节` |

## 谓词映射（示例）

```text
〈第1章〉 --隶属于章节--> 〈1.1 Go语言简介〉
〈第1章〉 --后继章节--> 〈第2章〉
〈第1章〉 --涉及关键词--> 〈并发〉
〈并发〉 --共现相关--> 〈goroutine〉
```

## 界面（UI-kg-ref-v6）

- **布局**：分层图谱 ↔ 逻辑结构图（思维导图）
- **大纲**：展示 Markdown 目录树
- **三元组**：展示 RDF 风格关系列表

## 接口

`GET /v1/knowledge-graphs/{graphId}` 额外返回：

```json
{
  "triples": [...],
  "propertyGraph": { "nodes": [...], "relationships": [...] },
  "mindmap": { "data": { "text": "..." }, "children": [...] },
  "markdownOutline": "# ...\n## ..."
}
```
