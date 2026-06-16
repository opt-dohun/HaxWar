namespace HexWar.Application.Services;

using System;

public static class ServerIdentity
{
    public static string Id { get; } = $"{Environment.MachineName}_{Guid.NewGuid():N}";
}
