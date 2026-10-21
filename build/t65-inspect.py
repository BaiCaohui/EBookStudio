import sqlite3

db = r"publish\Data\Library.db"
conn = sqlite3.connect("file:" + db + "?mode=ro", uri=True)
cur = conn.cursor()

print("user_version:", cur.execute("PRAGMA user_version").fetchone()[0])
print("notes cols:", [d[1] for d in cur.execute("PRAGMA table_info(notes)")])
print("tables:", [r[0] for r in cur.execute(
    "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name")])

print("\n-- note_style --")
for row in cur.execute("SELECT * FROM note_style"):
    print(row)

print("\n-- notes (book_id, type, quote, content) --")
for row in cur.execute("SELECT id, book_id, type, quote, content FROM notes LIMIT 20"):
    print(row)

print("\n-- join: notes with style --")
for row in cur.execute(
        "SELECT n.quote, n.type, COALESCE(s.style, 0) FROM notes n "
        "LEFT JOIN note_style s ON s.note_id = n.id LIMIT 20"):
    print(row)

conn.close()
