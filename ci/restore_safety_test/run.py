from pathlib import Path
import subprocess,tempfile
root=Path(__file__).resolve().parents[2]
s=(root/'SalaryApp/RestoreSafety.swift').read_text().replace('nonisolated ','')
a=s.index('    static var root: URL');b=s.index('    static var pending:',a)
s=s[:a]+'    static var root: URL { testRoot.appendingPathComponent("journal") }\n'+s[b:]
with tempfile.TemporaryDirectory() as temp:
 p=Path(temp);(p/'Recovery.swift').write_text(s)
 (p/'main.swift').write_text('''import Foundation
import SQLite3
let testRoot=FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
try FileManager.default.createDirectory(at: testRoot, withIntermediateDirectories: true)
defer { try? FileManager.default.removeItem(at: testRoot) }
enum OfficialBookFiles { static var root: URL { testRoot.appendingPathComponent("books") } }
let store=testRoot.appendingPathComponent("test.sqlite")
var db: OpaquePointer?
precondition(sqlite3_open(store.path,&db) == SQLITE_OK)
precondition(sqlite3_exec(db,"PRAGMA journal_mode=WAL; CREATE TABLE sample(value TEXT); INSERT INTO sample VALUES ('original');",nil,nil,nil) == SQLITE_OK)
try FileManager.default.createDirectory(at: OfficialBookFiles.root,withIntermediateDirectories: true)
let book=OfficialBookFiles.root.appendingPathComponent("fictional.txt")
try Data("original book".utf8).write(to:book)
fputs("prepare checkpoint\\n", stderr)
try RestoreSafety.prepare(store:store)
precondition(RestoreSafety.pending)
precondition(sqlite3_exec(db,"UPDATE sample SET value='partial';",nil,nil,nil) == SQLITE_OK)
try Data("partial book".utf8).write(to:book)
sqlite3_close(db)
fputs("recover checkpoint\\n", stderr)
try RestoreSafety.recoverIfNeeded()
precondition(!RestoreSafety.pending)
precondition(try String(contentsOf:book,encoding:.utf8) == "original book")
precondition(sqlite3_open(store.path,&db) == SQLITE_OK)
var stmt: OpaquePointer?
precondition(sqlite3_prepare_v2(db,"SELECT value FROM sample",-1,&stmt,nil) == SQLITE_OK)
precondition(sqlite3_step(stmt) == SQLITE_ROW)
precondition(String(cString:sqlite3_column_text(stmt,0)) == "original")
sqlite3_finalize(stmt);sqlite3_close(db)
try RestoreSafety.recoverIfNeeded()
print("Interrupted restore rolled back WAL database and books; retry is idempotent")
'''.replace('precondition(try String(contentsOf:book,encoding:.utf8) == "original book")','let restoredBook = try String(contentsOf:book,encoding:.utf8)\nprecondition(restoredBook == "original book")'))
 subprocess.run(['swiftc','Recovery.swift','main.swift','-o','test'],cwd=p,check=True)
 subprocess.run([str(p/'test')],cwd=p,check=True)
