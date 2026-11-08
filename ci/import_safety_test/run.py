from pathlib import Path
import subprocess,tempfile
root=Path(__file__).resolve().parents[2]
s=(root/'SalaryApp/ExcelImporter.swift').read_text()
reader=s[s.index('private nonisolated func localName'):s.index('nonisolated final class SharedStringsReader')]+s[s.index('nonisolated final class WorksheetReader'):]
reader=reader.replace('nonisolated ','')
with tempfile.TemporaryDirectory() as temp:
 p=Path(temp)
 (p/'Reader.swift').write_text('import Foundation\n'+reader)
 (p/'main.swift').write_text('''import Foundation
func tr(_ a: String,_ b: String)->String { a }
enum ImportError: Error { case cannotOpen(String) }
let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
defer { try? FileManager.default.removeItem(at: root) }
func read(_ cell: String) throws -> [[String]] {
 let file = root.appendingPathComponent("sheet.xml")
 try Data(("<worksheet><sheetData><row>" + cell + "</row></sheetData></worksheet>").utf8).write(to: file)
 var rows: [[String]] = []
 try WorksheetReader.read(file, sharedStrings: ["وهمي"]) { rows.append($0) }
 return rows
}
precondition(try read("<c r=\\"A1\\" t=\\"s\\"><v>0</v></c>") == [["وهمي"]])
for cell in ["<c r=\\"A1\\" t=\\"s\\"><v>-1</v></c>", "<c r=\\"A1\\" t=\\"s\\"><v>2</v></c>", "<c r=\\"1\\"><v>2</v></c>", "<c r=\\"XFE1\\"><v>2</v></c>", "<c r=\\"ZZZZZZZZZZZZZZZZZZ1\\"><v>2</v></c>"] {
 do { _ = try read(cell); fatalError("Malformed Excel accepted") } catch { }
}
print("Malformed Excel references rejected safely")
'''.replace('precondition(try read(', 'let valid = try read(').replace(') == [["وهمي"]])', ')\nprecondition(valid == [["وهمي"]])'))
 subprocess.run(['swiftc','Reader.swift','main.swift','-o','test'],cwd=p,check=True)
 subprocess.run([str(p/'test')],cwd=p,check=True)
