using SalaryLocal.Core;
internal static class LocalPdfPrintLayoutTests
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
        void Reject(Action action) { try { action(); } catch (InvalidDataException) { checks++; return; } throw new Exception("Unsafe print layout accepted"); }
        var portrait = new LocalPdfPageSize(794, 1123); var landscape = new LocalPdfPageSize(1123, 794);
        var exact = LocalPdfPrintLayout.Fit(portrait, portrait, 0, 0, 794, 1123);
        Check(exact.Scale == 1 && exact.X == 0 && exact.Y == 0, "Borderless exact page");
        var fit = LocalPdfPrintLayout.Fit(portrait, portrait, 20, 30, 750, 1060);
        Check(fit.Scale < 1 && fit.X >= 20 && fit.Y >= 30 && fit.X + fit.Width <= 770.001 && fit.Y + fit.Height <= 1090.001, "Safe printable margins");
        Check(Math.Abs(fit.Width / fit.Height - portrait.Width / portrait.Height) < .00001, "Aspect preserved");
        var mixed = LocalPdfPrintLayout.Fit(landscape, portrait, 20, 30, 750, 1060);
        Check(mixed.Width <= 750 && mixed.Height <= 1060 && mixed.Scale < fit.Scale, "Mixed orientation fits without cropping");
        var smaller = LocalPdfPrintLayout.Fit(new(200, 300), portrait, 20, 30, 750, 1060);
        Check(smaller.Scale == 1 && smaller.X == 295 && smaller.Y == 410, "Never silently enlarge");
        Reject(() => LocalPdfPrintLayout.Fit(portrait, portrait, -1, 0, 750, 1060));
        Reject(() => LocalPdfPrintLayout.Fit(portrait, portrait, 20, 0, 794, 1123));
        Reject(() => LocalPdfPrintLayout.Fit(new(double.NaN, 100), portrait, 0, 0, 750, 1060));
        Reject(() => LocalPdfPrintLayout.Fit(portrait, new(0, 0), 0, 0, 750, 1060));
        Reject(() => LocalPdfPrintLayout.Fit(portrait, portrait, 0, 0, double.PositiveInfinity, 1060));
        LocalPdfPrintLayout.ValidateRaster(4961, 7016); checks++;
        Reject(() => LocalPdfPrintLayout.ValidateRaster(10000, 10000));
        Reject(() => LocalPdfPrintLayout.ValidateRaster(int.MaxValue, int.MaxValue));
        Reject(() => LocalPdfPrintLayout.ValidateRaster(0, 100));
        Console.WriteLine($"PDF print layout: {checks} checks passed.");
    }
}
