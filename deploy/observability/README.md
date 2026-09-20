# 可观测性接入（§25）

**三支柱**
| 支柱 | 方案 | 接入点 |
|---|---|---|
| 日志 | 结构化日志（Serilog 风格字段）→ OTLP → 采集端 | `OpenTelemetry__OtlpEndpoint` 留空则仅控制台 |
| 指标 | Prometheus 抓取 `/metrics` | Deployment 注解 `prometheus.io/scrape` |
| 追踪 | OpenTelemetry 链路追踪 → OTLP | `OpenTelemetry__ServiceName` 按服务区分 |

**文件**
- `otel-collector.yaml`：Collector 接收/处理/导出（指标→Prometheus，追踪→Tempo）
- `prometheus-rules.yaml`：告警规则（未就绪、5xx 错误率、P95 延迟）

**落地状态说明**
配置已就位并在 K8s 清单中通过环境变量与注解接入；本地无 K8s 集群，因此：
清单仅完成**客户端语法校验**，未经集群内 schema 校验与实际部署验证。
