PRAGMA foreign_keys = ON;
CREATE TABLE authors (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL COLLATE NOCASE,
    email VARCHAR(320) UNIQUE,
    active BOOLEAN NOT NULL DEFAULT 1,
    joined DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
);
CREATE TABLE posts (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    author_id INTEGER NOT NULL REFERENCES authors(id) ON DELETE CASCADE,
    title TEXT NOT NULL,
    body TEXT,
    words INTEGER NOT NULL DEFAULT 0 CHECK (words >= 0),
    price NUMERIC(8,2) NOT NULL DEFAULT 0,
    price_with_tax REAL GENERATED ALWAYS AS (price * 1.2) STORED,
    published_at DATETIME,
    updated_at DATETIME
);
CREATE TABLE post_log (id INTEGER PRIMARY KEY, post_id INTEGER NOT NULL, event TEXT NOT NULL, at DATETIME NOT NULL DEFAULT (datetime('now')));
CREATE INDEX posts_unpublished ON posts (author_id) WHERE published_at IS NULL;
CREATE INDEX authors_lower_name ON authors (lower(name));

CREATE VIEW author_stats AS
SELECT a.id, a.name, ifnull(a.email, '(none)') AS email, count(p.id) AS posts, total(p.words) AS words,
       group_concat(p.title, '; ') AS titles, iif(a.active, 'yes', 'no') AS is_active,
       strftime('%Y-%m-%d', a.joined) AS joined_day
FROM authors a LEFT JOIN posts p ON p.author_id = a.id
GROUP BY a.id, a.name, a.email, a.active, a.joined;

CREATE VIEW recent_posts AS
SELECT p.id, p.title, p.published_at FROM posts p WHERE p.published_at >= datetime('now', '-30 days') AND p.title LIKE '%sql%';

CREATE TRIGGER posts_log_insert AFTER INSERT ON posts
BEGIN
    INSERT INTO post_log (post_id, event) VALUES (NEW.id, 'created');
END;

CREATE TRIGGER posts_validate BEFORE UPDATE OF words ON posts
WHEN NEW.words < OLD.words
BEGIN
    SELECT RAISE(ABORT, 'words cannot go down');
END;

CREATE TRIGGER posts_touch AFTER UPDATE OF title, body ON posts
BEGIN
    UPDATE posts SET updated_at = datetime('now') WHERE id = NEW.id;
END;

INSERT INTO authors (name, email) VALUES ('Ada Lovelace', 'ada@example.com'), ('Bob', NULL), ('Chloé "Tab	here"', 'chloe@example.com');
INSERT INTO posts (author_id, title, body, words, price, published_at) VALUES
 (1, 'Notes on sql engines', 'line1
line2 \ backslash', 120, 9.99, datetime('now', '-1 day')),
 (1, 'Draft', NULL, 0, 0, NULL),
 (3, 'Bonjour', 'unicode ✓', 42, 120.5, '2020-01-01 10:00:00');
UPDATE posts SET body = 'edited' WHERE id = 2;
