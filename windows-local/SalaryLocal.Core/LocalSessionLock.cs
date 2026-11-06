using System.Security.Cryptography;
using System.Text.Json;

namespace SalaryLocal.Core;

/// قفل جلسة فقط؛ لا يشفّر الملفات ولا يمنع صاحب حساب ويندوز من تغيير الإعدادات.
public sealed class LocalSessionLock
{
    public const string Warning = "قفل جلسة فقط، وليس تشفيراً للبيانات. صاحب حساب Windows يستطيع تغيير ملف القفل. احمِ حساب Windows والقرص والنسخ الاحتياطية؛ لا توجد استعادة لكلمة السر داخل التطبيق.";
    private const int Iterations = 600_000;
    private readonly string path;
    private readonly TimeProvider clock;
    private Settings? settings;
    private long lastActivity, blockedAt;
    private int failures;
    private bool blocked;
    public bool Enabled => settings is not null;
    public bool IsLocked { get; private set; }
    public int IdleMinutes => settings?.IdleMinutes ?? 5;
    private sealed record Settings(int Version, string Salt, string Hash, int Iterations, int IdleMinutes);

    public LocalSessionLock(string path, TimeProvider? clock = null)
    {
        this.path = Path.GetFullPath(path); this.clock = clock ?? TimeProvider.System;
        lastActivity = this.clock.GetTimestamp();
        try
        {
            using var stream = new FileStream(this.path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 8192) throw new InvalidDataException();
            settings = JsonSerializer.Deserialize<Settings>(stream) ?? throw new InvalidDataException();
            if (string.IsNullOrWhiteSpace(settings.Salt) || string.IsNullOrWhiteSpace(settings.Hash) || settings.Version != 1 || settings.Iterations != Iterations || settings.IdleMinutes is < 1 or > 120 || Convert.FromBase64String(settings.Salt).Length != 32 || Convert.FromBase64String(settings.Hash).Length != 32) throw new InvalidDataException();
        }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        catch (Exception e) when (e is JsonException or FormatException or InvalidDataException or ArgumentException)
        { throw new InvalidDataException("ملف قفل الجلسة تالف أو غير مدعوم؛ لم يُتجاوز القفل ولم تُعدّل البيانات.", e); }
        IsLocked = true;
    }

    public void Enable(string password, int idleMinutes)
    {
        if (Enabled) throw new InvalidOperationException("القفل مفعّل؛ استخدم تغيير كلمة السر.");
        var next = Create(password, idleMinutes); Save(next); settings = next; RecordActivity();
    }
    public bool ChangePassword(string currentPassword, string newPassword, int idleMinutes)
    {
        RequireUnlocked();
        if (!Verify(currentPassword)) return false;
        var next = Create(newPassword, idleMinutes); Save(next); settings = next; RecordActivity(); return true;
    }
    public bool Disable(string currentPassword)
    {
        RequireUnlocked(); if (!Verify(currentPassword)) return false;
        File.Delete(path); settings = null; IsLocked = false; return true;
    }
    private void RequireUnlocked()
    {
        if (!Enabled || IsLocked) throw new InvalidOperationException("افتح الجلسة أولاً قبل تغيير إعدادات القفل.");
    }
    public void RecordActivity() { if (!IsLocked) lastActivity = clock.GetTimestamp(); }
    public bool CheckIdle()
    {
        if (Enabled && !IsLocked && clock.GetElapsedTime(lastActivity) >= TimeSpan.FromMinutes(IdleMinutes)) Lock();
        return IsLocked;
    }
    public void Lock() { if (Enabled) IsLocked = true; }
    public bool TryUnlock(string password)
    {
        if (!Enabled || !IsLocked) return false;
        if (!Verify(password)) return false;
        IsLocked = false; RecordActivity(); return true;
    }
    private bool Verify(string password)
    {
        if (blocked && clock.GetElapsedTime(blockedAt) < TimeSpan.FromSeconds(30)) return false;
        if (blocked) { blocked = false; failures = 0; }
        if (settings is null || password.Length > 1024) return Failure();
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(settings.Salt), Iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(settings.Hash))) return Failure();
            failures = 0; blocked = false; return true;
        }
        finally { CryptographicOperations.ZeroMemory(hash); }
    }
    private bool Failure() { if (++failures >= 5) { blocked = true; blockedAt = clock.GetTimestamp(); } return false; }
    private static Settings Create(string password, int idleMinutes)
    {
        if (password.Length is < 8 or > 1024 || string.IsNullOrWhiteSpace(password)) throw new InvalidDataException("اختر كلمة سر من 8 إلى 1024 حرفاً، وليست فراغات فقط.");
        if (idleMinutes is < 1 or > 120) throw new InvalidDataException("مدة القفل بين دقيقة و120 دقيقة.");
        var salt = RandomNumberGenerator.GetBytes(32);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        try { return new(1, Convert.ToBase64String(salt), Convert.ToBase64String(hash), Iterations, idleMinutes); }
        finally { CryptographicOperations.ZeroMemory(hash); }
    }
    private void Save(Settings value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(stream, value); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
