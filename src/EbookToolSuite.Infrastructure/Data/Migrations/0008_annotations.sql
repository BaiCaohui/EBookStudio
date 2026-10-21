-- 0008_annotations.sql  T6-5 书签与笔记
--
-- 两张表照设计文档 5.2 的 DDL 落，只补了一列 page_index：
--   T6-2B 之后 PDF / 漫画的锚点是"页码 +（PDF 的）页内字符偏移"（设计文档 §6.5.2），
--   没有页码列就表达不了固定版式书的位置。EPUB / TXT 的这两列为 NULL。
--
-- location / anchor 存的是 JSON（与 reading_progress.location 同一套三级定位写法）：
--   location = ReaderLocator（chapterHref / chapterIndex / percentInChapter / scrollTop / scrollHeight）
--   anchor   = NoteAnchor（选区起止路径与偏移 + 章内比例 + PDF 页内字符区间）
-- 为什么不是多列：定位形态会演进而读侧要能"缺一级就退一级"，
-- 存 JSON 才能让老行在新代码里读得出来（ReaderLocator.TryParse 就是这种纪律）。
--
-- 高亮与批注共表（设计文档 5.2）：type 0 = 高亮（只存 quote），type 1 = 批注（还存 content）。
-- 颜色列先留着（默认 0），这一版 UI 只用一种高亮色，不做调色板。

CREATE TABLE IF NOT EXISTS bookmarks (
    id          TEXT PRIMARY KEY,
    book_id     TEXT NOT NULL REFERENCES books(id) ON DELETE CASCADE,
    spine_index INTEGER,
    page_index  INTEGER,
    location    TEXT,
    label       TEXT,
    created_at  INTEGER NOT NULL);

CREATE INDEX IF NOT EXISTS idx_bookmarks_book ON bookmarks(book_id, spine_index);

CREATE TABLE IF NOT EXISTS notes (
    id          TEXT PRIMARY KEY,
    book_id     TEXT NOT NULL REFERENCES books(id) ON DELETE CASCADE,
    spine_index INTEGER,
    page_index  INTEGER,
    anchor      TEXT,
    color       INTEGER NOT NULL DEFAULT 0,
    type        INTEGER NOT NULL DEFAULT 0,
    quote       TEXT,
    content     TEXT,
    created_at  INTEGER NOT NULL,
    updated_at  INTEGER);

CREATE INDEX IF NOT EXISTS idx_notes_book ON notes(book_id, spine_index);
