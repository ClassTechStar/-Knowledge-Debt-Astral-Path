-- =============================================================================
--  知债：星穹学途（Knowledge Debt: Astral Path）· PostgreSQL 初始 DDL
--  依据：《技术方案》§20 数据与存储、§45 知识库、§46 用户画像、§19 扩展服务
--  口径：
--    · 服务端为唯一权威写方；客户端仅端上复算同一套纯函数（§C3）
--    · 所有时间为 UTC（timestamptz）
--    · 软删除与审计留痕：涉及 consent / profile 的表必须可追溯（§46.7 / §6.6）
--    · 表名与英文标识口径一致：一律使用 astralpath 前缀或语义表名，无旧名残留
-- =============================================================================

CREATE EXTENSION IF NOT EXISTS pgcrypto;   -- gen_random_uuid()
CREATE EXTENSION IF NOT EXISTS pg_trgm;    -- 全文/模糊检索辅助

-- ─────────────────────────────────────────────────────────────────────────────
--  1. 图包与知识点（graph-svc / pack-svc 只读权威）
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS graph_packs (
    pack_id        TEXT PRIMARY KEY,
    name           TEXT        NOT NULL,
    course_scope   TEXT        NOT NULL DEFAULT '',
    graph_version  INTEGER     NOT NULL,
    published_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    is_acyclic     BOOLEAN     NOT NULL DEFAULT TRUE,
    CHECK (graph_version > 0)
);

CREATE TABLE IF NOT EXISTS knowledge_nodes (
    pack_id     TEXT NOT NULL REFERENCES graph_packs(pack_id) ON DELETE CASCADE,
    kp_id       TEXT NOT NULL,
    name        TEXT NOT NULL,
    course      TEXT NOT NULL DEFAULT '',
    description TEXT NOT NULL DEFAULT '',
    PRIMARY KEY (pack_id, kp_id)
);

CREATE TABLE IF NOT EXISTS knowledge_edges (
    pack_id   TEXT NOT NULL REFERENCES graph_packs(pack_id) ON DELETE CASCADE,
    from_kp   TEXT NOT NULL,
    to_kp     TEXT NOT NULL,
    edge_type TEXT NOT NULL CHECK (edge_type IN ('prerequisite', 'transfer_gap')),
    weight    DOUBLE PRECISION NOT NULL DEFAULT 1.0 CHECK (weight > 0 AND weight <= 2),
    source    TEXT NOT NULL DEFAULT '',
    PRIMARY KEY (pack_id, from_kp, to_kp),
    CHECK (from_kp <> to_kp),                       -- 禁止自环（§C3 无环校验）
    FOREIGN KEY (pack_id, from_kp) REFERENCES knowledge_nodes(pack_id, kp_id) ON DELETE CASCADE,
    FOREIGN KEY (pack_id, to_kp)   REFERENCES knowledge_nodes(pack_id, kp_id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_edges_from ON knowledge_edges(pack_id, from_kp);

-- ─────────────────────────────────────────────────────────────────────────────
--  2. 掌握度与债边（mastery-svc / debt-svc 权威）
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS mastery_records (
    student_id     TEXT NOT NULL,
    kp_id          TEXT NOT NULL,
    graph_version  INTEGER NOT NULL,
    recent_acc     DOUBLE PRECISION NOT NULL,
    sev            DOUBLE PRECISION NOT NULL,
    self_conf      DOUBLE PRECISION NOT NULL,
    score          DOUBLE PRECISION NOT NULL,
    band           TEXT NOT NULL,
    attempt_count  INTEGER NOT NULL DEFAULT 0,
    last_attempt_at TIMESTAMPTZ,
    formula_version TEXT NOT NULL,
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (student_id, kp_id, graph_version)
);
CREATE INDEX IF NOT EXISTS idx_mastery_student ON mastery_records(student_id);

CREATE TABLE IF NOT EXISTS debt_edges (
    debt_id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    student_id         TEXT NOT NULL,
    from_kp            TEXT NOT NULL,
    to_kp              TEXT NOT NULL,
    graph_version      INTEGER NOT NULL,
    score_from         DOUBLE PRECISION NOT NULL,
    score_to           DOUBLE PRECISION NOT NULL,
    freq               INTEGER NOT NULL DEFAULT 0,
    days_since_last_error INTEGER NOT NULL DEFAULT 0,
    recency            DOUBLE PRECISION NOT NULL DEFAULT 0,
    impact             DOUBLE PRECISION NOT NULL DEFAULT 0,
    weight_ver         TEXT NOT NULL,
    status             TEXT NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'cleared')),
    cleared_at         TIMESTAMPTZ,
    created_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (student_id, from_kp, to_kp, graph_version)
);
CREATE INDEX IF NOT EXISTS idx_debt_student ON debt_edges(student_id, status);
COMMENT ON COLUMN debt_edges.status IS 'cleared 仅由 progress 路径写入（§C8）';

-- ─────────────────────────────────────────────────────────────────────────────
--  3. 计划、今日任务与尝试（planner-svc / progress-svc）
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS plans (
    plan_id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    student_id          TEXT NOT NULL,
    graph_version       INTEGER NOT NULL,
    day_budget_min      INTEGER NOT NULL,
    constraints_checked BOOLEAN NOT NULL DEFAULT FALSE,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at          TIMESTAMPTZ,
    CHECK (constraints_checked = TRUE)      -- §C4：未过约束不得返回 201
);

CREATE TABLE IF NOT EXISTS attempts (
    attempt_id   UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    student_id   TEXT NOT NULL,
    kp_id        TEXT NOT NULL,
    question_id  TEXT NOT NULL,
    plan_item_id TEXT,
    stem_hash    TEXT NOT NULL DEFAULT '',
    answer_kind  TEXT NOT NULL DEFAULT '',
    correct      BOOLEAN NOT NULL,
    self_conf    INTEGER NOT NULL CHECK (self_conf BETWEEN 1 AND 5),
    latency_ms   BIGINT NOT NULL DEFAULT 0,
    hints_used   INTEGER NOT NULL DEFAULT 0,
    occurred_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_attempts_student_time ON attempts(student_id, occurred_at DESC);

-- ─────────────────────────────────────────────────────────────────────────────
--  4. consent 与审计（consent-svc，强伦理）
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS consents (
    student_id   TEXT NOT NULL,
    teacher_id   TEXT NOT NULL,
    purpose      TEXT NOT NULL,
    state        TEXT NOT NULL CHECK (state IN ('granted', 'revoked')),
    allow_teacher BOOLEAN NOT NULL DEFAULT FALSE,
    granted_at   TIMESTAMPTZ,
    revoked_at   TIMESTAMPTZ,
    updated_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (student_id, teacher_id, purpose)
);
COMMENT ON TABLE consents IS '撤销即时失效并触发教师端缓存 purge（§6.6 URGENT）';

CREATE TABLE IF NOT EXISTS consent_audits (
    audit_id     UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    student_id   TEXT NOT NULL,
    actor_id     TEXT NOT NULL,
    actor_role   TEXT NOT NULL,
    action       TEXT NOT NULL CHECK (action IN ('grant', 'revoke')),
    purpose      TEXT NOT NULL,
    occurred_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    trace_id     TEXT NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS idx_consent_audit_student ON consent_audits(student_id, occurred_at DESC);

-- ─────────────────────────────────────────────────────────────────────────────
--  5. 知识库（§45）kb-svc / kb-search-svc
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS kb_documents (
    id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    title          TEXT NOT NULL,
    owner_user_id  TEXT NOT NULL,
    visibility     TEXT NOT NULL CHECK (visibility IN ('private', 'consented', 'course', 'public')),
    course_code    TEXT NOT NULL DEFAULT '',
    tags           TEXT[] NOT NULL DEFAULT '{}',
    status         TEXT NOT NULL CHECK (status IN ('parsing', 'ready', 'failed', 'archived')),
    char_count     INTEGER NOT NULL DEFAULT 0,
    graph_id       TEXT,
    published_version TEXT,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_kb_owner ON kb_documents(owner_user_id);
CREATE INDEX IF NOT EXISTS idx_kb_tags ON kb_documents USING GIN (tags);

CREATE TABLE IF NOT EXISTS kb_versions (
    doc_id      UUID NOT NULL REFERENCES kb_documents(id) ON DELETE CASCADE,
    version     TEXT NOT NULL,
    char_count  INTEGER NOT NULL DEFAULT 0,
    note        TEXT,
    created_by  TEXT NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (doc_id, version)
);
COMMENT ON TABLE kb_versions IS '不可变版本；发布/回滚只改 published_version 指针（§45.5）';

CREATE TABLE IF NOT EXISTS kb_chunks (
    chunk_id  TEXT PRIMARY KEY,
    doc_id    UUID NOT NULL REFERENCES kb_documents(id) ON DELETE CASCADE,
    idx       INTEGER NOT NULL,
    text      TEXT NOT NULL,
    UNIQUE (doc_id, idx)
);

-- ─────────────────────────────────────────────────────────────────────────────
--  6. 用户画像（§46）profile-svc
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS profile_tags (
    student_id  TEXT NOT NULL,
    tag         TEXT NOT NULL,
    domain      TEXT NOT NULL CHECK (domain NOT IN ('sensitive', 'crisis', 'semantic')),  -- 禁列域
    weight      DOUBLE PRECISION NOT NULL DEFAULT 1.0,
    opt_out     BOOLEAN NOT NULL DEFAULT FALSE,
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (student_id, tag)
);

CREATE TABLE IF NOT EXISTS profile_snapshots (
    snapshot_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    student_id  TEXT NOT NULL,
    snapshot_date DATE NOT NULL,
    features    JSONB NOT NULL DEFAULT '{}',
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (student_id, snapshot_date)
);

-- ─────────────────────────────────────────────────────────────────────────────
--  7. 扩展服务（§19）只存各自拥有的表
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS diffusion_runs (
    run_id        UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    student_id    TEXT NOT NULL,
    algo_version  TEXT NOT NULL,
    alpha         DOUBLE PRECISION NOT NULL,
    depth         INTEGER NOT NULL,
    truncation_bound DOUBLE PRECISION NOT NULL,
    result        JSONB NOT NULL,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS exam_risk_snapshots (
    snapshot_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    student_id  TEXT NOT NULL,
    exam_id     TEXT NOT NULL,
    expected_gain DOUBLE PRECISION NOT NULL,
    risk_band   TEXT NOT NULL,
    plan        JSONB NOT NULL DEFAULT '{}',
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS cohort_stats (
    stat_id     UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    course      TEXT NOT NULL DEFAULT '',
    k           INTEGER NOT NULL,
    sample_count INTEGER NOT NULL,
    suppressed  BOOLEAN NOT NULL DEFAULT TRUE,      -- 默认抑制（fail-closed）
    p25         DOUBLE PRECISION,
    p50         DOUBLE PRECISION,
    p75         DOUBLE PRECISION,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    CHECK (k >= 2)
);
COMMENT ON TABLE cohort_stats IS '仅存聚合；任何情况下不落个体明细（§19.3 强伦理）';

CREATE TABLE IF NOT EXISTS review_queue (
    queue_id   UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    student_id TEXT NOT NULL,
    kp_id      TEXT NOT NULL,
    due_at     TIMESTAMPTZ NOT NULL,
    interval_days INTEGER NOT NULL,
    state      TEXT NOT NULL DEFAULT 'suggested',   -- 建议态，不直写 mastery
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS forecasts (
    forecast_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    student_id  TEXT NOT NULL,
    days_ahead  INTEGER NOT NULL,
    expected_score DOUBLE PRECISION NOT NULL,
    lower_bound DOUBLE PRECISION NOT NULL,
    upper_bound DOUBLE PRECISION NOT NULL,
    band        TEXT NOT NULL,
    suppressed  BOOLEAN NOT NULL DEFAULT FALSE,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS lab_experiments (
    experiment_id    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name             TEXT NOT NULL,
    candidate_version TEXT NOT NULL,
    tolerance        DOUBLE PRECISION NOT NULL DEFAULT 1e-6,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS formula_versions (
    version      TEXT PRIMARY KEY,
    proposer     TEXT NOT NULL,
    approver     TEXT,
    state        TEXT NOT NULL DEFAULT 'registered' CHECK (state IN ('registered', 'promoted')),
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    CHECK (approver IS NULL OR proposer <> approver)   -- 双人审批：不可同一人
);

-- ─────────────────────────────────────────────────────────────────────────────
--  8. 审计与幂等
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS request_audit (
    trace_id    TEXT PRIMARY KEY,
    actor_id    TEXT NOT NULL DEFAULT '',
    actor_role  TEXT NOT NULL DEFAULT '',
    method      TEXT NOT NULL,
    path        TEXT NOT NULL,
    status      INTEGER NOT NULL,
    occurred_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_audit_time ON request_audit(occurred_at DESC);
