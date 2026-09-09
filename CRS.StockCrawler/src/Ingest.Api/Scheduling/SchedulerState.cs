namespace Ingest.Api.Scheduling;

/// <summary>
/// Steuerung und Zustand des Schedulers, zur Laufzeit umschaltbar.
///
/// Bewusst nicht nur ein Konfigurationswert: zum Abschalten müsste man sonst
/// die Anwendung neu starten — und genau das will man nicht, wenn gerade ein
/// langer Backfill läuft oder man die Kennzahlen in Ruhe ansehen möchte.
///
/// Als Singleton registriert und von mehreren Threads gelesen, deshalb
/// durchgehend mit <c>lock</c> beziehungsweise <c>volatile</c> abgesichert.
/// </summary>
public sealed class SchedulerState
{
    private readonly object _gate = new();
    private volatile bool _enabled = true;

    /// <summary>Ob der Scheduler seine Läufe ausführt. Der Takt läuft weiter.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>Läuft gerade ein Job? Verhindert Überschneidungen.</summary>
    public bool Busy { get; private set; }

    /// <summary>Was gerade läuft, und seit wann.</summary>
    /// <remarks>
    /// <c>LastJob</c> allein beantwortet die Frage nicht: Es steht auch dann noch da, wenn
    /// der Lauf längst beendet ist. Für eine Statusleiste braucht es die Unterscheidung
    /// „läuft gerade" von „lief zuletzt" — sonst zeigt sie stundenlang einen Lauf an, den
    /// es nicht mehr gibt.
    /// </remarks>
    public string? LaufendJob { get; private set; }

    public DateTime? LaufendSeitUtc { get; private set; }

    public DateTime? NextHourlyUtc { get; set; }
    public DateTime? NextDailyUtc { get; set; }

    public DateTime? LastHourlyUtc { get; private set; }
    public DateTime? LastDailyUtc { get; private set; }

    public string? LastJob { get; private set; }
    public string? LastResult { get; private set; }
    public int SkippedWhileDisabled { get; private set; }

    public IDisposable BeginRun(string job)
    {
        lock (_gate)
        {
            Busy = true;
            LastJob = job;
            LaufendJob = job;
            LaufendSeitUtc = DateTime.UtcNow;
        }
        return new RunScope(this, job);
    }

    private void EndRun(string job, string result)
    {
        lock (_gate)
        {
            Busy = false;
            LastResult = result;
            LaufendJob = null;
            LaufendSeitUtc = null;

            if (job.Contains("Stunde", StringComparison.OrdinalIgnoreCase))
                LastHourlyUtc = DateTime.UtcNow;
            else
                LastDailyUtc = DateTime.UtcNow;
        }
    }

    public void NoteSkipped()
    {
        lock (_gate) SkippedWhileDisabled++;
    }

    private sealed class RunScope(SchedulerState state, string job) : IDisposable
    {
        public string Result { get; set; } = "ok";
        public void Dispose() => state.EndRun(job, Result);
    }

    /// <summary>Ergebnis des laufenden Jobs festhalten.</summary>
    public void SetResult(string result)
    {
        lock (_gate) LastResult = result;
    }
}
