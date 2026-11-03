#!/usr/bin/env python3
"""Builds the Badger SQL Migrate pages for lonebadger.tech/badger-sql-migrate/.

The site (badger-website repo, site/) is plain static HTML served by Azure Static Web Apps with a CSP that
forbids inline styles and scripts, so this generator emits static pages that use /styles.css plus
/badger-sql-migrate/docs.css, and highlights code at build time.

Usage:
  python3 docs/website/build.py <site-root>   e.g. ../badger-website/site
Download sizes and SHA-256 hashes are read from artifacts/SHA256SUMS when present (build/publish.sh).
"""
import html
import os
import re
import shutil
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
sys.path.insert(0, HERE)

VERSION = "0.3.0"
RELEASE_DATE = "November 2026"
BASE = "/badger-sql-migrate"
# Where release files are downloaded from: the public Azure blob container build/upload-release.sh fills.
DOWNLOAD_BASE = f"https://lonebadgerdownloads.blob.core.windows.net/badger-sql-migrate/v{VERSION}"
FOOTER = ("© 2026 Lone Badger Tech, LLC. Badger SQL Migrate is free software under the <a href='/badger-sql-migrate/license/'>MIT license</a>. "
          "Microsoft, SQL Server, Azure and Microsoft Entra are trademarks of the Microsoft group of companies; "
          "other product names are trademarks of their respective owners. Badger SQL Migrate is not affiliated "
          "with or endorsed by Microsoft or the makers of the other tools mentioned.")

# ---------------------------------------------------------------- code highlighting

SQL_KEYWORDS = set("""
ADD ALL ALTER AND ANY AS ASC BEGIN BETWEEN BY CASCADE CASE CATCH CHECK COLLATE COLUMN COMMIT CONSTRAINT CREATE
CROSS CURRENT DATABASE DEFAULT DELETE DESC DISTINCT DROP ELSE ENCRYPTED END EXEC EXECUTE EXISTS FOREIGN FROM FULL
FUNCTION GO GRANT GROUP HAVING IF IN INCLUDE INDEX INNER INSERT INTO IS JOIN KEY LEFT LIKE MATCHED MERGE NOCOUNT NOT
NULL OBJECT_ID OFFLINE ON ONLINE OR ORDER OUTER PRIMARY PRINT PROCEDURE REFERENCES RETURN RETURNS REVOKE ROLE
ROLLBACK SCHEMA SELECT SET TABLE THEN THROW TO TOP TRAN TRANSACTION TRIGGER TRY UNION UNIQUE UPDATE USING VALUES
VIEW WHEN WHERE WHILE WITH ALTER OR SCHEMABINDING NONCLUSTERED CLUSTERED IDENTITY OUTPUT RAISERROR DECLARE
""".split())

TOKEN = re.compile(
    r"(?P<c>--[^\n]*|/\*.*?\*/)"
    r"|(?P<s>N?'(?:[^']|'')*')"
    r"|(?P<v>\{\{?[A-Za-z_][\w.]*\}\}?|\$\([A-Za-z_][\w.]*\)|\$\{[a-z]+:[^}]+\})"
    r"|(?P<w>[A-Za-z_@#][\w@#$]*)",
    re.S,
)
YAML_TOKEN = re.compile(
    r"(?P<c>#[^\n]*)"
    r"|(?P<s>\"[^\"\n]*\"|'[^'\n]*')"
    r"|(?P<v>\$\{[a-z]+:[^}]+\}|\{[A-Za-z_][\w.]*\})"
    r"|(?P<k>^\s*-?\s*[\w .-]+?(?=:(\s|$)))",
    re.M,
)
SHELL_TOKEN = re.compile(r"(?P<c>#[^\n]*)|(?P<s>\"[^\"\n]*\"|'[^'\n]*')|(?P<k>^\s*(?:bsm|dotnet|tar|curl|xattr|shasum|sha256sum|Get-FileHash|kinit|az|chmod|sudo|mv|Expand-Archive|git)\b)", re.M)


def highlight(lang, text):
    if lang == "sql":
        pattern = TOKEN
    elif lang == "yaml":
        pattern = YAML_TOKEN
    elif lang in ("bash", "shell", "powershell"):
        pattern = SHELL_TOKEN
    else:
        return html.escape(text)
    out, last = [], 0
    for m in pattern.finditer(text):
        out.append(html.escape(text[last:m.start()]))
        kind = m.lastgroup
        value = m.group(0)
        if kind == "w":
            out.append(f'<span class="tok-k">{html.escape(value)}</span>' if value.upper() in SQL_KEYWORDS else html.escape(value))
        else:
            cls = {"c": "tok-c", "s": "tok-s", "v": "tok-v", "k": "tok-k"}[kind]
            out.append(f'<span class="{cls}">{html.escape(value)}</span>')
        last = m.end()
    out.append(html.escape(text[last:]))
    return "".join(out)


def code(text, lang="", file=None):
    text = text.strip("\n")
    label = f'<span class="file">{html.escape(file)}</span>' if file else ""
    return f'{label}<pre><code>{highlight(lang, text)}</code></pre>'


def sql(text, file=None):
    return code(text, "sql", file)


def yaml(text, file=None):
    return code(text, "yaml", file)


def sh(text, file=None):
    return code(text, "bash", file)


# ---------------------------------------------------------------- layout

PRODUCT_NAV = [
    ("Badger SQL Migrate", f"{BASE}/"),
    ("Docs", f"{BASE}/docs/"),
    ("Examples", f"{BASE}/examples/"),
    ("Download", f"{BASE}/download/"),
    ("What's New", f"{BASE}/releases/"),
]


class Page:
    def __init__(self, path, title, description, body, sidebar=None, wide=False, heading=None, lede=None):
        self.path = path  # relative to BASE, e.g. "docs/concepts/"
        self.title = title
        self.description = description
        self.body = body
        self.sidebar = sidebar
        self.wide = wide or sidebar is not None
        self.heading = heading
        self.lede = lede


def nav_html():
    return "".join(f'<a href="{href}">{html.escape(label)}</a>' for label, href in PRODUCT_NAV)


def sidebar_html(groups, current):
    parts = ['<aside class="sidebar" aria-label="Documentation">']
    for group, items in groups:
        parts.append(f"<h4>{html.escape(group)}</h4><ul>")
        for label, path in items:
            cls = ' class="current" aria-current="page"' if path == current else ""
            parts.append(f'<li><a href="{BASE}/{path}"{cls}>{html.escape(label)}</a></li>')
        parts.append("</ul>")
    parts.append("</aside>")
    return "".join(parts)


def pager_html(groups, current):
    flat = [item for _, items in groups for item in items]
    paths = [p for _, p in flat]
    if current not in paths:
        return ""
    i = paths.index(current)
    prev = f'<a href="{BASE}/{flat[i - 1][1]}">← {html.escape(flat[i - 1][0])}</a>' if i > 0 else "<span></span>"
    nxt = f'<a href="{BASE}/{flat[i + 1][1]}">{html.escape(flat[i + 1][0])} →</a>' if i + 1 < len(flat) else "<span></span>"
    return f'<nav class="pager" aria-label="Pages">{prev}{nxt}</nav>'


def render(page):
    heading = ""
    if page.heading:
        heading = f"<h1>{page.heading}</h1>"
        if page.lede:
            heading += f'<p class="lede">{page.lede}</p>'
    if page.sidebar:
        main = (f'<div class="layout">{sidebar_html(page.sidebar, page.path)}'
                f'<article class="content">{heading}{page.body}{pager_html(page.sidebar, page.path)}</article></div>')
    else:
        main = heading + page.body
    body_class = ' class="wide"' if page.wide else ""
    return f"""<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{html.escape(page.title)}</title>
<meta name="description" content="{html.escape(page.description)}">
<link rel="stylesheet" href="/styles.css">
<link rel="stylesheet" href="{BASE}/docs.css">
<link rel="icon" href="{BASE}/icon.svg" type="image/svg+xml">
</head>
<body{body_class}>
<header>
  <a href="/">Lone Badger Tech</a>
  <nav>{nav_html()}</nav>
</header>
<main>
{main}
</main>
<footer>{FOOTER}</footer>
</body>
</html>
"""


def read_artifacts():
    sums = os.path.join(REPO, "artifacts", "SHA256SUMS")
    result = {}
    if os.path.exists(sums):
        for line in open(sums):
            digest, name = line.split(maxsplit=1)
            name = name.strip().lstrip("*")
            path = os.path.join(REPO, "artifacts", name)
            size = os.path.getsize(path) if os.path.exists(path) else 0
            result[name] = (digest, size)
    return result


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    site = os.path.abspath(sys.argv[1])
    out = os.path.join(site, BASE.strip("/"))
    if os.path.isdir(out):
        shutil.rmtree(out)
    os.makedirs(out)

    import pages  # noqa: E402  (content lives in pages.py)

    for page in pages.all_pages(read_artifacts()):
        target = os.path.join(out, page.path, "index.html")
        os.makedirs(os.path.dirname(target), exist_ok=True)
        with open(target, "w", encoding="utf-8", newline="\n") as f:
            f.write(render(page))
        print("wrote", os.path.relpath(target, site))
    for asset in ("docs.css", "icon.svg"):
        shutil.copy(os.path.join(HERE, asset), os.path.join(out, asset))
        print("wrote", os.path.join(BASE.strip("/"), asset))


if __name__ == "__main__":
    main()
