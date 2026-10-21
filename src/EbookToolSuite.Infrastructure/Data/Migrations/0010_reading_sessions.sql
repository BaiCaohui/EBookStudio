-- 0010_reading_sessions.sql  T7-3B 阅读统计：阅读会话表
--
-- 为什么新增一张表而不是在 reading_progress 上加列：
--   reading_progress 是"每本一行、只存当前位置"，没有时长数据；统计要的是
--   "一次打开 = 一条会话"的时间序列。两者语义不同，混在一张表里会互相拖累
--   （reading_progress 的定位恢复逻辑一行都不动）。
--
-- 判据口径（写进 docs/DECISIONS.md，以后不再改）：
--   时长 = ended_at - started_at；ended_at 是"最后一次有阅读事件的时间"，
--   不是关窗时间——挂机不计时。跨本地日界的会话由聚合层按日拆分。
--   pages_read 是翻页数（分页 / PDF），chars_read 是按"本章字数 × 章内进度增量"
--   估算的字数（PDF / 漫画没有字数，只记页数，chars_read 保持 0）。
--
-- 删除书籍 -> 会话随 ON DELETE CASCADE 一起删（不提供"保留已删书统计"选项）。
--
-- 幂等：全部 IF NOT EXISTS。纪律：一经发布不得修改，后续演进只追加 0011_*.sql。

CREATE TABLE IF NOT EXISTS reading_sessions (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    book_id     TEXT NOT NULL REFERENCES books(id) ON DELETE CASCADE,
    started_at  INTEGER NOT NULL,                    -- Unix 毫秒（UTC），与其它表口径一致
    ended_at    INTEGER NOT NULL,                    -- 最后一次阅读事件的时间
    pages_read  INTEGER NOT NULL DEFAULT 0,
    chars_read  INTEGER NOT NULL DEFAULT 0);

CREATE INDEX IF NOT EXISTS idx_reading_sessions_book ON reading_sessions(book_id, started_at);
CREATE INDEX IF NOT EXISTS idx_reading_sessions_started ON reading_sessions(started_at);
