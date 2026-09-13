using System;

namespace kiriyamalauncher.Data.Entities;

public record FlatColor
{
    public required string Name { get; init; }
    public required string Hex { get; init; }
}
