using System;
using System.Linq;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Objects;

namespace ChatPlus;

public class Targeting(ILogger<Targeting> logger, ISharedSystem sharedSystem)
{
    private readonly ILogger<Targeting> _logger = logger;
    private readonly ISharedSystem _sharedSystem = sharedSystem;

    // Since this targeting is only for pm's its not very critical so this is quite simple for now
    public IGameClient? FindTarget(string query)
    {
        var normalizedQuery = query.Trim();

        if (string.IsNullOrEmpty(normalizedQuery))
            return null;

        IGameClient? match;
        if (
            normalizedQuery.StartsWith('#') && ulong.TryParse(normalizedQuery.AsSpan(1), out var id)
        )
        {
            match = _sharedSystem
                .GetModSharp()
                .GetIServer()
                .GetGameClients(true, true)
                .FirstOrDefault(player =>
                    player.UserId.AsPrimitive() == id || player.SteamId == id
                );

            if (match is { IsAuthenticated: true })
                return match;
        }

        match = _sharedSystem
            .GetModSharp()
            .GetIServer()
            .GetGameClients(true, true)
            .FirstOrDefault(player =>
                player.Name.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
            );

        return match;
    }
}
