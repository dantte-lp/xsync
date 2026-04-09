namespace Xsync.Logging;

public enum FilePhase
{
    Pending,
    Hashing,
    Transferring,
    Verifying,
    Done,
    Match,
    DryRun,
    Failed,
}
