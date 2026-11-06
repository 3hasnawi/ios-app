using System.Text.Json.Nodes;
using SalaryLocal.Core;

internal static class LocalSessionLockTests
{
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(int seconds) => ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }
    public static void Run()
    {
        int checks = 0;
        void Check(bool ok, string label) { checks++; if (!ok) throw new Exception("Session lock: " + label); }
        void Reject(Action action, string label) { try { action(); } catch (Exception e) when (e is InvalidDataException or InvalidOperationException) { checks++; return; } throw new Exception("Session lock expected refusal: " + label); }
        var root = Path.Combine(Path.GetTempPath(), "synthetic-session-lock-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "session-lock.json"); var clock = new Clock();
        const string password = "Synthetic-password-123";
        try
        {
            var state = new LocalSessionLock(path, clock);
            Check(!state.Enabled && !state.IsLocked, "disabled first use");
            Reject(() => state.Enable("short", 5), "short password");
            Reject(() => state.Enable(password, 0), "invalid timeout");
            state.Enable(password, 1);
            var saved = File.ReadAllText(path);
            Check(!saved.Contains(password) && saved.Contains("600000"), "salted verifier only");
            Reject(() => state.Enable(password, 1), "cannot overwrite enabled lock");
            Check(!state.Disable("wrong") && File.ReadAllText(path) == saved, "disable requires password");
            Check(!state.ChangePassword("wrong", "New-synthetic-password", 2) && File.ReadAllText(path) == saved, "change requires password");
            clock.Advance(59); Check(!state.CheckIdle(), "before timeout");
            state.RecordActivity(); clock.Advance(59); Check(!state.CheckIdle(), "activity resets timeout");
            clock.Advance(1); Check(state.CheckIdle(), "idle locks exactly at timeout");
            Reject(() => state.Disable(password), "locked session cannot disable");
            Check(state.TryUnlock(password), "correct unlock");
            state.Lock(); Check(state.IsLocked, "manual lock");
            for (int i = 0; i < 5; i++) Check(!state.TryUnlock("wrong"), "incorrect password");
            Check(!state.TryUnlock(password), "cooldown blocks even correct password");
            clock.Advance(30); Check(state.TryUnlock(password), "cooldown expires");
            Check(state.ChangePassword(password, "New-synthetic-password", 2), "change works");
            Check(File.ReadAllText(path) != saved && state.IdleMinutes == 2, "new salt and timeout");
            var restarted = new LocalSessionLock(path, clock);
            Check(restarted.IsLocked && restarted.Enabled, "restart starts locked");
            Check(!restarted.TryUnlock(password) && restarted.TryUnlock("New-synthetic-password"), "old password invalidated");
            Check(restarted.Disable("New-synthetic-password") && !File.Exists(path), "verified disable persists");
            Check(!new LocalSessionLock(path).Enabled, "disabled after restart");
            File.WriteAllText(path, "{broken"); Reject(() => _ = new LocalSessionLock(path), "corrupt fails closed");
            File.WriteAllText(path, saved);
            var json = JsonNode.Parse(saved)!; json["Iterations"] = 1; File.WriteAllText(path, json.ToJsonString()); Reject(() => _ = new LocalSessionLock(path), "downgraded iterations rejected");
            File.WriteAllText(path, "null"); Reject(() => _ = new LocalSessionLock(path), "null settings rejected");
            Check(LocalSessionLock.Warning.Contains("ليس تشفيراً"), "scope warning");
            Console.WriteLine($"Local session lock: {checks} assertions passed.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
