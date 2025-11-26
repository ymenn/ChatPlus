using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;

namespace ChatPlus;

public class ChatPlus : IModSharpModule
{
    public string DisplayName => "ChatPlus";
    public string DisplayAuthor => "menn";
    private readonly ILogger<ChatPlus> _logger;
    private readonly ServiceProvider _serviceProvider;
    private readonly ISharedSystem _sharedSystem;
    private readonly Targeting? _targeting;

    private Dictionary<IGameClient, HashSet<IGameClient>> blockedPlayers = [];

    public ChatPlus(
        ISharedSystem sharedSystem,
        string dllPath,
        string sharpPath,
        Version version,
        IConfiguration coreConfiguration,
        bool hotReload
    )
    {
        ArgumentNullException.ThrowIfNull(dllPath);
        ArgumentNullException.ThrowIfNull(sharpPath);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(coreConfiguration);

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(dllPath, "Configuration.json"), false, false)
            .Build();

        var services = new ServiceCollection();

        _sharedSystem = sharedSystem;

        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(sharedSystem.GetLoggerFactory());
        services.AddSingleton(sharedSystem);
        services.TryAdd(ServiceDescriptor.Singleton(typeof(ILogger<>), typeof(Logger<>)));
        services.AddScoped<Targeting>();

        _logger = sharedSystem.GetLoggerFactory().CreateLogger<ChatPlus>();
        _serviceProvider = services.BuildServiceProvider();
        _targeting = _serviceProvider.GetService<Targeting>();
    }

    public bool Init()
    {
        _sharedSystem.GetClientManager().InstallCommandCallback("pm", OnPmCommand);
        _sharedSystem.GetClientManager().InstallCommandCallback("pmblock", OnPmBlockCommand);
        _sharedSystem.GetClientManager().InstallCommandCallback("test", OnTestCommand);
        // _sharedSystem.GetConVarManager().CreateServerCommand("ms_pm", OnPmConsoleCommand);

        _logger.LogInformation("ChatPlus Init");
        return true;
    }

    public void Shutdown()
    {
        _sharedSystem.GetClientManager().RemoveCommandCallback("pm", OnPmCommand);
        _sharedSystem.GetClientManager().RemoveCommandCallback("pmblock", OnPmBlockCommand);
        _sharedSystem.GetClientManager().RemoveCommandCallback("test", OnTestCommand);
        // _sharedSystem.GetConVarManager().ReleaseCommand("ms_pm");

        _logger.LogInformation("ChatPlus Shutdown");
    }

    // public ECommandAction OnPmConsoleCommand(StringCommand command)
    // {
    //     if (command.ArgCount == 0)
    //         return ECommandAction.Stopped;

    //     string messageString = command.GetCommandString();
    //     IGameClient? target = _targeting?.FindTarget(messageString);

    //     if (target is null)
    //     {
    //         _logger.LogInformation($"[PM] Couldn't find target called {messageString}");
    //         return ECommandAction.Stopped;
    //     }

    //     if (target.GetPlayerController() is { } targetController)
    //     {
    //         string feedbackMessage = $"[PM To] {target.Name}: {messageString}";
    //         _logger.LogInformation(feedbackMessage);
    //         string sentMessage = $"[PM From] *CONSOLE*: {messageString}";
    //         targetController.Print(HudPrintChannel.Chat, sentMessage);
    //     }
    //     else
    //     {
    //         _logger.LogInformation("[PM] Failed to get player controller, message not delivered.");
    //     }
    //     return ECommandAction.Handled;
    // }

    public ECommandAction OnTestCommand(IGameClient caller, StringCommand command)
    {
        caller.GetPlayerController()?.Print(HudPrintChannel.Chat, command.ArgString);
        caller
            .GetPlayerController()
            ?.Print(HudPrintChannel.Chat, $"Called cmd with arg count: {command.ArgCount}...");

        return ECommandAction.Handled;
    }

    public ECommandAction OnPmCommand(IGameClient caller, StringCommand command)
    {
        if (command.ArgCount < 2)
        {
            caller
                .GetPlayerController()
                ?.Print(
                    HudPrintChannel.Chat,
                    "[PM] Usage: ms_pm <player_name|#steam_id|#userid> <message>"
                );
            return ECommandAction.Stopped;
        }

        string targetString = command.GetArg(1);
        IGameClient? target = _targeting?.FindTarget(targetString);
        if (target is not { IsAuthenticated: true })
        {
            caller
                .GetPlayerController()
                ?.Print(HudPrintChannel.Chat, $"[PM] Couldn't find target called {targetString}");
            return ECommandAction.Stopped;
        }
        if (
            blockedPlayers.TryGetValue(target, out HashSet<IGameClient>? blockedSet)
            && blockedSet?.Contains(caller) == true
        )
        {
            caller
                .GetPlayerController()
                ?.Print(
                    HudPrintChannel.Chat,
                    $"[PM] The user {target.Name} has blocked you from sending them PMs."
                );
            return ECommandAction.Stopped;
        }

        int targetArgStartIndex = command.ArgString.IndexOf(targetString);
        string messageString = command.ArgString.Remove(targetArgStartIndex, targetString.Length);

        string sentMessage = $"(from ->) {caller.Name}: {messageString}";
        target.GetPlayerController()?.Print(HudPrintChannel.Chat, sentMessage);

        string feedbackMessage = $"(to ->) {target.Name}: {messageString}";
        caller.GetPlayerController()?.Print(HudPrintChannel.Chat, feedbackMessage);

        return ECommandAction.Handled;
    }

    public ECommandAction OnPmBlockCommand(IGameClient caller, StringCommand command)
    {
        if (command.ArgCount == 0)
        {
            caller
                .GetPlayerController()
                ?.Print(
                    HudPrintChannel.Chat,
                    "[PM] Usage: ms_pmblock <player_name|#steam_id|#userid>"
                );
            return ECommandAction.Stopped;
        }

        string targetString = command.GetArg(1);
        IGameClient? targetPlayer = _serviceProvider
            .GetService<Targeting>()
            ?.FindTarget(targetString);

        if (targetPlayer is not { IsAuthenticated: true })
        {
            caller
                .GetPlayerController()
                ?.Print(HudPrintChannel.Chat, $"[PM] Couldn't find target {targetString}");
            return ECommandAction.Stopped;
        }

        if (blockedPlayers.TryGetValue(caller, out HashSet<IGameClient>? value))
        {
            if (!value.Remove(targetPlayer))
            {
                value.Add(targetPlayer);
                caller
                    .GetPlayerController()
                    ?.Print(
                        HudPrintChannel.Chat,
                        $"[PM] Private messages from {targetPlayer.Name} blocked."
                    );
                return ECommandAction.Handled;
            }

            caller
                .GetPlayerController()
                ?.Print(
                    HudPrintChannel.Chat,
                    $"[PM] Private messages from {targetPlayer.Name} unblocked."
                );

            targetPlayer
                .GetPlayerController()
                ?.Print(
                    HudPrintChannel.Chat,
                    $"[PM] {caller.Name} has unblocked you from receiving PMs."
                );
            return ECommandAction.Handled;
        }

        blockedPlayers[caller] = [targetPlayer];
        caller
            .GetPlayerController()
            ?.Print(
                HudPrintChannel.Chat,
                $"[PM] Private messages from {targetPlayer.Name} blocked."
            );

        return ECommandAction.Handled;
    }
}
