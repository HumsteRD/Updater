# Собирает src/Pereezd.Core/Catalog/winget-index.tsv.gz из индекса winget (source2.msix -> Public/index.db).
# Запуск: python -I build-winget-index.py <index.db> <out.tsv.gz>
# Скачать индекс: https://cdn.winget.microsoft.com/cache/source2.msix (это zip).
import sqlite3, sys, gzip, io
db, out = sys.argv[1], sys.argv[2]
con = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
meta = dict(con.execute("select name, value from metadata"))
pk = con.execute("select rowid, id, name from packages order by rowid").fetchall()
idx = {}
lines = [f"#\twinget-index\t{meta.get('lastwritetime','')}\t{len(pk)}"]
for i, (rowid, pid, name) in enumerate(pk):
    idx[rowid] = i
    # Переводы строк (в том числе одиночный \r) внутри имени разорвали бы строку файла.
    name = ' '.join((name or '').split())
    lines.append(f"P\t{pid}\t{name}")
def emit(kind, sql):
    seen = {}
    for key, package in con.execute(sql):
        if not key or package not in idx: continue
        seen.setdefault(key, set()).add(idx[package])
    for key in sorted(seen):
        k = ' '.join(key.split()) if any(ch in key for ch in '\t\r\n') else key
        lines.append(f"{kind}\t{k}\t" + ",".join(str(x) for x in sorted(seen[key])))
    return len(seen)
c = emit("C", "select lower(productcode), package from productcodes2")
n = emit("N", "select norm_name, package from norm_names2")
u = emit("U", "select norm_publisher, package from norm_publishers2")
pf = emit("F", "select lower(pfn), package from pfns2")
data = ("\n".join(lines) + "\n").encode("utf-8")
with gzip.open(out, "wb", compresslevel=9) as f:
    f.write(data)
print(f"packages={len(pk)} codes={c} names={n} publishers={u} pfns={pf} raw={len(data)} gz={__import__('os').path.getsize(out)}")
