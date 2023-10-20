using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.Commands.Shared;
using Coflnet.Sky.Core;
using Coflnet.Sky.Filter;
using Microsoft.EntityFrameworkCore;

namespace Coflnet.Sky.Commands.MC;
public class LbinCommand : McCommand
{
    FilterParser parser = new FilterParser();
    public override async Task Execute(MinecraftSocket socket, string arguments)
    {
        var filters = new Dictionary<string, string>();
        var itemName = await parser.ParseFiltersAsync(socket, arguments, filters, FlipFilter.AllFilters);
        var itemId = await socket.GetService<Items.Client.Api.IItemsApi>().ItemsSearchTermIdGetAsync(itemName);
        var fe = socket.GetService<FilterEngine>();
        using var context = new HypixelContext();
        var auctions = await fe.AddFilters(context.Auctions
                    .Where(a => a.ItemId == itemId && a.End > DateTime.Now && a.HighestBidAmount == 0 && a.Bin), filters)
                    .OrderBy(a => a.StartingBid).Take(5).ToListAsync();
        socket.Dialog(db =>
            db.ForEach(auctions.OrderByDescending(a => a.StartingBid), (d, a) => d.MsgLine($"§e{a.StartingBid}§7: {a.End}")).Break
            .MsgLine($"{McColorCodes.GREEN}Trying to open the lowest bin ..."));
        var lowest = auctions.OrderBy(a => a.StartingBid).FirstOrDefault();
        if (lowest == null)
        {
            socket.SendMessage("Sorry there was no auction found");
            return;
        }
        socket.ExecuteCommand($"/viewauction {lowest.Uuid}");
    }
}