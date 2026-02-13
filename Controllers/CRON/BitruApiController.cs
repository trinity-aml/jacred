using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
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
    /// <summary>
    /// Парсинг Bitru через официальный API (api.php?get=torrents).
    /// Ограничение bitru: макс. 5 запросов в сек на IP (включая редиректы при скачивании .torrent),
    /// поэтому запросы (API + Download) делаем строго последовательно и с задержкой.
    /// </summary>
    [Route("/cron/bitruapi/[action]")]
    public class BitruApiController : BaseController
    {
        const string ApiGetTorrents = "torrents";

        /// <summary>
        /// Безопасная задержка между "операциями" к bitru (API POST и Download).
        /// (делаем заметно меньше 5 req/sec, т.к. Download может делать 1+ редиректов)
        /// </summary>
        const int ApiDelayMs = 650;

        const int DumpMaxBatches = 2;
        const int HardMaxBatches = 5000;

        static readonly string ApiUrl;
        static readonly string HostUrl;
        static readonly string LastNewTorPath = "Data/temp/bitruapi_lastnewtor.txt";

        static BitruApiController()
        {
            var host = AppInit.conf.Bitru?.host?.TrimEnd('/') ?? "https://bitru.org";
            ApiUrl = $"{host}/api.php";
            HostUrl = host;
        }

        #region throttle (global for controller)

        static readonly SemaphoreSlim _throttleLock = new SemaphoreSlim(1, 1);
        static long _lastThrottleTick = 0;
        static readonly Random _rnd = new Random();

        static async Task ThrottleBitruAsync()
        {
            await _throttleLock.WaitAsync();
            try
            {
                long now = Environment.TickCount64;
                long last = Interlocked.Read(ref _lastThrottleTick);
                long wait = ApiDelayMs - (now - last);
                if (wait > 0)
                {
                    int jitter = _rnd.Next(0, 200);
                    await Task.Delay((int)wait + jitter);
                }
                Interlocked.Exchange(ref _lastThrottleTick, Environment.TickCount64);
            }
            finally
            {
                _throttleLock.Release();
            }
        }

        #endregion

        #region Parse

        static bool _workParse = false;
        static int _apiCalls = 0;

        /// <summary>
        /// /cron/bitruapi/parse?limit=100 - один запрос (последние торренты)
        /// /cron/bitruapi/parse?fullparse - полный проход (пагинация "страницами")
        /// /cron/bitruapi/parse?batches=10 - ограниченный fullparse (10 батчей)
        /// dump=1 - сохранять request/response в Data/temp (первые 1-2 батча)
        /// </summary>
        async public Task<string> Parse(int limit = 100, int batches = 0, string fullparse = null, int dump = 0)
        {
            if (_workParse)
                return "work";

            _workParse = true;
            string mode = "";
            string stopReason = "";
            long cursor = 0;
            int magnetOk = 0;
            int batchesDone = 0;
            int apiCallsSnapshot;

            try
            {
                _apiCalls = 0;
                limit = Math.Min(100, Math.Max(1, limit));

                bool isFull = Request.Query.ContainsKey("fullparse") || !string.IsNullOrEmpty(fullparse) || batches > 1;
                int maxBatches = batches > 0 ? batches : 0;

                // safety
                if (maxBatches > HardMaxBatches)
                    maxBatches = HardMaxBatches;

                var sw = Stopwatch.StartNew();
                mode = isFull ? "fullroot" : "latest";
                ParserLog.Write("bitruapi", $"Parse start, mode={mode}, limit={limit}, batches={(maxBatches > 0 ? maxBatches.ToString() : "all")}, api={ApiUrl}");

                if (!isFull)
                {
                    var (items, resp) = await FetchLatestTorrentsFromApiOnce(limit, dump: dump == 1);
                    if (resp == null)
                    {
                        stopReason = "http_error";
                    }
                    else if (resp.Error)
                    {
                        stopReason = "api_error";
                    }
                    else
                    {
                        batchesDone = 1;
                        if (items.Count == 0)
                            stopReason = "items0";
                        else
                            magnetOk = await SaveTorrentsAndMagnets(items);

                        // lastnewtor from newest item
                        TryWriteLastNewTor(items);
                    }
                }
                else
                {
                    // full parse using "after_date = before_date previous page" (as requested)
                    (magnetOk, batchesDone, cursor, stopReason) = await FullParseAfterDate(limit, maxBatches, dump == 1);
                }

                apiCallsSnapshot = _apiCalls;
                ParserLog.Write("bitruapi", $"Parse completed in {sw.Elapsed.TotalSeconds:F1}s, saved={magnetOk}, batches={batchesDone}, api_calls={apiCallsSnapshot}, stop={stopReason}, cursor={cursor}");
                return $"saved {magnetOk}; batches={batchesDone}; api_calls={apiCallsSnapshot}; cursor={cursor}; stop={stopReason}; mode={mode}";
            }
            catch (Exception ex)
            {
                apiCallsSnapshot = _apiCalls;
                ParserLog.Write("bitruapi", $"Error: {ex.Message}");
                return $"saved {magnetOk}; batches={batchesDone}; api_calls={apiCallsSnapshot}; cursor={cursor}; stop=error; mode={mode}";
            }
            finally
            {
                _workParse = false;
            }
        }

        #endregion

        #region ParseFromDate (после даты)

        /// <summary>
        /// Загрузить торренты после указанной даты (dd.MM.yyyy).
        /// Пример: /cron/bitruapi/ParseFromDate?lastnewtor=23.10.2025&limit=100
        /// </summary>
        async public Task<string> ParseFromDate(string lastnewtor, int limit = 100)
        {
            if (string.IsNullOrWhiteSpace(lastnewtor))
                return "bad lastnewtor (use dd.MM.yyyy)";

            if (!DateTime.TryParseExact(lastnewtor.Trim(), "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime fromDate))
                return "bad date format (use dd.MM.yyyy)";

            if (_workParse)
                return "work";

            _workParse = true;

            try
            {
                _apiCalls = 0;
                limit = Math.Min(100, Math.Max(1, limit));

                long unixFrom = new DateTimeOffset(fromDate.Year, fromDate.Month, fromDate.Day, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

                var sw = Stopwatch.StartNew();
                ParserLog.Write("bitruapi", $"ParseFromDate lastnewtor={lastnewtor} (unix={unixFrom}), limit={limit}");

                var (items, resp) = await FetchTorrentsAfterDateOnce(limit, unixFrom);
                string stopReason = "ok";
                int magnetOk = 0;

                if (resp == null)
                    stopReason = "http_error";
                else if (resp.Error)
                    stopReason = "api_error";
                else if (items.Count == 0)
                    stopReason = "items0";
                else
                {
                    magnetOk = await SaveTorrentsAndMagnets(items);
                    TryWriteLastNewTor(items);
                }

                ParserLog.Write("bitruapi", $"ParseFromDate completed in {sw.Elapsed.TotalSeconds:F1}s, saved={magnetOk}, api_calls={_apiCalls}, stop={stopReason}");
                return $"saved {magnetOk}; batches=1; api_calls={_apiCalls}; cursor=0; stop={stopReason}; mode=fromdate";
            }
            catch (Exception ex)
            {
                ParserLog.Write("bitruapi", $"Error: {ex.Message}");
                return $"saved 0; batches=0; api_calls={_apiCalls}; cursor=0; stop=error; mode=fromdate";
            }
            finally
            {
                _workParse = false;
            }
        }

        #endregion

        #region API request helpers + dump

        async Task<BitruApiResponse> ApiRequestAsync(object jsonParams, bool dump = false, string dumpTag = null)
        {
            string json = JsonConvert.SerializeObject(jsonParams);
            string postData = $"get={ApiGetTorrents}&json={Uri.EscapeDataString(json)}";

            if (dump)
            {
                try
                {
                    IO.Directory.CreateDirectory("Data/temp");
                    IO.File.WriteAllText($"Data/temp/bitruapi_last_request{(string.IsNullOrEmpty(dumpTag) ? "" : "_" + dumpTag)}.json", json);
                }
                catch { }
            }

            await ThrottleBitruAsync();
            Interlocked.Increment(ref _apiCalls);

            string response = await HttpClient.Post(ApiUrl, postData, timeoutSeconds: 20, useproxy: AppInit.conf.Bitru.useproxy);
            if (string.IsNullOrWhiteSpace(response))
            {
                if (dump)
                {
                    try
                    {
                        IO.Directory.CreateDirectory("Data/temp");
                        IO.File.WriteAllText($"Data/temp/bitruapi_last_api{(string.IsNullOrEmpty(dumpTag) ? "" : "_" + dumpTag)}.json", "");
                    }
                    catch { }
                }
                return null;
            }

            if (dump)
            {
                try
                {
                    IO.Directory.CreateDirectory("Data/temp");
                    IO.File.WriteAllText($"Data/temp/bitruapi_last_api{(string.IsNullOrEmpty(dumpTag) ? "" : "_" + dumpTag)}.json", response);
                }
                catch { }
            }

            return JsonConvert.DeserializeObject<BitruApiResponse>(response);
        }

        static long ParseUnix(object v)
        {
            if (v == null) return 0;
            if (v is long l) return l;
            if (v is int i) return i;
            if (v is string s)
            {
                s = s.Trim().Trim('"');
                return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : 0;
            }
            try
            {
                return Convert.ToInt64(v, CultureInfo.InvariantCulture);
            }
            catch { return 0; }
        }

        async Task<(List<TorrentDetails> items, BitruApiResponse resp)> FetchLatestTorrentsFromApiOnce(int limit, bool dump)
        {
            var p = new Dictionary<string, object>
            {
                { "limit", limit },
                { "category", new[] { "movie", "serial" } }
            };
            var resp = await ApiRequestAsync(p, dump: dump, dumpTag: "1");
            var items = MapItems(resp);
            return (items, resp);
        }

        async Task<(List<TorrentDetails> items, BitruApiResponse resp)> FetchTorrentsAfterDateOnce(int limit, long afterUnix)
        {
            var p = new Dictionary<string, object>
            {
                { "limit", limit },
                { "category", new[] { "movie", "serial" } },
                { "after_date", afterUnix.ToString() }
            };
            var resp = await ApiRequestAsync(p, dump: false);
            var items = MapItems(resp);
            return (items, resp);
        }

        List<TorrentDetails> MapItems(BitruApiResponse resp)
        {
            var list = new List<TorrentDetails>();
            if (resp?.Result?.Items == null)
                return list;

            foreach (var wrap in resp.Result.Items)
            {
                if (wrap?.Item == null)
                    continue;

                var t = MapToTorrentDetails(wrap.Item);
                if (t != null)
                    list.Add(t);
            }

            return list;
        }

        #endregion

        #region FullParse (after_date paging)

        async Task<(int magnetOk, int batchesDone, long cursor, string stopReason)> FullParseAfterDate(int limit, int maxBatches, bool dump)
        {
            int magnetOkTotal = 0;
            int batchesDone = 0;
            long cursor = 0;
            string stopReason = "ok";

            // cursor for after_date (string)
            string afterDate = null;

            // anti-duplicates across batches
            var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            DateTime? newest = null;

            for (int step = 0; step < HardMaxBatches; step++)
            {
                if (maxBatches > 0 && batchesDone >= maxBatches)
                {
                    stopReason = "max_batches";
                    break;
                }

                var p = new Dictionary<string, object>
                {
                    { "limit", limit },
                    { "category", new[] { "movie", "serial" } }
                };

                // 2+ page: after_date = before_date previous page
                if (!string.IsNullOrEmpty(afterDate))
                    p["after_date"] = afterDate;

                bool dumpThis = dump && batchesDone < DumpMaxBatches;
                var resp = await ApiRequestAsync(p, dump: dumpThis, dumpTag: (batchesDone + 1).ToString());
                if (resp == null)
                {
                    stopReason = "http_error";
                    break;
                }

                if (resp.Error)
                {
                    stopReason = "api_error";
                    break;
                }

                if (resp.Result?.Items == null)
                {
                    stopReason = "api_bad";
                    break;
                }

                var batchAll = MapItems(resp);

                // compute next cursor
                long respBefore = ParseUnix(resp.Result.BeforeDate);
                if (respBefore == 0)
                {
                    // fallback: min added in items
                    respBefore = batchAll.Count > 0 ? (long)batchAll.Min(x => new DateTimeOffset(x.createTime).ToUnixTimeSeconds()) : 0;
                }

                // dedup across batches by url
                var batch = new List<TorrentDetails>();
                foreach (var t in batchAll)
                {
                    if (t == null || string.IsNullOrEmpty(t.url))
                        continue;
                    if (seenUrls.Add(t.url))
                        batch.Add(t);
                }

                batchesDone++;

                if (newest == null && batchAll.Count > 0)
                    newest = batchAll.OrderByDescending(x => x.createTime).FirstOrDefault()?.createTime;

                if (batch.Count == 0 && resp.Result.Items.Count == 0)
                {
                    stopReason = "items0";
                    cursor = respBefore;
                    break;
                }

                // save this batch
                if (batch.Count > 0)
                    magnetOkTotal += await SaveTorrentsAndMagnets(batch);

                // next page cursor
                if (respBefore == 0)
                {
                    stopReason = "no_cursor";
                    cursor = 0;
                    break;
                }

                if (cursor != 0 && respBefore >= cursor)
                {
                    // no progress (same/greater cursor)
                    stopReason = "cursor_repeat";
                    cursor = respBefore;
                    break;
                }

                cursor = respBefore;
                afterDate = respBefore.ToString();
            }

            // write lastnewtor from newest
            if (newest.HasValue)
            {
                try
                {
                    IO.File.WriteAllText(LastNewTorPath, newest.Value.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture));
                }
                catch { }
            }

            return (magnetOkTotal, batchesDone, cursor, stopReason);
        }

        #endregion

        #region Map (API item -> TorrentDetails)

        TorrentDetails MapToTorrentDetails(BitruApiItemInner item)
        {
            var torrent = item.Torrent;
            var info = item.Info;
            var template = item.Template;

            if (torrent == null || info == null || template == null)
                return null;

            string category = (template.Category ?? "").ToLowerInvariant();
            string[] types = null;
            if (category == "movie")
                types = new[] { "movie" };
            else if (category == "serial")
                types = new[] { "serial" };
            else
                return null;

            string name = info.Name ?? "";
            string originalname = template.OrigName;
            string yearDisplay = BitruYearToDisplayString(info.Year);
            int relased = BitruYearToReleased(info.Year);

            string titlePart = name;
            if (!string.IsNullOrWhiteSpace(originalname))
                titlePart += " / " + originalname;
            if (!string.IsNullOrEmpty(yearDisplay))
                titlePart += " (" + yearDisplay + ")";
            if (template.Video?.Quality != null)
                titlePart += " " + template.Video.Quality;
            if (!string.IsNullOrWhiteSpace(template.Other))
                titlePart += " | " + template.Other;

            string url = $"{HostUrl}/details.php?id={torrent.Id}";
            string sizeName = FormatSize(torrent.Size);
            long addedUnix = ParseUnix(torrent.Added);
            DateTime createTime = addedUnix > 0
                ? DateTimeOffset.FromUnixTimeSeconds(addedUnix).UtcDateTime
                : DateTime.UtcNow;

            return new TorrentDetails
            {
                trackerName = "bitru",
                types = types,
                url = url,
                title = HttpUtility.HtmlDecode(titlePart.Trim()),
                sid = torrent.Seeders,
                pir = torrent.Leechers,
                sizeName = sizeName,
                createTime = createTime,
                name = name.Trim(),
                originalname = originalname?.Trim(),
                relased = relased,
                _sn = torrent.File
            };
        }

        static string BitruYearToDisplayString(object year)
        {
            if (year == null) return "";
            if (year is long l) return l.ToString();
            if (year is int i) return i.ToString();
            return year.ToString()?.Trim() ?? "";
        }

        static int BitruYearToReleased(object year)
        {
            if (year == null) return 0;
            if (year is long l) return (int)l;
            if (year is int i) return i;
            var s = year.ToString()?.Trim();
            if (string.IsNullOrEmpty(s)) return 0;
            var dash = s.IndexOf('-');
            var firstPart = dash > 0 ? s.Substring(0, dash).Trim() : s;
            return int.TryParse(firstPart, NumberStyles.None, CultureInfo.InvariantCulture, out int y) ? y : 0;
        }

        static string FormatSize(long bytes)
        {
            if (bytes < 1000L * 1024)
                return $"{bytes / 1024.0:F2} КБ";
            if (bytes < 1000L * 1048576)
                return $"{bytes / 1048576.0:F2} МБ";
            if (bytes < 1000L * 1073741824)
                return $"{bytes / 1073741824.0:F2} ГБ";
            return $"{bytes / 1099511627776.0:F2} ТБ";
        }

        #endregion

        #region Save to FileDB + magnets (saved = magnet_ok)

        async Task<int> SaveTorrentsAndMagnets(List<TorrentDetails> torrents)
        {
            int magnetOk = 0;

            // local dedup by url to avoid double work inside a batch
            var uniq = torrents
                .Where(t => t != null && !string.IsNullOrWhiteSpace(t.url))
                .GroupBy(t => t.url, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            await FileDB.AddOrUpdate(uniq, async (t, db) =>
            {
                if (db != null && db.TryGetValue(t.url, out TorrentDetails cache))
                {
                    // already have magnet - count as magnet_ok
                    if (!string.IsNullOrWhiteSpace(cache.magnet))
                    {
                        t.magnet = cache.magnet;
                        t._sn = null;
                        Interlocked.Increment(ref magnetOk);
                        return true;
                    }

                    // if unchanged and no magnet - skip downloading
                    if (cache.title == t.title && string.IsNullOrWhiteSpace(cache.magnet))
                        return true;
                }

                string downloadUrl = t._sn;
                if (string.IsNullOrWhiteSpace(downloadUrl) || !downloadUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    var idMatch = System.Text.RegularExpressions.Regex.Match(t.url ?? "", @"\?id=(\d+)");
                    downloadUrl = idMatch.Success ? $"{HostUrl}/download.php?id={idMatch.Groups[1].Value}" : null;
                }
                if (string.IsNullOrWhiteSpace(downloadUrl))
                    return false;

                await ThrottleBitruAsync();

                byte[] data = await HttpClient.Download(downloadUrl, referer: HostUrl + "/", timeoutSeconds: 20, useproxy: AppInit.conf.Bitru.useproxy);
                string magnet = data != null ? BencodeTo.Magnet(data) : null;
                if (!string.IsNullOrWhiteSpace(magnet))
                {
                    t.magnet = magnet;
                    t._sn = null;
                    Interlocked.Increment(ref magnetOk);
                    return true;
                }

                return false;
            });

            return magnetOk;
        }

        void TryWriteLastNewTor(List<TorrentDetails> torrents)
        {
            try
            {
                var lastTor = torrents.OrderByDescending(x => x.createTime).FirstOrDefault();
                if (lastTor != null)
                    IO.File.WriteAllText(LastNewTorPath, lastTor.createTime.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture));
            }
            catch { }
        }

        #endregion
    }
}