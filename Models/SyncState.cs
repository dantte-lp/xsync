namespace Xsync.Models;

public enum SyncState
{
    Pending = 0,
    Hashing = 1,
    Transferring = 2,
    Verifying = 3,
    Done = 4,
    Failed = 5,
    Skipped = 6,
}
