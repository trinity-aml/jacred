﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using JacRed.Engine.CORE;
using JacRed.Models.tParse;
using IO = System.IO;
using JacRed.Engine;
using JacRed.Models.Details;

namespace JacRed.Controllers.CRON
{
    [Route("/cron/megapeer/[action]")]
    public class MegapeerController : BaseController
    {
        // Реальные категории
        static readonly List<string> Cats = new List<string>() { "80", "79", "6", "5", "55", "57", "76" };

        // Человеческие названия категорий
        static readonly Dictionary<string, string> CatNames = new Dictionary<string, string>()
        {
            {"80", "Зарубежные фильмы"},
            {"79", "Наши фильмы"},
            {"6",  "Зарубежные сериалы"},
            {"5",  "Наши сериалы"},
            {"55", "Документалистика"},
            {"57", "Телепередачи"},
            {"76", "Мультипликация"},
        };

        static string CatTitle(string cat) =>
            CatNames.TryGetValue(cat, out var n) ? n : $"Категория {cat}";

        static Dictionary<string, List<TaskParse>> taskParse = new Dictionary<string, List<TaskParse>>();

        static MegapeerController()
        {
            try
            {
                if (IO.File.Exists("Data/temp/megapeer_taskParse.json"))
                    taskParse = JsonConvert.DeserializeObject<Dictionary<string, List<TaskParse>>>(IO.File.ReadAllText("Data/temp/megapeer_taskParse.json")) ?? new Dictionary<string, List<TaskParse>>();
            }
            catch
            {
                taskParse = new Dictionary<string, List<TaskParse>>();
            }
        }

        // Маркер валидной страницы
        const string BrowsePageValidMarker = "id=\"logo\"";

        // Защита от параллельных запусков browse
        static readonly SemaphoreSlim _browseLock = new SemaphoreSlim(1, 1);

        static readonly int[] BrowseDelayCycleMs = new[] { 1500, 2500, 3500 };
        static int _browseDelayIndex = 0;

        static int NextBrowseDelayMs()
        {
            int i = Interlocked.Increment(ref _browseDelayIndex) - 1;
            return BrowseDelayCycleMs[Math.Abs(i % BrowseDelayCycleMs.Length)];
        }

        static readonly int[] DetailsDelayCycleMs = new[] { 250, 400, 600 };
        static int _detailsDelayIndex = 0;

        static int NextDetailsDelayMs()
        {
            int i = Interlocked.Increment(ref _detailsDelayIndex) - 1;
            return DetailsDelayCycleMs[Math.Abs(i % DetailsDelayCycleMs.Length)];
        }

        #region Cookie (берём из AppInit.conf.Megapeer.cookie)
        static string GetMegapeerCookie()
        {
            try
            {
                var c = AppInit.conf.Megapeer.cookie;
                if (string.IsNullOrWhiteSpace(c))
                    return null;

                // допускаем что в конфиге может лежать "cookie: ..." или просто "a=b; c=d"
                c = c.Trim();
                if (c.StartsWith("cookie:", StringComparison.OrdinalIgnoreCase))
                    c = c.Substring("cookie:".Length).Trim();

                return c.Trim().TrimEnd(';');
            }
            catch { return null; }
        }

        static void AddCookieHeader(List<(string name, string val)> headers)
        {
            var cookie = GetMegapeerCookie();
            if (!string.IsNullOrWhiteSpace(cookie))
                headers.Add(("cookie", cookie));
        }
        #endregion

        async Task<string> GetMegapeerBrowsePage(string url, string cat)
        {
            await _browseLock.WaitAsync();
            try
            {
                var headers = new List<(string name, string val)>()
                {
                    ("dnt", "1"),
                    ("pragma", "no-cache"),
                    ("cache-control", "no-cache"),
                    ("referer", $"{AppInit.conf.Megapeer.rqHost()}/browse.php?cat={cat}&page=0"),
                    ("sec-fetch-dest", "document"),
                    ("sec-fetch-mode", "navigate"),
                    ("sec-fetch-site", "same-origin"),
                    ("sec-fetch-user", "?1"),
                    ("upgrade-insecure-requests", "1")
                };
                AddCookieHeader(headers);

                const int maxRetries = 3;
                for (int attempt = 1; attempt <= maxRetries; attempt++)
                {
                    await Task.Delay(NextBrowseDelayMs());

                    var (content, response) = await HttpClient.BaseGetAsync(
                        url,
                        encoding: Encoding.GetEncoding(1251),
                        useproxy: AppInit.conf.Megapeer.useproxy,
                        addHeaders: headers
                    );

                    if (!string.IsNullOrEmpty(content) && content.Contains(BrowsePageValidMarker))
                        return content;

                    if (attempt < maxRetries)
                        continue;

                    return null;
                }

                return null;
            }
            finally
            {
                _browseLock.Release();
            }
        }

        #region Parse
        static bool _workParse = false;

        // default=0 — чинит /parse без ?page
        async public Task<string> Parse(int page = 0)
        {
            if (AppInit.conf?.disable_trackers != null && AppInit.conf.disable_trackers.Contains("megapeer", StringComparer.OrdinalIgnoreCase))
                return "disabled";
            if (_workParse)
                return "work";

            _workParse = true;

            int totalFound = 0;
            int totalProcessed = 0;
            var perCat = new Dictionary<string, (int found, int processed)>();

            try
            {
                foreach (var cat in Cats)
                {
                    var r = await parsePage(cat, page);
                    totalFound += r.found;
                    totalProcessed += r.processed;

                    if (!perCat.ContainsKey(cat)) perCat[cat] = (0, 0);
                    perCat[cat] = (perCat[cat].found + r.found, perCat[cat].processed + r.processed);
                }

                var resp = BuildLog(totalFound, totalProcessed, perCat, Cats);
                ParserLog.Write("megapeer", resp.Split('\n')[0].Trim()); // одна строка в лог — итог
                return resp;
            }
            catch (Exception ex)
            {
                ParserLog.Write("megapeer", $"error: {ex.Message}");
                return $"error: {ex.Message}";
            }
            finally
            {
                _workParse = false;
            }
        }
        #endregion

        #region UpdateTasksParse
        async public Task<string> UpdateTasksParse()
        {
            if (AppInit.conf?.disable_trackers != null && AppInit.conf.disable_trackers.Contains("megapeer", StringComparer.OrdinalIgnoreCase))
                return "disabled";

            int limitPage = 0;
            int.TryParse(Request?.Query["limit_page"], out limitPage);

            foreach (var cat in Cats)
            {
                string html = await GetMegapeerBrowsePage($"{AppInit.conf.Megapeer.rqHost()}/browse.php?cat={cat}&page=0", cat);
                if (string.IsNullOrWhiteSpace(html))
                    continue;

                // Реально в HTML: "Всего: 35587 (max. 500)"
                int total = 0, maxLimit = 0;

                int.TryParse(Regex.Match(html, @"Всего:\s*([0-9]+)", RegexOptions.IgnoreCase).Groups[1].Value, out total);
                int.TryParse(Regex.Match(html, @"max\.\s*([0-9]+)", RegexOptions.IgnoreCase).Groups[1].Value, out maxLimit);

                int pagesCount = 0;
                if (total > 0)
                    pagesCount = (int)Math.Ceiling(total / 50.0);

                if (maxLimit > 0)
                    pagesCount = pagesCount > 0 ? Math.Min(pagesCount, maxLimit) : maxLimit;

                if (pagesCount <= 0)
                    pagesCount = 1;

                int pagesToTake = (limitPage > 0) ? Math.Min(limitPage, pagesCount) : pagesCount;

                if (!taskParse.ContainsKey(cat))
                    taskParse[cat] = new List<TaskParse>();

                var list = taskParse[cat];
                for (int p = 0; p < pagesToTake; p++)
                {
                    if (list.FirstOrDefault(i => i.page == p) == null)
                        list.Add(new TaskParse(p) { updateTime = DateTime.MinValue });
                }
            }

            try
            {
                IO.Directory.CreateDirectory("Data/temp");
                IO.File.WriteAllText("Data/temp/megapeer_taskParse.json", JsonConvert.SerializeObject(taskParse));
            }
            catch { }

            return "ok";
        }
        #endregion

        #region ParseAllTask
        static bool _parseAllTaskWork = false;

        async public Task<string> ParseAllTask()
        {
            if (AppInit.conf?.disable_trackers != null && AppInit.conf.disable_trackers.Contains("megapeer", StringComparer.OrdinalIgnoreCase))
                return "disabled";
            if (_parseAllTaskWork)
                return "work";

            _parseAllTaskWork = true;

            int totalFound = 0;
            int totalProcessed = 0;
            var perCat = new Dictionary<string, (int found, int processed)>();

            try
            {
                foreach (var task in taskParse.ToArray())
                {
                    foreach (var val in task.Value.ToArray())
                    {
                        if (DateTime.Today == val.updateTime)
                            continue;

                        var r = await parsePage(task.Key, val.page);

                        totalFound += r.found;
                        totalProcessed += r.processed;

                        if (!perCat.ContainsKey(task.Key)) perCat[task.Key] = (0, 0);
                        perCat[task.Key] = (perCat[task.Key].found + r.found, perCat[task.Key].processed + r.processed);

                        if (r.ok)
                            val.updateTime = DateTime.Today;
                    }
                }

                try
                {
                    IO.Directory.CreateDirectory("Data/temp");
                    IO.File.WriteAllText("Data/temp/megapeer_taskParse.json", JsonConvert.SerializeObject(taskParse));
                }
                catch { }

                var resp = BuildLog(totalFound, totalProcessed, perCat, Cats);
                ParserLog.Write("megapeer", resp.Split('\n')[0].Trim()); // одна строка в лог
                return resp;
            }
            catch (Exception ex)
            {
                ParserLog.Write("megapeer", $"error: {ex.Message}");
                return $"error: {ex.Message}";
            }
            finally
            {
                _parseAllTaskWork = false;
            }
        }
        #endregion

        #region parsePage
        async Task<(bool ok, int found, int processed)> parsePage(string cat, int page)
        {
            string html = await GetMegapeerBrowsePage($"{AppInit.conf.Megapeer.rqHost()}/browse.php?cat={cat}&page={page}", cat);
            if (string.IsNullOrWhiteSpace(html) || !html.Contains(BrowsePageValidMarker))
                return (false, 0, 0);

            var torrents = new List<MegapeerDetails>();

            foreach (string row in html.Split("class=\"table_fon\"").Skip(1))
            {
                string Match(string pattern, int index = 1)
                {
                    string res = HttpUtility.HtmlDecode(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline).Match(row).Groups[index].Value.Trim());
                    res = Regex.Replace(res, "[\n\r\t ]+", " ");
                    return res.Replace("\u00A0", " ").Trim();
                }

                // createTime: первый <td> в строке
                string dtRaw = Match(@"<td>\s*([^<]+)\s*</td>", 1);
                DateTime createTime = ParseRuShortDate(dtRaw);
                if (createTime == default)
                    continue;

                // downloadId: абсолютная/относительная ссылка
                string downloadId = Match(@"href\s*=\s*""(?:https?://[^""]+)?/download/([0-9]+)""", 1);
                if (string.IsNullOrWhiteSpace(downloadId))
                    continue;

                // url + title
                string url = Match(@"<a[^>]*class=""url""[^>]*href=""([^""]+)""", 1);
                if (string.IsNullOrWhiteSpace(url))
                    url = Match(@"<a[^>]*href=""([^""]+)""[^>]*class=""[^""]*\burl\b[^""]*""", 1);

                string title = Match(@"class=""url""[^>]*>\s*([^<]+)\s*</a>", 1);
                if (string.IsNullOrWhiteSpace(title))
                    continue;

                url = NormalizeUrl(url);

                // sizeName
                string sizeName = Match(@"<td[^>]*align=""right""[^>]*>\s*([^<]+)", 1);

                // sid/pir в <font>
                string _sid = Match(@"alt=""S""[^>]*>\s*<font[^>]*>\s*([0-9]+)\s*</font>", 1);
                string _pir = Match(@"alt=""L""[^>]*>\s*<font[^>]*>\s*([0-9]+)\s*</font>", 1);

                int.TryParse(_sid, out int sid);
                int.TryParse(_pir, out int pir);

                // relased
                int relased = 0;
                var my = Regex.Match(title, @"\(([1-2][0-9]{3})\)");
                if (my.Success)
                    int.TryParse(my.Groups[1].Value, out relased);

                // name/originalname — эвристика (originalname потом перезапишем с карточки)
                string name = null, originalname = null;

                if (string.IsNullOrWhiteSpace(name))
                    name = Regex.Split(title, "(\\[|\\/|\\(|\\|)", RegexOptions.IgnoreCase)[0].Trim();

                // types
                string[] types = Array.Empty<string>();
                switch (cat)
                {
                    case "80":
                    case "79":
                        types = new[] { "movie" };
                        break;
                    case "6":
                    case "5":
                        types = new[] { "serial" };
                        break;
                    case "55":
                        types = new[] { "docuserial", "documovie" };
                        break;
                    case "57":
                        types = new[] { "tvshow" };
                        break;
                    case "76":
                        types = new[] { "multfilm", "multserial" };
                        break;
                }

                torrents.Add(new MegapeerDetails()
                {
                    trackerName = "megapeer",
                    types = types,
                    url = url,
                    title = title,
                    sid = sid,
                    pir = pir,
                    sizeName = sizeName,
                    createTime = createTime,
                    name = name,
                    originalname = originalname,
                    relased = relased,
                    downloadId = downloadId
                });
            }

            int found = torrents.Count;
            int processed = 0;

            await FileDB.AddOrUpdate(torrents, async (t, db) =>
            {
                var info = await GetDetailsInfoWithRetry(t.url, attempts: 3);
                string magnet = info.magnet;
                string orig = info.originalname;

                // fallback: /download -> magnet (если нужно)
                if (string.IsNullOrWhiteSpace(magnet))
                {
                    try
                    {
                        byte[] _t = await HttpClient.Download($"{AppInit.conf.Megapeer.host}/download/{t.downloadId}", referer: AppInit.conf.Megapeer.host);
                        magnet = BencodeTo.Magnet(_t);
                    }
                    catch { }
                }

                if (!string.IsNullOrWhiteSpace(magnet))
                {
                    t.magnet = magnet;
                    if (!string.IsNullOrWhiteSpace(orig))
                        t.originalname = orig;

                    Interlocked.Increment(ref processed);
                    return true;
                }

                return false;
            });

            return (true, found, processed);
        }
        #endregion

        #region Details helpers (magnet + originalname)
        async Task<(string magnet, string originalname)> GetDetailsInfoWithRetry(string detailsUrl, int attempts = 3)
        {
            await Task.Delay(NextDetailsDelayMs());

            var headers = new List<(string name, string val)>()
            {
                ("dnt", "1"),
                ("pragma", "no-cache"),
                ("cache-control", "no-cache"),
                ("referer", AppInit.conf.Megapeer.host),
                ("upgrade-insecure-requests", "1")
            };
            AddCookieHeader(headers);

            for (int i = 1; i <= attempts; i++)
            {
                try
                {
                    var (html, response) = await HttpClient.BaseGetAsync(
                        detailsUrl,
                        encoding: Encoding.GetEncoding(1251),
                        useproxy: AppInit.conf.Megapeer.useproxy,
                        addHeaders: headers
                    );

                    if (string.IsNullOrWhiteSpace(html))
                        throw new Exception("empty");

                    // magnet
                    string magnet = null;
                    var mm = Regex.Match(html, @"href\s*=\s*""(magnet:\?xt=urn:btih:[^""]+)""", RegexOptions.IgnoreCase);
                    if (mm.Success)
                        magnet = HttpUtility.HtmlDecode(mm.Groups[1].Value);

                    // originalname: варианты верстки
                    string original = null;

                    // <b>Оригинальное название:</b> Xxx<br>
                    var mo1 = Regex.Match(html, @"Оригинальное\s*название\s*:\s*</b>\s*([^<\r\n]+)", RegexOptions.IgnoreCase);
                    if (mo1.Success)
                        original = HttpUtility.HtmlDecode(mo1.Groups[1].Value).Trim();

                    // <font ...><b>Оригинальное название</b></font>: Xxx<br>
                    if (string.IsNullOrWhiteSpace(original))
                    {
                        var mo2 = Regex.Match(html, @"Оригинальное\s*название\s*</b>\s*</font>\s*:\s*([^<\r\n]+)", RegexOptions.IgnoreCase);
                        if (mo2.Success)
                            original = HttpUtility.HtmlDecode(mo2.Groups[1].Value).Trim();
                    }

                    // fallback
                    if (string.IsNullOrWhiteSpace(original))
                    {
                        var mo3 = Regex.Match(html, @"Оригинальное\s*название\s*[:\-]\s*([^<\r\n]+)", RegexOptions.IgnoreCase);
                        if (mo3.Success)
                            original = HttpUtility.HtmlDecode(mo3.Groups[1].Value).Trim();
                    }

                    if (!string.IsNullOrWhiteSpace(magnet) || !string.IsNullOrWhiteSpace(original))
                        return (magnet, original);
                }
                catch { }

                await Task.Delay(500 * i);
            }

            return (null, null);
        }
        #endregion

        #region Common helpers
        static DateTime ParseRuShortDate(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return default;

            s = s.Trim();
            var ru = new CultureInfo("ru-RU");

            // Пример: "9 Фев 26"
            string[] fmts = { "d MMM yy", "dd MMM yy", "d MMMM yy", "dd MMMM yy" };

            if (DateTime.TryParseExact(s, fmts, ru, DateTimeStyles.None, out var dt))
                return dt;

            if (DateTime.TryParse(s, ru, DateTimeStyles.None, out dt))
                return dt;

            return default;
        }

        static string NormalizeUrl(string href)
        {
            if (string.IsNullOrWhiteSpace(href))
                return href;

            href = href.Trim();

            if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return href;

            string host = AppInit.conf.Megapeer.host?.TrimEnd('/') ?? "";

            if (href.StartsWith("/"))
                return host + href;

            return host + "/" + href;
        }

        static string BuildLog(int totalFound, int totalProcessed,
            Dictionary<string, (int found, int processed)> perCat, List<string> order)
        {
            var sb = new StringBuilder();
            sb.Append("ok; found=").Append(totalFound).Append("; processed=").Append(totalProcessed).AppendLine();
            sb.AppendLine("by_cat:");
            foreach (var cat in order)
            {
                perCat.TryGetValue(cat, out var v);
                sb.Append("- ").Append(CatTitle(cat)).Append(" (").Append(cat).Append("): ")
                  .Append(v.found).Append("/").Append(v.processed).AppendLine();
            }
            return sb.ToString().TrimEnd();
        }
        #endregion
    }
}
