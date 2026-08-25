namespace NarutoAutoGUI.Worker;

internal enum WorkerLogSequenceDisposition
{
    Duplicate,
    Contiguous,
    Gap
}

internal sealed class WorkerLogSequenceTracker
{
    internal Guid? WorkerInstanceId { get; private set; }

    internal long LastContiguousSequence { get; private set; }

    internal long HighestObservedSequence { get; private set; }

    internal bool BeginWorkerInstance(Guid workerInstanceId)
    {
        if (WorkerInstanceId == workerInstanceId)
        {
            return false;
        }

        WorkerInstanceId = workerInstanceId;
        LastContiguousSequence = 0;
        HighestObservedSequence = 0;
        return true;
    }

    internal void ObserveTarget(long sequence)
    {
        if (sequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }
        HighestObservedSequence = Math.Max(HighestObservedSequence, sequence);
    }

    internal WorkerLogSequenceDisposition Observe(long sequence)
    {
        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }
        if (sequence <= LastContiguousSequence)
        {
            return WorkerLogSequenceDisposition.Duplicate;
        }

        HighestObservedSequence = Math.Max(HighestObservedSequence, sequence);
        if (sequence != LastContiguousSequence + 1)
        {
            return WorkerLogSequenceDisposition.Gap;
        }

        LastContiguousSequence = sequence;
        return WorkerLogSequenceDisposition.Contiguous;
    }

    internal void SkipToFirstAvailable(long firstAvailableSequence)
    {
        if (firstAvailableSequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(firstAvailableSequence));
        }

        LastContiguousSequence = Math.Max(LastContiguousSequence, firstAvailableSequence - 1);
        HighestObservedSequence = Math.Max(HighestObservedSequence, LastContiguousSequence);
    }
}
