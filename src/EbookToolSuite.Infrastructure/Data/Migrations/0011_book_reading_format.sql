-- 0011_book_reading_format.sql  当前阅读格式 + 阅读进度的来源格式（用户 2026-10-21）
--
-- 为什么是旁表而不是 ALTER TABLE ADD COLUMN：
--   SQLite 的 ADD COLUMN 没有 IF NOT EXISTS，会破坏本仓库「DDL 一律幂等、迁移可重放」
--   的纪律（与 0006 / 0009 同样的理由，见 DatabaseTests.user_version_归零后重建）。
--   旁表用 CREATE TABLE IF NOT EXISTS，老库升上来、重放都不报错。
--
-- current_format   用户选的「当前阅读格式」（存储串，如 EPUB/PDF；NULL = 没选过，按默认优先级链）。
--                  用户 2026-10-21：多格式共存的书籍要能在详情页直接切换"点阅读打开哪个文件"。
-- progress_format  现有阅读进度是在哪个格式下保存的（存储串；NULL = 旧数据/未知）。
--                  换格式继续读时要靠它判断"章号还是页码"，从而决定按百分比换算还是按文字指纹精确找位。
-- updated_at       最近一次写入时间（毫秒时间戳）；只用于排障，不参与业务判断。

CREATE TABLE IF NOT EXISTS book_reading_format (
    book_id         TEXT PRIMARY KEY REFERENCES books(id) ON DELETE CASCADE,
    current_format  TEXT,
    progress_format TEXT,
    updated_at      INTEGER NOT NULL DEFAULT 0);
