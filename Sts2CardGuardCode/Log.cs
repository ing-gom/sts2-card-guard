using System.Threading;

namespace Sts2CardGuard;

/// <summary>
/// Thin, throttled wrapper over the game logger. Filter hits fire often, so detailed
/// per-hit lines are capped; a periodic summary keeps the log useful without spam.
/// </summary>
internal static class Log
{
    private const int DetailCap = 40;
    private static int _detailCount;
    private static long _totalRemoved;
    private static long _totalHits;

    public static void Info(string msg) => MainFile.Logger.Info($"[{MainFile.ModId}] {msg}");
    public static void Warn(string msg) => MainFile.Logger.Warn($"[{MainFile.ModId}] {msg}");

    public static void FilterHit(string characterTitle, int removed, int kept)
    {
        Interlocked.Add(ref _totalRemoved, removed);
        long hits = Interlocked.Increment(ref _totalHits);

        if (Interlocked.Increment(ref _detailCount) <= DetailCap)
        {
            MainFile.Logger.Info(
                $"[{MainFile.ModId}] filtered {removed} disallowed card(s) for '{characterTitle}' ({kept} remain).");
            if (_detailCount == DetailCap)
                MainFile.Logger.Info($"[{MainFile.ModId}] (further per-hit filter logs suppressed; running total will still accrue)");
        }
        else if (hits % 100 == 0)
        {
            MainFile.Logger.Info(
                $"[{MainFile.ModId}] filter summary: {hits} hits, {Interlocked.Read(ref _totalRemoved)} cards removed so far.");
        }
    }
}
