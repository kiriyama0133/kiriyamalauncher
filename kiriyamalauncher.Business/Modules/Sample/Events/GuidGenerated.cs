using System;

namespace kiriyamalauncher.Business.Modules.Sample.Events;

public class GuidGenerated : EventArgs
{
    public required string UUID { get; init; }
}
