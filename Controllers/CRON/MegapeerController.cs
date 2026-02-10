using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using JacRed.Engine;
using JacRed.Engine.CORE;
using JacRed.Models.Details;
using IO = System.IO;

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

        // Маркер валидной страницы
        const string BrowsePageValidMarker = "id=\"logo\"";

        // Защита от параллельных запусков
        static bool _workParse = false;

        // Защита от параллельных запросов browse (помогает не ловить CF)
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
                var c = AppInit.conf?.Megapeer?.cookie;
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
        #endregion

        #region Cloudflare
        static bool IsCloudflarePage(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
                return false;

            string h = html.ToLowerInvariant();

            if (h.Contains("cdn-cgi/challenge-platform")) return true;
            if (h.Contains("cf-chl")) return true;
            if (h.Contains("just a moment")) return true;
            if (h.Contains("checking your browser")) return true;
            if (h.Contains("attention required")) return true;
            if (h.Contains("cf-error")) return true;

            // "cloudflare" может встречаться и на нормальных страницах, поэтому этот признак слабый
            if (h.Contains("cloudflare") && h.Contains("ray id")) return true;

            return false;
        }
        #endregion

        async Task<string> GetMegapeerBrowsePage(string url, string cat, int page, List<string> errors = null)
        {
            await _browseLock.WaitAsync();
            try
            {
                string referer = $"{AppInit.conf.Megapeer.rqHost()}/browse.php?cat={cat}&page=0";
                string cookie = GetMegapeerCookie();

                var headers = new List<(string name, string val)>()
                {
                    ("dnt", "1"),
                    ("pragma", "no-cache"),
                    ("cache-control", "no-cache"),
                    ("sec-fetch-dest", "document"),
                    ("sec-fetch-mode", "navigate"),
                    ("sec-fetch-site", "same-origin"),
                    ("sec-fetch-user", "?1"),
                    ("upgrade-insecure-requests", "1")
                };

                const int maxRetries = 3;
                for (int attempt = 1; attempt <= maxRetries; attempt++)
                {
                    await Task.Delay(NextBrowseDelayMs());

                    var (content, response) = await HttpClient.BaseGetAsync(
                        url,
                        encoding: Encoding.GetEncoding(1251),
                        cookie: cookie,
                        referer: referer,
                        useproxy: AppInit.conf.Megapeer.useproxy,
                        addHeaders: headers
                    );

                    if (string.IsNullOrWhiteSpace(content))
                        continue;

                    // опционально: быстрый дамп последнего browse для диагностики
                    //try { IO.Directory.CreateDirectory("Data/temp"); IO.File.WriteAllText("Data/temp/megapeer_last_browse.html", content, Encoding.UTF8); } catch { }

                    if (IsCloudflarePage(content))
                    {
                        // HttpClient не проходит JS challenge — делаем backoff и повторяем
                        int backoffMs = attempt == 1 ? 15000 : 30000;
                        errors?.Add($"cloudflare challenge: browse cat={cat} page={page}");
                        await Task.Delay(backoffMs);
                        continue;
                    }

                    if (content.Contains(BrowsePageValidMarker))
                        return content;

                    if (attempt < maxRetries)
                        continue;

                    errors?.Add($"invalid html: browse cat={cat} page={page} (no marker)");
                    return null;
                }

                return null;
            }
            catch (Exception ex)
            {
                errors?.Add($"browse error cat={cat} page={page}: {ex.Message}");
                return null;
            }
            finally
            {
                _browseLock.Release();
            }
        }

        static int GetMaxPagesFromBrowseHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
                return 1;

            int total = 0;
            int maxLimit = 0;

            int.TryParse(Regex.Match(html, @"Всего:\s*([0-9]+)", RegexOptions.IgnoreCase).Groups[1].Value, out total);
            int.TryParse(Regex.Match(html, @"max\.\s*([0-9]+)", RegexOptions.IgnoreCase).Groups[1].Value, out maxLimit);

            int pagesCount = 0;
            if (total > 0)
                pagesCount = (int)Math.Ceiling(total / 50.0);

            // если pagesCount не удалось вычислить, но maxLimit есть — используем его
            if (maxLimit > 0)
                pagesCount = pagesCount > 0 ? Math.Min(pagesCount, maxLimit) : maxLimit;

            if (pagesCount <= 0)
                pagesCount = 1;

            return pagesCount;
        }

        #region Parse
        /// <summary>
        /// /cron/megapeer/parse?page=0&maxpage=2  -> страницы 0 и 1\n        /// maxpage = количество страниц (НЕ номер последней страницы)\n        /// если maxpage не задан или <=0 -> парсим только одну страницу (как раньше)\n        /// </summary>
        async public Task<string> Parse(int page = 0, int maxpage = 0)
        {
            if (AppInit.conf?.disable_trackers != null && AppInit.conf.disable_trackers.Contains("megapeer", StringComparer.OrdinalIgnoreCase))
                return "disabled";
            if (_workParse)
                return "work";

            _workParse = true;

            int totalFound = 0;
            int totalProcessed = 0;
            int totalAdded = 0;
            int totalUpdated = 0;

            var perCat = new Dictionary<string, (int found, int processed)>();
            var errors = new List<string>();

            try
            {
                int startPage = page < 0 ? 0 : page;
                int requestedPages = maxpage > 0 ? maxpage : 1;

                foreach (var cat in Cats)
                {
                    // сначала определим максимум страниц для категории (по page=0)
                    string html0 = await GetMegapeerBrowsePage($"{AppInit.conf.Megapeer.rqHost()}/browse.php?cat={cat}&page=0", cat, 0, errors);
                    if (string.IsNullOrWhiteSpace(html0))
                    {
                        if (!perCat.ContainsKey(cat)) perCat[cat] = (0, 0);
                        continue;
                    }

                    int maxPages = GetMaxPagesFromBrowseHtml(html0);
                    if (startPage >= maxPages)
                    {
                        if (!perCat.ContainsKey(cat)) perCat[cat] = (0, 0);
                        continue;
                    }

                    int endExclusive = Math.Min(startPage + requestedPages, maxPages);

                    // если стартуем с 0 — можем переиспользовать html0, чтобы не дергать страницу 0 второй раз
                    for (int p = startPage; p < endExclusive; p++)
                    {
                        (bool ok, int found, int processed, int added, int updated) r;

                        if (p == 0)
                            r = await parsePage(cat, p, html0, errors);
                        else
                            r = await parsePage(cat, p, errors);

                        totalFound += r.found;
                        totalProcessed += r.processed;
                        totalAdded += r.added;
                        totalUpdated += r.updated;

                        if (!perCat.ContainsKey(cat)) perCat[cat] = (0, 0);
                        perCat[cat] = (perCat[cat].found + r.found, perCat[cat].processed + r.processed);
                    }
                }

                var resp = BuildLog(totalFound, totalProcessed, totalAdded, totalUpdated, perCat, Cats, errors);

                // ровно 1 строка лога
                ParserLog.Write("megapeer", $"ok; found={totalFound}; processed={totalProcessed}; added={totalAdded}; updated={totalUpdated}; errors={errors.Count}");

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

        #region parsePage
        async Task<(bool ok, int found, int processed, int added, int updated)> parsePage(string cat, int page, List<string> errors = null)
        {
            string html = await GetMegapeerBrowsePage($"{AppInit.conf.Megapeer.rqHost()}/browse.php?cat={cat}&page={page}", cat, page, errors);
            return await parsePage(cat, page, html, errors);
        }

        async Task<(bool ok, int found, int processed, int added, int updated)> parsePage(string cat, int page, string html, List<string> errors = null)
        {
            if (string.IsNullOrWhiteSpace(html) || !html.Contains(BrowsePageValidMarker))
                return (false, 0, 0, 0, 0);

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

                // sizeName (правый td)
                string sizeName = Match(@"<td[^>]*align=""right""[^>]*>\s*([^<]+)", 1);

                // sid/pir в <font>
                string _sid = Match(@"alt=""S""[^>]*>\s*<font[^>]*>\s*([0-9]+)\s*</font>", 1);
                string _pir = Match(@"alt=""L""[^>]*>\s*<font[^>]*>\s*([0-9]+)\s*</font>", 1);

                int.TryParse(_sid, out int sid);
                int.TryParse(_pir, out int pir);

                // relased (год в скобках)
                int relased = 0;
                var my = Regex.Match(title, @"\(([1-2][0-9]{3})\)");
                if (my.Success)
                    int.TryParse(my.Groups[1].Value, out relased);

                // name — оставляем основу до скобок/слеша/пайпа
                string name = Regex.Split(title, @"(\[|/|\(|\|)", RegexOptions.IgnoreCase)[0].Trim();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

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
                    title = title, // оставляем оригинальный заголовок
                    sid = sid,
                    pir = pir,
                    sizeName = sizeName,
                    createTime = createTime,
                    name = name,
                    originalname = null, // заполним с карточки
                    relased = relased,
                    downloadId = downloadId
                });
            }

            int found = torrents.Count;
            int processed = 0;
            int added = 0;
            int updated = 0;

            if (found == 0)
                return (true, 0, 0, 0, 0);

            // Сначала соберём magnet + originalname ДО FileDB.AddOrUpdate,
            // иначе FileDB сгруппирует торренты по ключу (name/originalname) до выполнения predicate,
            // и при изменении originalname внутри predicate данные уедут в "не тот" бакет.
            var ready = new List<MegapeerDetails>(torrents.Count);

            foreach (var t in torrents)
            {
                var info = await GetDetailsInfoWithRetry(t.url, attempts: 3, errors: errors);
                string magnet = info.magnet;
                string orig = info.originalname;

                // fallback: /download -> magnet
                if (string.IsNullOrWhiteSpace(magnet) && !string.IsNullOrWhiteSpace(t.downloadId))
                {
                    try
                    {
                        byte[] _t = await HttpClient.Download(
                            $"{AppInit.conf.Megapeer.host}/download/{t.downloadId}",
                            cookie: GetMegapeerCookie(),
                            referer: AppInit.conf.Megapeer.host,
                            useproxy: AppInit.conf.Megapeer.useproxy
                        );
                        magnet = BencodeTo.Magnet(_t);
                    }
                    catch { }
                }

                if (!string.IsNullOrWhiteSpace(orig))
                    t.originalname = orig;

                if (!string.IsNullOrWhiteSpace(magnet))
                {
                    t.magnet = magnet;
                    processed++;
                    ready.Add(t);
                }
            }

            if (ready.Count == 0)
                return (true, found, 0, 0, 0);

            // Только по magnet:
            // - если запись уже есть и magnet заполнен -> НЕ трогаем (return false)
            // - если записи нет -> добавляем
            // - если запись есть, но magnet пуст -> обновляем
            await FileDB.AddOrUpdate(ready, (t, db) =>
            {
                try
                {
                    if (db != null && db.TryGetValue(t.url, out TorrentDetails cached))
                    {
                        if (!string.IsNullOrWhiteSpace(cached.magnet))
                            return Task.FromResult(false);

                        Interlocked.Increment(ref updated);
                        return Task.FromResult(true);
                    }

                    Interlocked.Increment(ref added);
                    return Task.FromResult(true);
                }
                catch
                {
                    return Task.FromResult(true);
                }
            });

            return (true, found, processed, added, updated);
        }
        #endregion

        #region Details helpers (magnet + originalname)
        async Task<(string magnet, string originalname)> GetDetailsInfoWithRetry(string detailsUrl, int attempts = 3, List<string> errors = null)
        {
            await Task.Delay(NextDetailsDelayMs());

            var headers = new List<(string name, string val)>()
            {
                ("dnt", "1"),
                ("pragma", "no-cache"),
                ("cache-control", "no-cache"),
                ("upgrade-insecure-requests", "1")
            };

            for (int i = 1; i <= attempts; i++)
            {
                try
                {
                    var (html, response) = await HttpClient.BaseGetAsync(
                        detailsUrl,
                        encoding: Encoding.GetEncoding(1251),
                        cookie: GetMegapeerCookie(),
                        referer: AppInit.conf.Megapeer.host,
                        useproxy: AppInit.conf.Megapeer.useproxy,
                        addHeaders: headers
                    );

                    if (string.IsNullOrWhiteSpace(html))
                        throw new Exception("empty");

                    if (IsCloudflarePage(html))
                    {
                        int backoffMs = i == 1 ? 15000 : 30000;
                        errors?.Add("cloudflare challenge: details");
                        await Task.Delay(backoffMs);
                        continue;
                    }

                    // magnet
                    string magnet = null;
                    var mm = Regex.Match(html, @"href\s*=\s*""(magnet:\?xt=urn:btih:[^""]+)""", RegexOptions.IgnoreCase);
                    if (mm.Success)
                        magnet = HttpUtility.HtmlDecode(mm.Groups[1].Value);

                    // originalname
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

            s = HttpUtility.HtmlDecode(s);
            s = Regex.Replace(s, @"\s+", " ").Trim();

            // Иногда встречаются слова
            if (s.Equals("Сегодня", StringComparison.OrdinalIgnoreCase))
                return DateTime.Today;
            if (s.Equals("Вчера", StringComparison.OrdinalIgnoreCase))
                return DateTime.Today.AddDays(-1);

            // Числовые форматы
            string[] num = { "d.M.yy", "dd.MM.yy", "d.M.yyyy", "dd.MM.yyyy" };
            if (DateTime.TryParseExact(s, num, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dtn))
                return dtn;

            // Формат "10 Фев 26"
            var m = Regex.Match(s, @"^(?<d>\d{1,2})\s+(?<m>[A-Za-zА-Яа-я\.]+)\s+(?<y>\d{2,4})$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                int day = int.Parse(m.Groups["d"].Value);
                string mon = m.Groups["m"].Value.Trim().Trim('.').ToLowerInvariant();
                int year = int.Parse(m.Groups["y"].Value);
                if (year < 100) year += 2000;

                int month = 0;
                if (mon.StartsWith("янв")) month = 1;
                else if (mon.StartsWith("фев")) month = 2;
                else if (mon.StartsWith("мар")) month = 3;
                else if (mon.StartsWith("апр")) month = 4;
                else if (mon.StartsWith("май")) month = 5;
                else if (mon.StartsWith("июн")) month = 6;
                else if (mon.StartsWith("июл")) month = 7;
                else if (mon.StartsWith("авг")) month = 8;
                else if (mon.StartsWith("сен")) month = 9;
                else if (mon.StartsWith("сент")) month = 9;
                else if (mon.StartsWith("окт")) month = 10;
                else if (mon.StartsWith("ноя")) month = 11;
                else if (mon.StartsWith("дек")) month = 12;

                if (month > 0)
                {
                    try { return new DateTime(year, month, day); }
                    catch { }
                }
            }

            // Последний шанс
            var ru = new CultureInfo("ru-RU");
            if (DateTime.TryParse(s, ru, DateTimeStyles.None, out var dt))
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

        static string BuildLog(
            int totalFound,
            int totalProcessed,
            int totalAdded,
            int totalUpdated,
            Dictionary<string, (int found, int processed)> perCat,
            List<string> order,
            List<string> errors = null)
        {
            var sb = new StringBuilder();
            sb.Append("ok; found=").Append(totalFound)
              .Append("; processed=").Append(totalProcessed)
              .Append("; added=").Append(totalAdded)
              .Append("; updated=").Append(totalUpdated)
              .AppendLine();

            sb.AppendLine("by_cat:");
            foreach (var cat in order)
            {
                perCat.TryGetValue(cat, out var v);
                sb.Append("- ").Append(CatTitle(cat)).Append(" (").Append(cat).Append("): ")
                  .Append(v.found).Append("/").Append(v.processed).AppendLine();
            }

            if (errors != null && errors.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("errors:");
                foreach (var e in errors.Distinct().Take(30))
                    sb.Append("- ").Append(e).AppendLine();
            }

            return sb.ToString().TrimEnd();
        }
        #endregion
    }
}