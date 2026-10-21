-- 0006_conversion_task_priority.sql  T6-1 MOBI/AZW3 惰性转 EPUB 缓存
--
-- 为什么用旁表而不是 ALTER TABLE ADD COLUMN：
-- SQLite 的 ADD COLUMN 没有 IF NOT EXISTS，会破坏本项目「DDL 一律幂等」的纪律
-- （见 0002 开头的说明与 DatabaseTests.user_version_归零后重建）。
-- 旁表用 CREATE TABLE IF NOT EXISTS，重跑安全；老行没有 meta 就按默认值解释。
--
-- priority        队列优先级，越大越先跑（T6-1 判据 5：点开一本书触发的转换要插队）。
-- register_format 产物是否登记进 book_formats；阅读缓存转换为 0，否则打开一本 MOBI
--                 会给书库凭空加一个 EPUB 格式行。

CREATE TABLE IF NOT EXISTS conversion_task_meta (
    task_id         TEXT PRIMARY KEY REFERENCES conversion_tasks(id) ON DELETE CASCADE,
    priority        INTEGER NOT NULL DEFAULT 0,
    register_format INTEGER NOT NULL DEFAULT 1);

CREATE INDEX IF NOT EXISTS idx_task_meta_priority ON conversion_task_meta(priority DESC);