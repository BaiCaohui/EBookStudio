import sqlite3

db = r"Data-Test\t65-acceptance\Library.db"
conn = sqlite3.connect("file:" + db + "?mode=ro", uri=True)
cur = conn.cursor()

print("notes with style:")
for row in cur.execute(
        "SELECT n.id, n.quote, n.type, COALESCE(s.style,0), n.color FROM notes n "
        "LEFT JOIN note_style s ON s.note_id = n.id ORDER BY n.created_at"):
    print(row)

conn.close()
