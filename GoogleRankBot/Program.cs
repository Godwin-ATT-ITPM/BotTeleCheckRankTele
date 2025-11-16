using Microsoft.Playwright;
using Newtonsoft.Json;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

class Program
{
    // ===== CONFIG =====
    static string BOT_TOKEN = "8032582339:AAFVBJ6_NCogeCmJzqROLApwVUe9uGzkkMo";
    static string WATCH_FILE = "watchlist.json";

    // ===== UI FLOW STATE =====
    enum RankUiStep
    {
        None,
        WaitingKeyword,
        WaitingDomain,
        WaitingDevice,
        WaitingCountry
    }

    class RankUiState
    {
        public RankUiStep Step { get; set; } = RankUiStep.None;
        public string Keyword { get; set; } = "";
        public string Domain { get; set; } = "";
        public string Country { get; set; } = "VN";
        public bool IsMobile { get; set; } = false;
    }

    static Dictionary<long, RankUiState> UiStates = new();

    class WatchItem
    {
        public long ChatId { get; set; }
        public string Keyword { get; set; } = "";
        public string Domain { get; set; } = "";
        public string Country { get; set; } = "VN";
        public bool IsMobile { get; set; } = false;
    }

    static async Task Main()
    {
        var bot = new TelegramBotClient(BOT_TOKEN);

        // ⭐ Menu lệnh / (không tự gửi)
        await bot.SetMyCommands(new[]
        {
            new BotCommand { Command = "rank", Description = "Rank Google Desktop VN" },
            new BotCommand { Command = "rankm", Description = "Rank Google Mobile" },
            new BotCommand { Command = "rankc", Description = "Rank Google theo quốc gia" },
            new BotCommand { Command = "ranklist", Description = "Rank nhiều keyword 1 lần" },
            new BotCommand { Command = "addwatch", Description = "Lưu keyword để auto check mỗi ngày" },
            new BotCommand { Command = "mywatch", Description = "Xem danh sách keyword đang theo dõi" },
            new BotCommand { Command = "delwatch", Description = "Xoá keyword khỏi danh sách theo dõi" },
            new BotCommand { Command = "top10", Description = "Xem 10 website top đầu cho 1 keyword" },
            new BotCommand { Command = "rankui", Description = "Rank theo từng bước (UI)" },
            new BotCommand { Command = "help", Description = "Hướng dẫn sử dụng bot" }
        });

        using CancellationTokenSource cts = new CancellationTokenSource();

        var receiverOptions = new ReceiverOptions()
        {
            AllowedUpdates = Array.Empty<UpdateType>()
        };

        // ⭐ Start bot nhận tin nhắn
        bot.StartReceiving(
            HandleUpdateAsync,
            HandleErrorAsync,
            receiverOptions,
            cancellationToken: cts.Token
        );

        // ⭐ Task nền: gửi báo cáo mỗi sáng
        _ = Task.Run(() => DailyReportLoop(bot, cts.Token));

        Console.WriteLine("Bot Telegram PRO đang chạy...");
        Console.ReadLine();
    }

    // ===== LOAD / SAVE WATCH LIST =====
    static List<WatchItem> LoadWatchList()
    {
        try
        {
            if (!File.Exists(WATCH_FILE)) return new List<WatchItem>();
            var json = File.ReadAllText(WATCH_FILE);
            var list = JsonConvert.DeserializeObject<List<WatchItem>>(json);
            return list ?? new List<WatchItem>();
        }
        catch
        {
            return new List<WatchItem>();
        }
    }

    static void SaveWatchList(List<WatchItem> list)
    {
        var json = JsonConvert.SerializeObject(list, Formatting.Indented);
        File.WriteAllText(WATCH_FILE, json);
    }

    // ===== VÒNG LẶP GỬI BÁO CÁO MỖI SÁNG =====
    static async Task DailyReportLoop(ITelegramBotClient bot, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            DateTime now = DateTime.Now;
            DateTime nextRun = new DateTime(now.Year, now.Month, now.Day, 8, 0, 0);
            if (nextRun <= now) nextRun = nextRun.AddDays(1);

            TimeSpan delay = nextRun - now;
            try
            {
                await Task.Delay(delay, token);
            }
            catch (TaskCanceledException)
            {
                break;
            }

            if (token.IsCancellationRequested) break;

            var list = LoadWatchList();
            if (list.Count == 0) continue;

            var groups = list.GroupBy(x => x.ChatId);

            foreach (var group in groups)
            {
                var sb = new StringBuilder();
                sb.AppendLine("🌞 *Báo cáo rank sáng nay*");
                sb.AppendLine();

                foreach (var item in group)
                {
                    string res = await CheckRank(item.Keyword, item.Domain, item.Country, item.IsMobile);
                    sb.AppendLine($"• `{item.Keyword}` / `{item.Domain}` [{item.Country} {(item.IsMobile ? "Mobile" : "Desktop")}]");
                    sb.AppendLine($"  → {res}");
                    sb.AppendLine();
                }

                await bot.SendMessage(
                    chatId: group.Key,
                    text: sb.ToString(),
                    parseMode: ParseMode.Markdown
                );
            }
        }
    }

    // ===== UPDATE HANDLER CHÍNH =====
    static async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken token)
    {
        try
        {
            if (update.Type == UpdateType.Message && update.Message is { } message && message.Text is { } text)
            {
                long chatId = message.Chat.Id;
                var txt = text.Trim();

                if (txt.StartsWith("/"))
                {
                    await HandleCommandAsync(bot, message, txt, token);
                }
                else
                {
                    await HandleFlowOrDefaultAsync(bot, message, txt, token);
                }
            }
            else if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery is { } callback)
            {
                await HandleCallbackAsync(bot, callback, token);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERROR in HandleUpdateAsync: " + ex);
        }
    }

    // ===== HANDLE COMMAND =====
    static async Task HandleCommandAsync(ITelegramBotClient bot, Message message, string text, CancellationToken token)
    {
        long chatId = message.Chat.Id;

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToLowerInvariant();
        var args = parts.Skip(1).ToArray();

        switch (cmd)
        {
            case "/help":
                {
                    string msg =
    @"📌 *Danh sách lệnh:*

/rank `<keyword> <domain>`
→ Rank Google Desktop VN

/rankm `<keyword> <domain>`
→ Rank Mobile VN

/rankc `<keyword> <domain> <country>`
→ Rank theo quốc gia (VN/US/SG/TH/JP/UK...)

/ranklist `<domain> kw1 | kw2 | kw3`
→ Rank nhiều keyword 1 lần

/rankui
→ Rank theo từng bước (UI gợi ý, không cần nhớ cú pháp)

/addwatch `<keyword> <domain> <country> [m|d]`
→ Lưu keyword để auto check mỗi sáng (m=mobile, d=desktop)

/mywatch
→ Xem danh sách đang theo dõi

/delwatch `<stt>`
→ Xoá 1 dòng khỏi danh sách theo dõi

/top10 `<keyword>`
→ Xem 10 website top đầu (nếu chức năng còn bật)

/cancel
→ Hủy flow /rankui hiện tại
";
                    await bot.SendMessage(chatId, msg, parseMode: ParseMode.Markdown, cancellationToken: token);
                    break;
                }

            case "/cancel":
                {
                    if (UiStates.ContainsKey(chatId) && UiStates[chatId].Step != RankUiStep.None)
                    {
                        UiStates.Remove(chatId);
                        await bot.SendMessage(chatId, "✅ Đã huỷ flow /rankui.", cancellationToken: token);
                    }
                    else
                    {
                        await bot.SendMessage(chatId, "Hiện không có flow nào đang chạy.", cancellationToken: token);
                    }
                    break;
                }

            case "/rankui":
                {
                    UiStates[chatId] = new RankUiState
                    {
                        Step = RankUiStep.WaitingKeyword
                    };

                    await bot.SendMessage(
                        chatId,
                        "🔍 *Flow Rank UI*\n\nBước 1️⃣: Nhập keyword cần kiểm tra (vd: `fly88`, `new88`, `789bet` ...)",
                        parseMode: ParseMode.Markdown,
                        cancellationToken: token
                    );
                    break;
                }

            case "/rank":
                {
                    if (args.Length < 2)
                    {
                        await bot.SendMessage(chatId, "❗ Dùng: /rank keyword domain", cancellationToken: token);
                        break;
                    }

                    string keyword = args[0];
                    string domain = args[1];

                    string result = await CheckRank(keyword, domain, "VN", false);
                    await bot.SendMessage(chatId, result, cancellationToken: token);
                    break;
                }

            case "/rankm":
                {
                    if (args.Length < 2)
                    {
                        await bot.SendMessage(chatId, "❗ Dùng: /rankm keyword domain", cancellationToken: token);
                        break;
                    }

                    string keyword = args[0];
                    string domain = args[1];

                    string result = await CheckRank(keyword, domain, "VN", true);
                    await bot.SendMessage(chatId, result, cancellationToken: token);
                    break;
                }

            case "/rankc":
                {
                    if (args.Length < 3)
                    {
                        await bot.SendMessage(chatId, "❗ Dùng: /rankc keyword domain country (VN/US/SG/TH...)", cancellationToken: token);
                        break;
                    }

                    string keyword = args[0];
                    string domain = args[1];
                    string country = args[2].ToUpper();

                    string result = await CheckRank(keyword, domain, country, false);
                    await bot.SendMessage(chatId, result, cancellationToken: token);
                    break;
                }

            case "/ranklist":
                {
                    string input = text.Replace("/ranklist", "").Trim();
                    if (!input.Contains(" "))
                    {
                        await bot.SendMessage(chatId, "❗ Dùng: /ranklist domain kw1 | kw2 | kw3", cancellationToken: token);
                        break;
                    }

                    string domain = input[..input.IndexOf(" ")];
                    string keywordsText = input[(input.IndexOf(" ") + 1)..];

                    var keywords = keywordsText.Split("|").Select(k => k.Trim()).Where(k => k.Length > 0).ToList();

                    var sb = new StringBuilder();
                    sb.AppendLine($"📌 *Kết quả Rank List cho domain:* `{domain}`\n");

                    foreach (var kw in keywords)
                    {
                        string res = await CheckRank(kw, domain, "VN", false);
                        sb.AppendLine($"• `{kw}` → {res}");
                    }

                    await bot.SendMessage(chatId, sb.ToString(), parseMode: ParseMode.Markdown, cancellationToken: token);
                    break;
                }

            case "/addwatch":
                {
                    if (args.Length < 3)
                    {
                        await bot.SendMessage(chatId,
                            "❗ Dùng: /addwatch <keyword> <domain> <country> [m|d]\nVD: /addwatch fly88 fly88.com VN d",
                            cancellationToken: token);
                        break;
                    }

                    string keyword = args[0];
                    string domain = args[1];
                    string country = args[2].ToUpper();
                    bool isMobile = args.Length >= 4 && args[3].ToLower().StartsWith("m");

                    var list = LoadWatchList();
                    list.Add(new WatchItem
                    {
                        ChatId = chatId,
                        Keyword = keyword,
                        Domain = domain,
                        Country = country,
                        IsMobile = isMobile
                    });
                    SaveWatchList(list);

                    string mode = isMobile ? "Mobile" : "Desktop";

                    await bot.SendMessage(chatId,
                        $"✅ Đã lưu: `{keyword}` / `{domain}` [{country} {mode}]",
                        parseMode: ParseMode.Markdown,
                        cancellationToken: token);
                    break;
                }

            case "/mywatch":
                {
                    var list = LoadWatchList().Where(x => x.ChatId == chatId).ToList();

                    if (list.Count == 0)
                    {
                        await bot.SendMessage(chatId,
                            "📭 Bạn chưa lưu keyword nào. Dùng /addwatch để thêm.",
                            cancellationToken: token);
                        break;
                    }

                    var sb = new StringBuilder();
                    sb.AppendLine("📋 *Danh sách keyword đang theo dõi:*\n");

                    int i = 1;
                    foreach (var item in list)
                    {
                        sb.AppendLine($"{i}. `{item.Keyword}` / `{item.Domain}` [{item.Country} {(item.IsMobile ? "Mobile" : "Desktop")}]");
                        i++;
                    }

                    sb.AppendLine("\nDùng `/delwatch <stt>` để xoá.");
                    await bot.SendMessage(chatId, sb.ToString(), parseMode: ParseMode.Markdown, cancellationToken: token);
                    break;
                }

            case "/delwatch":
                {
                    if (args.Length < 1 || !int.TryParse(args[0], out int index))
                    {
                        await bot.SendMessage(chatId, "❗ Dùng: /delwatch <stt>", cancellationToken: token);
                        break;
                    }

                    var all = LoadWatchList();
                    var mine = all.Where(x => x.ChatId == chatId).ToList();

                    if (mine.Count == 0 || index < 1 || index > mine.Count)
                    {
                        await bot.SendMessage(chatId, "❗ STT không hợp lệ.", cancellationToken: token);
                        break;
                    }

                    var target = mine[index - 1];
                    all.RemoveAll(x => x.ChatId == target.ChatId &&
                                       x.Keyword == target.Keyword &&
                                       x.Domain == target.Domain &&
                                       x.Country == target.Country &&
                                       x.IsMobile == target.IsMobile);

                    SaveWatchList(all);

                    await bot.SendMessage(chatId,
                        $"✅ Đã xoá: `{target.Keyword}` / `{target.Domain}` [{target.Country} {(target.IsMobile ? "Mobile" : "Desktop")}]",
                        parseMode: ParseMode.Markdown,
                        cancellationToken: token);
                    break;
                }

            case "/top10":
                {
                    if (args.Length < 1)
                    {
                        await bot.SendMessage(chatId, "❗ Dùng: /top10 <keyword>", cancellationToken: token);
                        break;
                    }

                    string keyword = args[0];
                    string result = await GetTop10(keyword);

                    await bot.SendMessage(chatId, result, parseMode: ParseMode.Markdown, cancellationToken: token);
                    break;
                }

            default:
                {
                    await bot.SendMessage(chatId, "Gõ /help để xem hướng dẫn.", cancellationToken: token);
                    break;
                }
        }
    }

    // ===== HANDLE FLOW /rankui =====
    static async Task HandleFlowOrDefaultAsync(ITelegramBotClient bot, Message message, string text, CancellationToken token)
    {
        long chatId = message.Chat.Id;

        if (!UiStates.TryGetValue(chatId, out var state) || state.Step == RankUiStep.None)
        {
            await bot.SendMessage(chatId, "Gõ /help để xem hướng dẫn hoặc dùng /rankui để chạy flow từng bước.", cancellationToken: token);
            return;
        }

        switch (state.Step)
        {
            case RankUiStep.WaitingKeyword:
                {
                    state.Keyword = text;
                    state.Step = RankUiStep.WaitingDomain;
                    UiStates[chatId] = state;

                    await bot.SendMessage(
                        chatId,
                        $"✅ Keyword: `{state.Keyword}`\n\nBước 2️⃣: Nhập domain cần kiểm tra (vd: `fly88.com`, `789bet.com` ...)",
                        parseMode: ParseMode.Markdown,
                        cancellationToken: token
                    );
                    break;
                }

            case RankUiStep.WaitingDomain:
                {
                    state.Domain = text;
                    state.Step = RankUiStep.WaitingDevice;
                    UiStates[chatId] = state;

                    var keyboard = new InlineKeyboardMarkup(new[]
                    {
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("🖥 Desktop", "ui_dev_desktop"),
                        InlineKeyboardButton.WithCallbackData("📱 Mobile", "ui_dev_mobile")
                    }
                });

                    await bot.SendMessage(
                        chatId,
                        $"✅ Domain: `{state.Domain}`\n\nBước 3️⃣: Chọn thiết bị muốn giả lập:",
                        parseMode: ParseMode.Markdown,
                        replyMarkup: keyboard,
                        cancellationToken: token
                    );
                    break;
                }

            default:
                {
                    await bot.SendMessage(chatId, "Vui lòng chọn trên các nút hoặc gõ /cancel để huỷ flow.", cancellationToken: token);
                    break;
                }
        }
    }

    // ===== HANDLE CALLBACK (inline button) =====
    static async Task HandleCallbackAsync(ITelegramBotClient bot, Telegram.Bot.Types.CallbackQuery callback, CancellationToken token)
    {
        long chatId = callback.Message?.Chat.Id ?? callback.From.Id;
        string data = callback.Data ?? "";

        await bot.AnswerCallbackQuery(callback.Id);

        if (!UiStates.TryGetValue(chatId, out var state))
        {
            await bot.SendMessage(chatId, "Flow đã hết hạn, hãy gõ /rankui để bắt đầu lại.", cancellationToken: token);
            return;
        }

        if (data == "ui_dev_desktop" || data == "ui_dev_mobile")
        {
            state.IsMobile = (data == "ui_dev_mobile");
            state.Step = RankUiStep.WaitingCountry;
            UiStates[chatId] = state;

            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("🇻🇳 VN", "ui_ctry_VN"),
                    InlineKeyboardButton.WithCallbackData("🇺🇸 US", "ui_ctry_US"),
                    InlineKeyboardButton.WithCallbackData("🇸🇬 SG", "ui_ctry_SG")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("🇹🇭 TH", "ui_ctry_TH"),
                    InlineKeyboardButton.WithCallbackData("🇯🇵 JP", "ui_ctry_JP"),
                    InlineKeyboardButton.WithCallbackData("🇬🇧 UK", "ui_ctry_UK")
                }
            });

            string deviceText = state.IsMobile ? "📱 Mobile" : "🖥 Desktop";

            await bot.SendMessage(
                chatId,
                $"✅ Thiết bị: *{deviceText}*\n\nBước 4️⃣: Chọn quốc gia muốn kiểm tra:",
                parseMode: ParseMode.Markdown,
                replyMarkup: keyboard,
                cancellationToken: token
            );

            return;
        }

        if (data.StartsWith("ui_ctry_"))
        {
            string country = data.Replace("ui_ctry_", "");
            state.Country = country.ToUpper();
            state.Step = RankUiStep.None;
            UiStates[chatId] = state;

            string deviceText = state.IsMobile ? "Mobile" : "Desktop";

            await bot.SendMessage(
                chatId,
                $"✅ Quốc gia: *{state.Country}*\n\n⏳ Đang kiểm tra rank cho:\n- Keyword: `{state.Keyword}`\n- Domain: `{state.Domain}`\n- Country: `{state.Country}`\n- Device: *{deviceText}*",
                parseMode: ParseMode.Markdown,
                cancellationToken: token
            );

            string res = await CheckRank(state.Keyword, state.Domain, state.Country, state.IsMobile);

            await bot.SendMessage(
                chatId,
                $"📊 *Kết quả Rank UI:*\n\nKeyword: `{state.Keyword}`\nDomain: `{state.Domain}`\nQuốc gia: *{state.Country}* – *{deviceText}*\n\n{res}",
                parseMode: ParseMode.Markdown,
                cancellationToken: token
            );

            UiStates.Remove(chatId);
            return;
        }

        // Callback khác (không dùng)
        await bot.SendMessage(chatId, "Nút không hợp lệ hoặc flow đã hết hạn. Gõ /rankui để bắt đầu lại.", cancellationToken: token);
    }

    static Task HandleErrorAsync(ITelegramBotClient bot, Exception error, CancellationToken token)
    {
        Console.WriteLine("ERROR: " + error);
        return Task.CompletedTask;
    }

    // ===== CHECK RANK (CHUNG) =====
    static async Task<string> CheckRank(string keyword, string domain, string country = "VN", bool isMobile = false)
    {
        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new()
            {
                Headless = true,
                Args = new[]
                {
                    "--disable-blink-features=AutomationControlled",
                    "--no-sandbox",
                    "--disable-dev-shm-usage"
                }
            });

            string userAgent = isMobile
                ? "Mozilla/5.0 (Linux; Android 10; Mobile) AppleWebKit/537.36 Chrome/124.0 Mobile Safari/537.36"
                : "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0 Safari/537.36";

            string gl = country.ToUpper();
            string hl = country.ToLower();

            var context = await browser.NewContextAsync(new()
            {
                UserAgent = userAgent,
                Locale = hl,
                ViewportSize = isMobile
                    ? new ViewportSize { Width = 390, Height = 844 }
                    : new ViewportSize { Width = 1400, Height = 900 }
            });

            var page = await context.NewPageAsync();

            string googleUrl = $"https://www.google.com/search?q={Uri.EscapeDataString(keyword)}&num=100&hl={hl}&gl={gl}";

            await page.GotoAsync(googleUrl, new PageGotoOptions { Timeout = 60000 });
            await page.WaitForTimeoutAsync(2000);

            var results = await page.QuerySelectorAllAsync("div.MjjYud");

            int position = 1;
            string cleanDomain = domain.Replace("https://", "").Replace("http://", "").TrimEnd('/').ToLower();

            foreach (var item in results)
            {
                var linkEl = await item.QuerySelectorAsync("a[jsname]");
                if (linkEl == null) { position++; continue; }

                string href = await linkEl.GetAttributeAsync("href");
                if (href == null) { position++; continue; }

                if (href.StartsWith("/url?q="))
                {
                    int start = "/url?q=".Length;
                    int end = href.IndexOf("&", start);
                    href = end > start ? href.Substring(start, end - start) : href.Substring(start);
                    href = HttpUtility.UrlDecode(href);
                }

                if (!href.StartsWith("http")) { position++; continue; }

                string cleanHref = href.Replace("https://", "").Replace("http://", "").TrimEnd('/').ToLower();

                if (cleanHref.Contains(cleanDomain))
                {
                    await browser.CloseAsync();
                    return $"⭐ TOP **{position}** ({country} {(isMobile ? "Mobile" : "Desktop")})";
                }

                position++;
            }

            await browser.CloseAsync();
            return $"❌ Không thấy trong TOP 100 ({country} {(isMobile ? "Mobile" : "Desktop")})";
        }
        catch (Exception ex)
        {
            return $"⚠ Lỗi Playwright: {ex.Message}";
        }
    }

    static async Task<string> GetTop10(string keyword)
    {
        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new()
            {
                Headless = true,
                Args = new[]
                {
                    "--disable-blink-features=AutomationControlled",
                    "--no-sandbox",
                    "--disable-dev-shm-usage"
                }
            });

            var context = await browser.NewContextAsync(new()
            {
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/124.0 Safari/537.36",
            });

            var page = await context.NewPageAsync();

            // ⭐ Google Lite (KHÔNG BỊ SGE / UI test)
            string url = $"https://www.google.com/search?lite=1&q={Uri.EscapeDataString(keyword)}&num=20";

            await page.GotoAsync(url, new PageGotoOptions { Timeout = 60000 });
            await page.WaitForTimeoutAsync(1500);

            // ⭐ SERP Lite dùng selector khác
            var links = await page.QuerySelectorAllAsync("a");

            List<string> domains = new();

            foreach (var a in links)
            {
                string href = await a.GetAttributeAsync("href");
                if (href == null) continue;

                if (href.StartsWith("/url?q="))
                {
                    int s = "/url?q=".Length;
                    int e = href.IndexOf("&", s);
                    href = e > s ? href.Substring(s, e - s) : href.Substring(s);
                    href = HttpUtility.UrlDecode(href);
                }

                if (!href.StartsWith("http")) continue;

                try
                {
                    var host = new Uri(href).Host.Replace("www.", "").ToLower();

                    if (host.Contains("google")) continue;
                    if (!domains.Contains(host))
                        domains.Add(host);

                    if (domains.Count >= 10)
                        break;
                }
                catch { }
            }

            if (domains.Count == 0)
                return "❌ Không tìm thấy (Google Lite vẫn không trả SERP — cần Fake IP)";

            StringBuilder sb = new();
            sb.AppendLine($"📌 *TOP 10 website Google (Lite) cho keyword:* `{keyword}`\n");

            int i = 1;
            foreach (var d in domains)
            {
                sb.AppendLine($"{i}. `{d}`");
                i++;
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"⚠ Lỗi: {ex.Message}";
        }
    }
}
