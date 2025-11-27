using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using ChatPlus.DiscordWebhookModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Sharp.Modules.LocalizerManager.Shared;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Managers;
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
    private readonly Dictionary<IGameClient, HashSet<IGameClient>> blockedPlayers = [];
    private readonly ISharpModuleManager _modules;
    private IModSharpModuleInterface<ILocalizerManager>? _cachedLocalizerInterface;
    private readonly string? _webhookUrl;
    private readonly HttpClient _httpClient;

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

        _modules = _sharedSystem.GetSharpModuleManager();

        _webhookUrl = configuration.GetValue<string>("webhookUrl");
        if (string.IsNullOrEmpty(_webhookUrl))
        {
            _logger.LogWarning(
                "No webhook URL found in Configuration.json. Private messages will not be sent to webhook."
            );
        }
        _httpClient = new();
    }

    public bool Init()
    {
        _sharedSystem.GetClientManager().InstallCommandCallback("pm", OnPmCommand);
        _sharedSystem.GetClientManager().InstallCommandCallback("pmblock", OnPmBlockCommand);

        _logger.LogInformation("ChatPlus Init");
        return true;
    }

    public void OnAllModulesLoaded()
    {
        GetLocalizerInterface()?.LoadLocaleFile("chatplus-locale");
    }

    public void Shutdown()
    {
        _sharedSystem.GetClientManager().RemoveCommandCallback("pm", OnPmCommand);
        _sharedSystem.GetClientManager().RemoveCommandCallback("pmblock", OnPmBlockCommand);
        _httpClient.Dispose();
        _logger.LogInformation("ChatPlus Shutdown");
    }

    public async Task SendLogToWebhook(IGameClient? sender, IGameClient recipient, string message)
    {
        if (string.IsNullOrEmpty(_webhookUrl))
            return;

        try
        {
            int embedColor = 0x00ff11ff; // #00ff11ff

            var embed = new Embed
            {
                Title = "Private Message Log",
                Description =
                    $"**Sender:** {sender?.Name ?? "Unknown"} ({sender?.SteamId.ToString() ?? "N/A"})\n"
                    + $"**Recipient:** {recipient.Name} ({recipient.SteamId})\n"
                    + $"**Message:** ```\n{message}\n```",
                Color = embedColor,
                Timestamp = DateTime.UtcNow,
            };

            var webhookContent = new DiscordWebhookPayload();
            webhookContent.Embeds.Add(embed);

            var serializerSettings = new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
            };

            var jsonPayload = JsonConvert.SerializeObject(webhookContent, serializerSettings);
            var requestContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            HttpResponseMessage response = await _httpClient.PostAsync(_webhookUrl, requestContent);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception e)
        {
            _logger.LogError("Failed to send message through webhook. Message: {exception}", e);
        }
    }

    private ILocalizerManager? GetLocalizerInterface()
    {
        if (_cachedLocalizerInterface?.Instance is null)
        {
            _cachedLocalizerInterface = _modules.GetOptionalSharpModuleInterface<ILocalizerManager>(
                ILocalizerManager.Identity
            );
        }

        return _cachedLocalizerInterface?.Instance;
    }

    public ECommandAction OnPmCommand(IGameClient caller, StringCommand command)
    {
        var localizer = GetLocalizerInterface()?.GetLocalizer(caller);
        if (command.ArgCount < 2)
        {
            caller
                .GetPlayerController()
                ?.Print(
                    HudPrintChannel.Chat,
                    localizer is { }
                        ? localizer.Format("ChatPlus.PmCommand.Usage")
                        : "[PM] Usage: ms_pm <player_name|#steam_id|#userid> <message>"
                );
            return ECommandAction.Stopped;
        }

        string targetString = command.GetArg(1);
        IGameClient? target = _targeting?.FindTarget(targetString);
        if (target is not { IsAuthenticated: true })
        {
            caller
                .GetPlayerController()
                ?.Print(
                    HudPrintChannel.Chat,
                    localizer is { }
                        ? localizer.Format("ChatPlus.TargetNotFound", targetString)
                        : $"[PM] Couldn't find target called {targetString}"
                );
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
                    localizer is { }
                        ? localizer.Format("ChatPlus.PmCommand.UserBlockedYou", target.Name)
                        : $"[PM] The user {target.Name} has blocked you from sending them private messages."
                );
            return ECommandAction.Stopped;
        }

        int targetArgStartIndex = command.ArgString.IndexOf(targetString);
        string messageString = command.ArgString.Remove(targetArgStartIndex, targetString.Length);

        string sentMessage = localizer is { }
            ? localizer.Format("ChatPlus.MessageInbound", caller.Name, messageString)
            : $"(from ->) {caller.Name}: {messageString}";
        target.GetPlayerController()?.Print(HudPrintChannel.Chat, sentMessage);

        string feedbackMessage = localizer is { }
            ? localizer.Format("ChatPlus.MessageOutbound", target.Name, messageString)
            : $"(to ->) {target.Name}: {messageString}";
        caller.GetPlayerController()?.Print(HudPrintChannel.Chat, feedbackMessage);

        _ = SendLogToWebhook(caller, target, messageString);

        return ECommandAction.Handled;
    }

    public ECommandAction OnPmBlockCommand(IGameClient caller, StringCommand command)
    {
        var localizer = GetLocalizerInterface()?.GetLocalizer(caller);

        if (command.ArgCount == 0)
        {
            caller
                .GetPlayerController()
                ?.Print(
                    HudPrintChannel.Chat,
                    localizer is { }
                        ? localizer.Format("ChatPlus.PmBlockCommand.Usage")
                        : "[PM] Usage: ms_pmblock <player_name|#steam_id|#userid>"
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
                ?.Print(
                    HudPrintChannel.Chat,
                    localizer is { }
                        ? localizer.Format("ChatPlus.TargetNotFound", targetString)
                        : $"[PM] Couldn't find target {targetString}"
                );
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
                        localizer is { }
                            ? localizer.Format(
                                "ChatPlus.PmBlockCommand.BlockedSuccessfully",
                                targetString
                            )
                            : $"[PM] Private messages from {targetPlayer.Name} blocked."
                    );
                return ECommandAction.Handled;
            }

            caller
                .GetPlayerController()
                ?.Print(
                    HudPrintChannel.Chat,
                    localizer is { }
                        ? localizer.Format(
                            "ChatPlus.PmBlockCommand.UnblockedSuccessfully",
                            targetString
                        )
                        : $"[PM] Private messages from {targetPlayer.Name} unblocked."
                );

            return ECommandAction.Handled;
        }

        blockedPlayers[caller] = [targetPlayer];
        caller
            .GetPlayerController()
            ?.Print(
                HudPrintChannel.Chat,
                localizer is { }
                    ? localizer.Format("ChatPlus.PmBlockCommand.BlockedSuccessfully", targetString)
                    : $"[PM] Private messages from {targetPlayer.Name} blocked."
            );

        return ECommandAction.Handled;
    }
}
