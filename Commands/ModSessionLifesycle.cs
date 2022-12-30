using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Web;
using Coflnet.Sky.Commands.Shared;
using Coflnet.Sky.ModCommands.Dialogs;
using Coflnet.Sky.Core;
using Newtonsoft.Json;
using WebSocketSharp;
using OpenTracing;
using Coflnet.Sky.ModCommands.Services;
using System.Threading;
using System.Text;
using System.Diagnostics;
using Coflnet.Sky.ModCommands.Tutorials;

namespace Coflnet.Sky.Commands.MC
{
    /// <summary>
    /// Represents a mod session
    /// </summary>
    public class ModSessionLifesycle : IDisposable
    {
        protected MinecraftSocket socket;
        protected ActivitySource tracer => socket.tracer;
        public SessionInfo SessionInfo => socket.SessionInfo;
        public string COFLNET = MinecraftSocket.COFLNET;
        public SelfUpdatingValue<FlipSettings> FlipSettings;
        public SelfUpdatingValue<string> UserId;
        public SelfUpdatingValue<AccountInfo> AccountInfo;
        public SelfUpdatingValue<AccountSettings> AccountSettings;
        public SelfUpdatingValue<PrivacySettings> PrivacySettings;
        public Activity ConSpan => socket.ConSpan;
        public System.Threading.Timer PingTimer;
        private SpamController spamController = new SpamController();
        private DelayHandler delayHandler;
        public VerificationHandler VerificationHandler;
        public FlipProcesser flipProcesser;
        public TimeSpan CurrentDelay => delayHandler?.CurrentDelay ?? DelayHandler.DefaultDelay;

        public static FlipSettings DEFAULT_SETTINGS => new FlipSettings()
        {
            MinProfit = 100000,
            MinVolume = 20,
            ModSettings = new ModSettings() { ShortNumbers = true },
            Visibility = new VisibilitySettings() { SellerOpenButton = true, ExtraInfoMax = 3, Lore = true }
        };

        public ModSessionLifesycle(MinecraftSocket socket)
        {
            this.socket = socket;
            delayHandler = new DelayHandler(TimeProvider.Instance, socket.GetService<FlipTrackingService>(), this.SessionInfo);

            flipProcesser = new FlipProcesser(socket, spamController, delayHandler);
            VerificationHandler = new VerificationHandler(socket);
        }



        public async Task SetupConnectionSettings(string stringId)
        {
            /*    socket.Dialog(db => db.Lines("The welcome pig greets you",
    "           __,---.__",
    "        ,-'          `-.__",
    @"      &/           `._\ _\",
    "       /               ' '._",
    @"      |   ,               ("")",
    @"      |__,'`-..--|__|--''"));

                socket.Dialog(db => db.ForEach("ayz🤨🤔🇧🇾:|,:-.#ä+!^°~´` ", (db, c) => db.ForEach("01234567890123456789", (idb, ignore) => idb.Msg(c.ToString())).MsgLine("|")));
                socket.Dialog(db => db.LineBreak().ForEach(":;", (db, c) => db.ForEach("012345678901234567890123456789", (idb, ignore) => idb.Msg(c.ToString())).MsgLine("|")));
                socket.Dialog(db => db.LineBreak().ForEach("´", (db, c) => db.ForEach("012345678901234567890123", (idb, ignore) => idb.Msg(c.ToString())).MsgLine("|")));
    */
            using var loadSpan = socket.CreateActivity("loadSession", ConSpan);
            SessionInfo.SessionId = stringId;

            PingTimer = new System.Threading.Timer((e) =>
            {
                SendPing();
            }, null, TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(50));

            UserId = await SelfUpdatingValue<string>.Create("mod", stringId);
            _ = socket.TryAsyncTimes(() => SendLoginPromptMessage(stringId), "login prompt");
            if (MinecraftSocket.IsDevMode)
                await UserId.Update("1");
            if (UserId.Value == default)
            {
                using var waitLogin = socket.CreateActivity("waitLogin", ConSpan);
                UserId.OnChange += (newset) => Task.Run(async () => await SubToSettings(newset));
                FlipSettings = await SelfUpdatingValue<FlipSettings>.CreateNoUpdate(() => DEFAULT_SETTINGS);
                SubSessionToEventsFor(SessionInfo.McUuid);
            }
            else
            {
                using var sub2SettingsSpan = socket.CreateActivity("sub2Settings", ConSpan);
                await SubToSettings(UserId);
            }

            loadSpan.Dispose();
            UpdateExtraDelay();
        }

        private async Task SendLoginPromptMessage(string stringId)
        {
            var index = 1;
            while (UserId.Value == null)
            {
                SendMessage(COFLNET + $"Please {McColorCodes.WHITE}§lclick this [LINK] to login{McColorCodes.GRAY} so we can load your settings §8(do '/cofl help login' to get more info)",
                    GetAuthLink(stringId));
                await Task.Delay(TimeSpan.FromSeconds(300 * index++)).ConfigureAwait(false);

                if (UserId.Value != default)
                    return;
                SendMessage("do /cofl stop to stop receiving this (or click this message)", "/cofl stop");
            }
        }

        protected virtual async Task SubToSettings(string val)
        {
            ConSpan.Log("subbing to settings of " + val);
            var flipSettingsTask = SelfUpdatingValue<FlipSettings>.Create(val, "flipSettings", () => DEFAULT_SETTINGS);
            var accountSettingsTask = SelfUpdatingValue<AccountSettings>.Create(val, "accountSettings");
            AccountInfo = await SelfUpdatingValue<AccountInfo>.Create(val, "accountInfo", () => new AccountInfo() { UserId = int.Parse(val ?? "0") });
            FlipSettings = await flipSettingsTask;

            // make sure there is only one connection
            AccountInfo.Value.ActiveConnectionId = SessionInfo.ConnectionId;
            _ = socket.TryAsyncTimes(() => AccountInfo.Update(AccountInfo.Value), "accountInfo update");

            FlipSettings.OnChange += UpdateSettings;
            AccountInfo.OnChange += (ai) => Task.Run(async () => await UpdateAccountInfo(ai));
            if (AccountInfo.Value != default)
                await UpdateAccountInfo(AccountInfo);
            else
                Console.WriteLine("accountinfo is default");

            AccountSettings = await accountSettingsTask;
            SubSessionToEventsFor(val);
            await ApplyFlipSettings(FlipSettings.Value, ConSpan);
        }

        private void SubSessionToEventsFor(string val)
        {
            SessionInfo.EventBrokerSub?.Unsubscribe();
            SessionInfo.EventBrokerSub = socket.GetService<EventBrokerClient>().SubEvents(val, onchange =>
            {
                Console.WriteLine("received update from event");
                SendMessage(COFLNET + onchange.Message);
            });
        }

        /// <summary>
        /// Makes sure given settings are applied
        /// </summary>
        /// <param name="settings"></param>
        /// <param name="span"></param>
        private async Task ApplyFlipSettings(FlipSettings settings, Activity span)
        {
            if (settings == null)
                return;
            try
            {
                if (FlipSettings.Value?.ModSettings?.Chat ?? false)
                    await ChatCommand.MakeSureChatIsConnected(socket);
                if (settings.BasedOnLBin && settings.AllowedFinders != LowPricedAuction.FinderType.SNIPER)
                {
                    socket.SendMessage(new DialogBuilder().CoflCommand<SetCommand>(McColorCodes.RED + "Your profit is based on lbin, therefore you should only use the `sniper` flip finder to maximise speed", "finder sniper", "Click to only use the sniper"));
                }
                if (settings.Visibility.LowestBin && settings.AllowedFinders != LowPricedAuction.FinderType.SNIPER && !SessionInfo.LbinWarningSent)
                {
                    SessionInfo.LbinWarningSent = true;
                    socket.SendMessage(new DialogBuilder().CoflCommand<SetCommand>(McColorCodes.RED + "You enabled display of lbin on a flip finder that is not based on lbin (but median). "
                        + "That slows down flips because the lbin has to be searched for before the flip is sent to you."
                        + "If you are okay with that, ignore this warning. If you want flips faster click this to disable displaying the lbin",
                        "showlbin false",
                        $"You can also enable only lbin based flips \nby executing {McColorCodes.AQUA}/cofl set finders sniper.\nClicking this will hide lbin in flip messages. \nYou can still see lbin in item descriptions."));
                }
                // preload flip settings
                settings.MatchesSettings(new FlipInstance() { Auction = new SaveAuction() });
                span.Log(JSON.Stringify(settings));
            }
            catch (Exception e)
            {
                socket.Error(e, "applying flip settings");
            }
        }

        /// <summary>
        /// Called when setting were updated to apply them
        /// </summary>
        /// <param name="settings"></param>
        protected virtual void UpdateSettings(FlipSettings settings)
        {
            using var span = socket.CreateActivity("SettingsUpdate", ConSpan);
            var changed = settings.LastChanged;
            if (changed == null)
            {
                changed = "Settings changed";
            }
            if (changed != "preventUpdateMsg")
                SendMessage($"{COFLNET}{changed}");

            ApplyFlipSettings(settings, span).Wait();
        }

        protected virtual async Task UpdateAccountInfo(AccountInfo info)
        {
            using var span = socket.CreateActivity("AuthUpdate", ConSpan)
                    .AddTag("premium", info.Tier.ToString())
                    .AddTag("userId", info.UserId.ToString());

            var userIsVerifiedTask = VerificationHandler.MakeSureUserIsVerified(info);
            if (info.UserId == 0)
            {
                info.UserId = (socket as IFlipConnection).UserId;
                await AccountInfo.Update(info);
            }

            try
            {
                var userIsTest = info.UserId > 0 && info.UserId < 10;
                if (info.ActiveConnectionId != SessionInfo.ConnectionId && !string.IsNullOrEmpty(info.ActiveConnectionId) && !userIsTest)
                {
                    // wait for settings sync
                    await Task.Delay(500).ConfigureAwait(false);
                    if (info.ActiveConnectionId != SessionInfo.ConnectionId)
                    {
                        // another connection of this account was opened, close this one
                        SendMessage("\n\n" + COFLNET + McColorCodes.GREEN + "We closed this connection because you opened another one", null,
                            "To protect against your mod opening\nmultiple connections which you can't stop,\nwe closed this one.\nThe latest one you opened should still be active");
                        socket.ExecuteCommand("/cofl stop");
                        span.Log("connected from somewhere else");
                        socket.Close();
                        return;
                    }
                }

                if (info.ConIds.Contains("logout"))
                {
                    SendMessage("You have been logged out");
                    span.Log("force loggout");
                    info.ConIds.Remove("logout");
                    await this.AccountInfo.Update(info);
                    socket.Close();
                    return;
                }

                await UpdateAccountTier(info);

                span.Log(JsonConvert.SerializeObject(info, Formatting.Indented));
                if (SessionInfo.SentWelcome)
                    return; // don't send hello again
                SessionInfo.SentWelcome = true;
                await SendAuthorizedHello(info);

                // there seems to be a racecondition where the flipsettings are not yet loaded
                for (int i = 0; i < 10; i++)
                {
                    if (!string.IsNullOrEmpty(FlipSettings.Value.Changer))
                        break;
                    await Task.Delay(300);
                }
                if (FlipSettings.Value.ModSettings.AutoStartFlipper)
                {
                    SendMessage(socket.formatProvider.WelcomeMessage());
                    SessionInfo.FlipsEnabled = true;
                    UpdateConnectionTier(info, span);
                }
                else
                {
                    socket.Dialog(db => db.Msg("What do you want to do?").Break
                        .CoflCommand<FlipCommand>($"> {McColorCodes.GOLD}AH flip  ", "true", $"{McColorCodes.GOLD}Show me flips!\n{McColorCodes.DARK_GREEN}(and reask on every start)\nexecutes {McColorCodes.AQUA}/cofl flip")
                        .CoflCommand<FlipCommand>(McColorCodes.DARK_GREEN + " always ah flip ", "always", McColorCodes.DARK_GREEN + "don't show this again and always show me flips")
                        .DialogLink<EchoDialog>(McColorCodes.BLUE + " use the pricing data ", "alright, nothing else to do :)", "I don't want to flip")
                        .Break);
                    await socket.TriggerTutorial<Welcome>();
                }
                await userIsVerifiedTask;
                return;
            }
            catch (Exception e)
            {
                socket.Error(e, "loading modsocket");
                SendMessage(COFLNET + $"Your settings could not be loaded, please relink again :)");
            }
        }

        public async Task<AccountTier> UpdateAccountTier(AccountInfo info)
        {
            var userApi = socket.GetService<PremiumService>();
            var expiresTask = userApi.GetCurrentTier(info.UserId);
            var expires = await expiresTask;
            info.Tier = expires.Item1;
            info.ExpiresAt = expires.Item2;
            return info.Tier;
        }

        public async Task<IEnumerable<string>> GetMinecraftAccountUuids()
        {
            var result = await McAccountService.Instance.GetAllAccounts(UserId.Value, DateTime.UtcNow - TimeSpan.FromDays(30));
            if (result == null || result.Count() == 0)
                return new string[] { SessionInfo.McUuid };
            if (!result.Contains(SessionInfo.McUuid))
                result = result.Append(SessionInfo.McUuid);
            return result;
        }

        protected virtual void SendMessage(string message, string click = null, string hover = null)
        {
            socket.SendMessage(message, click, hover);
        }
        protected virtual void SendMessage(ChatPart[] parts)
        {
            socket.SendMessage(parts);
        }
        public virtual string GetAuthLink(string stringId)
        {
            return $"https://sky.coflnet.com/authmod?mcid={SessionInfo.McName}&conId={HttpUtility.UrlEncode(stringId)}";
        }


        public void UpdateConnectionTier(AccountInfo accountInfo, Activity span = null)
        {
            this.ConSpan.SetTag("tier", accountInfo?.Tier.ToString());
            span?.Log("set connection tier to " + accountInfo?.Tier.ToString());
            if (accountInfo == null)
                return;

            if (socket.HasFlippingDisabled())
                return;
            if (FlipSettings.Value.DisableFlips)
            {
                SendMessage(COFLNET + "you currently don't receive flips because you disabled them", "/cofl set disableflips false", "click to enable");
                return;
            }

            if (accountInfo.Tier == AccountTier.NONE)
                FlipperService.Instance.AddNonConnection(socket, false);
            if (accountInfo.Tier == AccountTier.PREMIUM)
                FlipperService.Instance.AddConnection(socket, false);
            else if (accountInfo.Tier == AccountTier.PREMIUM_PLUS)
            {
                FlipperService.Instance.AddConnectionPlus(socket, false);
            }
            else if (accountInfo.Tier == AccountTier.STARTER_PREMIUM)
                FlipperService.Instance.AddStarterConnection(socket, false);
            else if (accountInfo.Tier == AccountTier.SUPER_PREMIUM)
            {
                DiHandler.GetService<PreApiService>().AddUser(socket, accountInfo.ExpiresAt);
                FlipperService.Instance.AddConnectionPlus(socket, false);
                socket.SendMessage(McColorCodes.GRAY + "speedup enabled, remaining " + (accountInfo.ExpiresAt - DateTime.UtcNow).ToString("g"));
            }
        }


        protected virtual async Task SendAuthorizedHello(AccountInfo accountInfo)
        {
            var user = UserService.Instance.GetUserById(accountInfo.UserId);
            var email = user.Email;
            string anonymisedEmail = UserService.Instance.AnonymiseEmail(email);
            if (this.SessionInfo.McName == null)
                await Task.Delay(800).ConfigureAwait(false); // allow another half second for the playername to be loaded
            var messageStart = $"Hello {this.SessionInfo.McName} ({anonymisedEmail}) \n";
            if (accountInfo.Tier != AccountTier.NONE && accountInfo.ExpiresAt > DateTime.UtcNow)
                SendMessage(
                    COFLNET + messageStart + $"You have {McColorCodes.GREEN}{accountInfo.Tier.ToString()} until {accountInfo.ExpiresAt.ToString("yyyy-MMM-dd HH:mm")} UTC", null,
                    $"That is in {McColorCodes.GREEN + (accountInfo.ExpiresAt - DateTime.UtcNow).ToString("d'd 'h'h 'm'm 's's'")}"
                );
            else
                SendMessage(COFLNET + messageStart + $"You use the {McColorCodes.BOLD}FREE{McColorCodes.RESET} version of the flip finder");

            await Task.Delay(300).ConfigureAwait(false);
        }

        /// <summary>
        /// Execute every minute to clear collections
        /// </summary>
        internal void HouseKeeping()
        {
            flipProcesser.MinuteCleanup();
            while (socket.TopBlocked.Count > 300)
                socket.TopBlocked.TryDequeue(out _);
        }

        private void SendPing()
        {
            var blockedFlipFilterCount = flipProcesser.BlockedFlipCount;
            using var span = socket.CreateActivity("ping", ConSpan)?.AddTag("count", blockedFlipFilterCount);
            try
            {
                UpdateExtraDelay();
                spamController.Reset();
                if (blockedFlipFilterCount > 0 && SessionInfo.LastBlockedMsg.AddMinutes(FlipSettings.Value.ModSettings.MinutesBetweenBlocked) < DateTime.UtcNow)
                {
                    socket.SendMessage(new ChatPart(COFLNET + $"there were {blockedFlipFilterCount} flips blocked by your filter the last minute",
                        "/cofl blocked",
                        $"{McColorCodes.GRAY} execute {McColorCodes.AQUA}/cofl blocked{McColorCodes.GRAY} to list blocked flips"),
                        new ChatPart(" ", "/cofl void", null));
                    if (SessionInfo.LastBlockedMsg == default)
                        socket.TryAsyncTimes(() => socket.TriggerTutorial<Sky.ModCommands.Tutorials.Blocked>(), "blocked tutorial");
                    SessionInfo.LastBlockedMsg = DateTime.UtcNow;

                    // remove blocked (if clear failed to do so)
                    while (socket.TopBlocked.Count > 345)
                        socket.TopBlocked.TryDequeue(out _);
                }
                else
                {
                    socket.Send(Response.Create("ping", 0));

                    UpdateConnectionTier(AccountInfo, span);
                }
                if (blockedFlipFilterCount > 1000)
                    span.SetTag("error", true);
                SendReminders();
            }
            catch (System.InvalidOperationException)
            {
                socket.RemoveMySelf();
            }
            catch (Exception e)
            {
                span.Log("could not send ping");
                socket.Error(e, "on ping"); // CloseBecauseError(e);
            }
        }

        private void SendReminders()
        {
            if (AccountSettings?.Value?.Reminders == null)
                return;
            var reminders = AccountSettings?.Value?.Reminders?.Where(r => r.TriggerTime < DateTime.UtcNow).ToList();
            foreach (var item in reminders)
            {
                socket.SendSound("note.pling");
                SendMessage($"[§1R§6eminder§f]§7: " + McColorCodes.WHITE + item.Text);
                AccountSettings.Value.Reminders.Remove(item);
            }
            if (reminders?.Count > 0)
                AccountSettings.Update().Wait();
        }

        public virtual void StartTimer(double seconds = 10, string prefix = "§c")
        {
            var mod = this.FlipSettings.Value?.ModSettings;
            if (socket.Version == "1.3-Alpha")
                socket.SendMessage(COFLNET + "You have to update your mod to support the timer");
            else
                socket.Send(Response.Create("countdown", new
                {
                    seconds = seconds,
                    widthPercent = (mod?.TimerX ?? 0) == 0 ? 10 : mod.TimerX,
                    heightPercent = (mod?.TimerY ?? 0) == 0 ? 10 : mod.TimerY,
                    scale = (mod?.TimerScale ?? 0) == 0 ? 2 : mod.TimerScale,
                    prefix = mod?.TimerPrefix ?? prefix,
                    maxPrecision = (mod?.TimerPrecision ?? 0) == 0 ? 3 : mod.TimerPrecision
                }));
        }

        private void UpdateExtraDelay()
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(new Random().Next(1, 3000)).ConfigureAwait(false);
                try
                {
                    var ids = await GetMinecraftAccountUuids();

                    var sumary = await delayHandler.Update(ids, LastCaptchaSolveTime);

                    if (sumary.AntiAfk && !socket.HasFlippingDisabled() && SessionInfo.captchaInfo.LastGenerated < DateTime.UtcNow.AddMinutes(-20))
                    {
                        SendMessage("Hello there, you acted suspiciously like a macro bot (flipped consistently for multiple hours and/or fast). \nPlease select the correct answer to prove that you are not.", null, "You are delayed until you do");
                        SendMessage(new CaptchaGenerator().SetupChallenge(socket, SessionInfo.captchaInfo));
                    }
                    if (sumary.MacroWarning)
                    {
                        using var span = socket.CreateActivity("macroWarning", ConSpan).AddTag("name", SessionInfo.McName);
                        SendMessage("\nWe detected macro usage on your account. \nPlease stop using any sort of unfair advantage immediately. You may be additionally and permanently delayed if you don't.");
                    }

                    if (sumary.Penalty > TimeSpan.Zero)
                    {
                        using var span = socket.CreateActivity("nerv", ConSpan);
                        span.Log(JsonConvert.SerializeObject(ids, Formatting.Indented));
                        span.Log(JsonConvert.SerializeObject(sumary, Formatting.Indented));
                    }
                }
                catch (Exception e)
                {
                    socket.Error(e, "retrieving penalty");
                }
            });
        }

        private DateTime LastCaptchaSolveTime => (AccountInfo?.Value?.LastCaptchaSolve > SessionInfo.LastCaptchaSolve ? AccountInfo.Value.LastCaptchaSolve : SessionInfo.LastCaptchaSolve);

        internal async Task SendFlipBatch(IEnumerable<LowPricedAuction> flips)
        {
            await flipProcesser.NewFlips(flips);
        }

        public void Dispose()
        {
            FlipSettings?.Dispose();
            UserId?.Dispose();
            AccountInfo?.Dispose();
            SessionInfo?.Dispose();
            PingTimer?.Dispose();
        }
    }
}
