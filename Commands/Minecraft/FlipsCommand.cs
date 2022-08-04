using System;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.Api.Client.Api;
using Coflnet.Sky.Core;
using Coflnet.Sky.ModCommands.Dialogs;

namespace Coflnet.Sky.Commands.MC;

public class FlipsCommand : McCommand
{
    public override async Task Execute(MinecraftSocket socket, string arguments)
    {

        var accounts = await socket.sessionLifesycle.GetMinecraftAccountUuids();

        var response = await socket.GetService<IFlipApi>().ApiFlipStatsPlayerPlayerUuidGetAsync(socket.SessionInfo.McUuid, 7);
        int.TryParse(arguments.Trim('"'), out int page);
        if (page < 0)
            page = response.Flips.Count / 10 + page;
        if (page == 0)
            page = 1;
        var pageSize = 10;
        var toDisplay = response.Flips.Skip((page - 1) * pageSize).Take(pageSize);
        var totalPages = response.Flips.Count / pageSize + 1;
        var dialog = DialogBuilder.New.MsgLine($"Flips (page {page}/{totalPages})")
            .ForEach(toDisplay, (db, f) =>
                db.MsgLine($"{socket.formatProvider.GetRarityColor(Enum.Parse<Tier>(f.Tier, true))}{f.ItemName} {(f.Profit > 0 ? McColorCodes.GREEN : McColorCodes.RED)}Profit: {socket.formatProvider.FormatPrice(f.Profit)}",
                        $"https://sky.coflnet.com/auction/{f.OriginAuction}", $"Sold at: {f.SellTime:g}\nFound first by: {f.Finder}"));
        socket.SendMessage(dialog.Build());
    }
}