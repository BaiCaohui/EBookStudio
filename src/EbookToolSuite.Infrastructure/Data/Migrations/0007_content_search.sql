-- 0007_content_search.sql  T6-4 全文搜索（FTS5 + trigram）
--
-- 三张表：
--   content_chunk        正文切片（每章再按 ~800 字切），普通表：按 book_id 删得便宜
--   content_fts          FTS5 外部内容表（content='content_chunk'），tokenize='trigram'
--   content_index_state  每本书的索引状态：pending / indexing / done / failed / unsupported
--
-- 为什么 content_fts 用「外部内容表」而不是把 content_chunk 直接建成 fts5：
--   删一本书要能按 book_id 走 B 树索引删（fts5 的 UNINDEXED 列没有索引，删一本要全表扫）；
--   而且原文只存一份（content_chunk.body），FTS 表只存倒排索引（设计文档 5.x 的
--   content_fts 就是这种形态）。
--
-- 为什么 tokenize='trigram'（计划书 T6-4 的推荐方案，不是设计文档 5.x 的 unicode61）：
--   unicode61 把整句中文当一个 token，搜「电子书」命中不了「这是一本电子书」；
--   trigram 按 3 字符窗口切，能子串匹配。代价是索引体积约为原文 3-5 倍，
--   而且 **查询词少于 3 个字符时 MATCH 命中不了**——这个限制由服务层用 LIKE 兜底
--   （2 字中文词太常见，不能不处理）。两条路径都在 ContentRepository 里有注释。
--
-- 触发器维护外部内容表（SQLite 官方推荐的同步写法）：写 content_chunk 就自动同步 FTS。

CREATE TABLE IF NOT EXISTS content_chunk (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    book_id       TEXT NOT NULL REFERENCES books(id) ON DELETE CASCADE,
    spine_index   INTEGER NOT NULL,
    chapter_title TEXT,
    chapter_href  TEXT,
    body          TEXT NOT NULL,
    indexed_at    INTEGER NOT NULL);

CREATE INDEX IF NOT EXISTS idx_chunk_book ON content_chunk(book_id, spine_index);

CREATE VIRTUAL TABLE IF NOT EXISTS content_fts USING fts5(
    body,
    content='content_chunk',
    content_rowid='id',
    tokenize='trigram');

CREATE TRIGGER IF NOT EXISTS content_chunk_ai AFTER INSERT ON content_chunk BEGIN
    INSERT INTO content_fts(rowid, body) VALUES (new.id, new.body);
END;

CREATE TRIGGER IF NOT EXISTS content_chunk_ad AFTER DELETE ON content_chunk BEGIN
    INSERT INTO content_fts(content_fts, rowid, body) VALUES('delete', old.id, old.body);
END;

CREATE TRIGGER IF NOT EXISTS content_chunk_au AFTER UPDATE ON content_chunk BEGIN
    INSERT INTO content_fts(content_fts, rowid, body) VALUES('delete', old.id, old.body);
    INSERT INTO content_fts(rowid, body) VALUES (new.id, new.body);
END;

CREATE TABLE IF NOT EXISTS content_index_state (
    book_id      TEXT PRIMARY KEY REFERENCES books(id) ON DELETE CASCADE,
    status       TEXT NOT NULL,
    chunk_count  INTEGER NOT NULL DEFAULT 0,
    error        TEXT,
    updated_at   INTEGER NOT NULL);
