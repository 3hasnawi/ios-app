using System.IO.Compression;
namespace SalaryWindows;
internal static class BookArchive
{
    public static void Export(string archivePath, Book book, string destination)
    {
        var temporary=destination+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var zip=ZipFile.OpenRead(archivePath))
            {
                var entry=zip.GetEntry($"books/{book.Id.ToString().ToUpperInvariant()}.pdf") ?? throw new InvalidDataException("ملف الكتاب غير موجود في النسخة.");
                if(entry.Length < 5 || entry.Length > 256L*1024*1024) throw new InvalidDataException("حجم ملف الكتاب غير صالح أو يتجاوز الحد المدعوم.");
                using var source=entry.Open();using var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None);
                var prefix=new byte[5];source.ReadExactly(prefix);
                if(!prefix.SequenceEqual("%PDF-"u8.ToArray())) throw new InvalidDataException("ملف الكتاب ليس PDF صالحاً.");
                output.Write(prefix);source.CopyTo(output);
                if(output.Length!=entry.Length) throw new InvalidDataException("ملف الكتاب غير مكتمل؛ لم يُستبدل الملف السابق.");
            }
            File.Move(temporary,destination,true);
        }
        finally { try { File.Delete(temporary); } catch { } }
    }
}
