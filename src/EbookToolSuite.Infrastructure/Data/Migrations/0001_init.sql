-- 0001_init.sql  EBookStudio 首版 schema（VS1 最小集合：4 张表）
-- 本文件由设计文档 5.2 切片生成，列定义与之逐字一致。
-- 纪律：一经发布不得修改，后续演进只追加 0002_*.sql。

-- ===== 批次（必须最先建：books.import_batch_id 引用它）=====
CREATE TABLE IF NOT EXISTS import_batch (
    id TEXT PRIMARY KEY, source TEXT, started_at INTEGER NOT NULL,
    finished_at INTEGER, total INTEGER DEFAULT 0, succeeded INTEGER DEFAULT 0,
    failed INTEGER DEFAULT 0, note TEXT);

-- ===== 书 =====
CREATE TABLE IF NOT EXISTS books (
    id              TEXT PRIMARY KEY,
    title           TEXT NOT NULL,
    title_sort      TEXT,
    authors         TEXT,
    author_sort     TEXT,
    translators     TEXT,
    publisher       TEXT,
    published_date  TEXT,
    isbn            TEXT,
    language        TEXT,
    description     TEXT,
    series          TEXT,
    series_index    REAL,
    rating          INTEGER NOT NULL DEFAULT 0,
    reading_status  INTEGER NOT NULL DEFAULT 0,
    identifier      TEXT,
    word_count      INTEGER,
    chapter_count   INTEGER,
    cover_path      TEXT,
    damaged         INTEGER NOT NULL DEFAULT 0,
    import_batch_id TEXT REFERENCES import_batch(id) ON DELETE SET NULL,
    created_at      INTEGER NOT NULL,
    updated_at      INTEGER NOT NULL,
    last_read_at    INTEGER
);

CREATE TABLE IF NOT EXISTS book_formats (
    id                 TEXT PRIMARY KEY,
    book_id            TEXT NOT NULL REFERENCES books(id) ON DELETE CASCADE,
    format             TEXT NOT NULL,
    file_path          TEXT NOT NULL,
    file_size          INTEGER NOT NULL DEFAULT 0,
    full_hash          TEXT,
    quick_fingerprint  TEXT,
    file_modified_at   INTEGER,
    missing            INTEGER NOT NULL DEFAULT 0,
    added_at           INTEGER NOT NULL,
    UNIQUE(book_id, format),
    UNIQUE(file_path)
);

-- ===== 阅读 =====
CREATE TABLE IF NOT EXISTS reading_progress (
    book_id      TEXT PRIMARY KEY REFERENCES books(id) ON DELETE CASCADE,
    spine_index  INTEGER,
    page_index   INTEGER,
    total_pages  INTEGER,
    location     TEXT,          -- 三级定位：元素选择器/锚点
    fingerprint  TEXT,          -- 文本指纹（前20字+偏移比例）
    percent      REAL NOT NULL DEFAULT 0,
    updated_at   INTEGER NOT NULL
);

-- ===== 索引 =====
CREATE INDEX IF NOT EXISTS idx_formats_hash ON book_formats(full_hash);
CREATE INDEX IF NOT EXISTS idx_formats_fp   ON book_formats(quick_fingerprint);
CREATE INDEX IF NOT EXISTS idx_books_title    ON books(title);
CREATE INDEX IF NOT EXISTS idx_books_author   ON books(authors);
CREATE INDEX IF NOT EXISTS idx_books_lastread ON books(last_read_at DESC);
CREATE INDEX IF NOT EXISTS idx_books_status   ON books(reading_status);
