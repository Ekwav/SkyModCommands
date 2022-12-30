namespace Coflnet.Sky.ModCommands.Services;

using Microsoft.Extensions.Hosting;
using System;
using System.Threading;
using System.Threading.Tasks;
using StackExchange.Redis;
using Coflnet.Sky.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Coflnet.Sky.Commands.Shared;
using System.Collections.Concurrent;
using Coflnet.Sky.Commands;

/// <summary>
/// Handles events before the api update
/// </summary>
public class PreApiService : BackgroundService
{
    ConnectionMultiplexer redis;
    IConfiguration config;
    ILogger<PreApiService> logger;
    static private ConcurrentDictionary<IFlipConnection, DateTime> users = new();
    public PreApiService(ConnectionMultiplexer redis, IConfiguration config, FlipperService flipperService, ILogger<PreApiService> logger)
    {
        this.redis = redis;
        this.config = config;
        this.logger = logger;

        flipperService.PreApiLowPriceHandler += PreApiLowPriceHandler;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // here to trigger the creation of the service
        await Task.Delay(1000, stoppingToken);
    }
    public void AddUser(IFlipConnection connection, DateTime expires)
    {
        users.AddOrUpdate(connection, expires, (key, old) => expires);
        logger.LogInformation($"Added user {connection.UserId} to flip list {users.Count} users {expires}");
    }

    private async Task PreApiLowPriceHandler(FlipperService sender, LowPricedAuction e)
    {
        e.Auction.ItemName += Commands.MC.McColorCodes.DARK_GRAY + ".";
        foreach (var item in users.Keys)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (Random.Shared.Next(0, 2) == 0)
                        await Task.Delay(Random.Shared.Next(4000, 8000)).ConfigureAwait(false);
                    logger.LogInformation($"Sent flip to {item.UserId} for {e.Auction.Uuid} ");
                    var sendSuccessful = await item.SendFlip(e).ConfigureAwait(false);
                    if (!sendSuccessful)
                    {
                        logger.LogInformation($"Failed to send flip to {item.UserId} for {e.Auction.Uuid}");
                        users.TryRemove(item, out _);
                    }
                    if (users[item] < DateTime.Now)
                    {
                        users.TryRemove(item, out _);
                        logger.LogInformation("Removed user from flip list");
                    }
                }
                catch (System.Exception e)
                {
                    logger.LogError(e, "Error while sending flip to user");
                }
            }).ConfigureAwait(false);
        }
        var profit = e.TargetPrice - e.Auction.StartingBid;
        if (profit > 0)
            logger.LogInformation($"Pre-api low price handler called for {e.Auction.Uuid} profit {profit} users {users.Count}");
        await Task.Delay(20_000).ConfigureAwait(false);
        // check if flip was sent to anyone 
        await Task.Delay(15_000).ConfigureAwait(false);
        // if not send to all users
    }
}