using System.Collections.ObjectModel;
using NarutoAutoGUI.Models;

namespace NarutoAutoGUI.Views;

// The newest-first activity list. Consecutive entries from one Plan Item share a group heading at their newest end,
// so a new entry joins the group under the top heading or starts a new group above it.
internal sealed class RunLogFeed(ObservableCollection<object> items, int capacity)
{
    internal const string EnvironmentLabel = "运行环境";

    internal int EntryCount { get; private set; }

    // Returns how many rows were inserted at the top, so a reader scrolled away can keep their place.
    internal int Add(LogEntry entry)
    {
        var inserted = 1;
        if (items.Count != 0 && items[0] is RunLogGroup { PlanItemId: var top } heading && top == entry.PlanItemId) {
            items.Insert(1, entry);
            // A heading made before the Run Plan label was known takes it from the first entry that has it.
            if (entry.TaskLabel is { } label && label != heading.Label) {
                items[0] = heading with { Label = label };
            }
        } else {
            items.Insert(0, entry);
            items.Insert(0, new RunLogGroup(entry.PlanItemId, entry.TaskLabel ?? EnvironmentLabel));
            inserted = 2;
        }
        EntryCount++;
        while (EntryCount > capacity) {
            items.RemoveAt(items.Count - 1);
            EntryCount--;
            if (items[^1] is RunLogGroup) {
                items.RemoveAt(items.Count - 1);
            }
        }
        return inserted;
    }

    internal void Clear()
    {
        items.Clear();
        EntryCount = 0;
    }
}
