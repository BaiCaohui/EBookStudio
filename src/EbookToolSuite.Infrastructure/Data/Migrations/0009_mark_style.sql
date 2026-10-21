-- 0009_mark_style.sql  T6-5 补短：标记样式（高亮 / 下划线 / 波浪线 / 删除线）
--
-- 为什么是旁表而不是给 notes 加一列：
--   SQLite 的 `ALTER TABLE ... ADD COLUMN` **没有 IF NOT EXISTS**，迁移就不可重放；
--   而本仓库的 DatabaseTests 明确要求"user_version 归零后重放全部迁移不报错"。
--   新建一张 IF NOT EXISTS 的旁表天然幂等，已发布的 v8 库也能直接升上来。
--
-- 为什么单独一列而不是复用 color：
--   color 是设计文档 F-25 的"4 色高亮"，语义是颜色；style 是"哪种标记"，
--   两者互相独立（将来可以"绿色波浪线"）。塞进一列会让两个维度再也分不开。
--
-- 取值：0 高亮 / 1 下划线 / 2 波浪线 / 3 删除线。没有行的笔记按 0 读（COALESCE）。

CREATE TABLE IF NOT EXISTS note_style (
    note_id   TEXT PRIMARY KEY REFERENCES notes(id) ON DELETE CASCADE,
    style     INTEGER NOT NULL DEFAULT 0);
