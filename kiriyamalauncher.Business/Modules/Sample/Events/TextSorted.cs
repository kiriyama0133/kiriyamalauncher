using System;

namespace kiriyamalauncher.Business.Modules.Sample.Events;

public class TextSorted : EventArgs
{
    public required string? SortedText { get; init; }
}
