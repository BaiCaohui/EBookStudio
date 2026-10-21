-- 0002_duplicate_detection.sql  T2-2 模糊去重：可能重复组的两张记录表
-- 只新增表，不动旧表：SQLite 的 ALTER TABLE ADD COLUMN 没有 IF NOT EXISTS，会破坏「DDL 一律幂等」的纪律。
-- 纪律：一经发布不得修改，后续演进只追加 0003_*.sql。

-- ===== 已合并的副本（书库里默认隐藏，可恢复）=====
-- book_id      被标记为副本的那本（它的独有格式已改指 kept_book_id）
-- kept_book_id 合并后保留的那本
-- 主键即 book_id：一本书只能属于一个副本关系
CREATE TABLE IF NOT EXISTS duplicate_copy (
    book_id         TEXT PRIMARY KEY REFERENCES books(id) ON DELETE CASCADE,
    kept_book_id    TEXT NOT NULL REFERENCES books(id) ON DELETE CASCADE,
    marked_at       INTEGER NOT NULL);

CREATE INDEX IF NOT EXISTS idx_dup_copy_kept ON duplicate_copy(kept_book_id);

-- ===== 已忽略的重复组（不再出现在重复检测结果里）=====
-- member_ids = 组内书籍 id 排序后用逗号连接（不用书名：成员变了就是新组，
--              既不会漏掉新加入的重复书，也不会反复打扰已处理过的组）
CREATE TABLE IF NOT EXISTS duplicate_ignore (
    member_ids      TEXT PRIMARY KEY,
    ignored_at      INTEGER NOT NULL);

