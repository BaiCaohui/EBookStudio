-- 0005_operation_log.sql  操作日志（执行计划 T3-6）
--
-- operation_log：谁在什么时候对哪本书做了什么。活动中心的时间线、保存闭环的可追溯
--   都读它。book_id 刻意不加外键（书删了日志也要留着，否则历史就断了）。
--
-- 还原点**不建表**：权威来源就是每个还原点目录里的 manifest.json
--   （Data/Backup/RestorePoints/{pointId}/manifest.json）。还原点数量很少（受 1 GB
--   总上限与单本 5 份约束），扫盘比维护一张可能与磁盘不一致的索引表更安全。
--
-- 幂等：全部 IF NOT EXISTS。
-- 纪律：一经发布不得修改，后续演进只追加 0006_*.sql。

CREATE TABLE IF NOT EXISTS operation_log (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    at            INTEGER NOT NULL,        -- Unix 毫秒（UTC），与其它表口径一致
    operation     TEXT NOT NULL,           -- 见 Core/Common/OperationNames.cs
    book_id       TEXT,
    format_id     TEXT,
    restore_point TEXT,                    -- 关联的还原点 id（没有就是 NULL）
    summary       TEXT NOT NULL,           -- 一句话，直接显示给用户
    detail_json   TEXT                     -- 结构化细节（门槛结论、条目数、字节数）
);

CREATE INDEX IF NOT EXISTS idx_operation_log_at ON operation_log(at DESC);
CREATE INDEX IF NOT EXISTS idx_operation_log_book ON operation_log(book_id, at DESC);
