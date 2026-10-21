-- 0004_conversion_queue.sql  转换队列（执行计划 T2-4）
--
-- 设计文档 5.2 的 conversion_tasks / conversion_templates 落地。
-- 与 DDL 原文有两处【刻意偏离】，都在下面写明了理由。
--
-- 幂等：全部 IF NOT EXISTS。
-- 纪律：一经发布不得修改，后续演进只追加 0005_*.sql。

CREATE TABLE IF NOT EXISTS conversion_tasks (
    id TEXT PRIMARY KEY,
    book_id TEXT REFERENCES books(id) ON DELETE SET NULL,
    batch_id TEXT,
    input_path TEXT NOT NULL,
    output_path TEXT NOT NULL,
    out_format TEXT NOT NULL,
    -- 偏离 1：设计文档此处是 REFERENCES conversion_templates(id)，
    -- 但内置模板来自代码（ConversionTemplates.cs）而不是这张表，
    -- 建了外键会让每一次入队都因外键失败。故只存 id 字符串。
    template_id TEXT,
    arguments_json TEXT NOT NULL DEFAULT '[]',
    status INTEGER NOT NULL DEFAULT 0,
    progress REAL,
    progress_kind INTEGER NOT NULL DEFAULT 0,
    error_code TEXT,
    error_message TEXT,
    retry_count INTEGER NOT NULL DEFAULT 0,
    created_at INTEGER NOT NULL,
    started_at INTEGER,
    finished_at INTEGER);

CREATE INDEX IF NOT EXISTS idx_tasks_status ON conversion_tasks(status, created_at);

-- 用户自定义转换模板。内置模板来自代码 ConversionTemplates.cs（见上面偏离 1），
-- 这张表留给将来「保存为自己的模板」用；T2-4 只建表，不写入。
CREATE TABLE IF NOT EXISTS conversion_templates (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    out_format TEXT NOT NULL,
    arguments_json TEXT NOT NULL DEFAULT '[]',
    description TEXT,
    is_builtin INTEGER NOT NULL DEFAULT 0,
    updated_at INTEGER NOT NULL);
