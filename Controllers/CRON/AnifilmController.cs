using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using JacRed.Engine;
using JacRed.Engine.CORE;
using JacRed.Models.Details;
using Microsoft.AspNetCore.Mvc;

namespace JacRed.Controllers.CRON
{
    [Route("/cron/anifilm/[action]")]
    public class AnifilmController : BaseController
    {
        #region Cookie (берём из AppInit.conf.Anifilm.cookie)
        static string GetAnifilmCookie()
        {
            try
            {
                var c = AppInit.conf?.Anifilm?.cookie;
                if (string.IsNullOrWhiteSpace(c))
                    return null;

                // допускаем что в конфиге может лежать "cookie: ..." или просто "a=b; c=d"
                c = c.Trim();
                if (c.StartsWith("cookie:", StringComparison.OrdinalIgnoreCase))
                    c = c.Substring("cookie:".Length).Trim();

                return c.Trim().TrimEnd(';');
            }
            catch
            {
                return null;
            }
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

            // слабый признак
            if (h.Contains("cloudflare") && h.Contains("ray id")) return true;

            return false;
        }

        async Task<string> GetAnifilmHtmlWithRetry(string url, string referer = null, int attempts = 3)
        {
            string cookie = GetAnifilmCookie();

            for (int i = 1; i <= attempts; i++)
            {
                string html = await HttpClient.Get(
                    url,
                    cookie: cookie,
                    referer: referer,
                    useproxy: AppInit.conf.Anifilm.useproxy
                );

                if (string.IsNullOrWhiteSpace(html))
                    continue;

                if (IsCloudflarePage(html))
                {
                    // HttpClient не проходит JS challenge — делаем backoff и повторяем
                    await Task.Delay(i == 1 ? 15000 : 30000);
                    continue;
                }

                return html;
            }

            return null;
        }
        #endregion

        #region Parse
        static bool workParse = false;

        async public Task<string> Parse(bool fullparse)
        {
            if (AppInit.conf?.disable_trackers != null && AppInit.conf.disable_trackers.Contains("anifilm", StringComparer.OrdinalIgnoreCase))
                return "disabled";
            if (workParse)
                return "work";

            workParse = true;

            string log = "";

            try
            {
                if (fullparse)
                {
                    for (int page = 1; page <= 70; page++)
                    {
                        await Task.Delay(AppInit.conf.Anifilm.parseDelay);
                        await parsePage("serials", page, DateTime.Today.AddDays(-(2 * page)));
                    }

                    for (int page = 1; page <= 32; page++)
                    {
                        await Task.Delay(AppInit.conf.Anifilm.parseDelay);
                        await parsePage("ova", page, DateTime.Today.AddDays(-(2 * page)));
                    }

                    for (int page = 1; page <= 2; page++)
                    {
                        await Task.Delay(AppInit.conf.Anifilm.parseDelay);
                        await parsePage("ona", page, DateTime.Today.AddDays(-(2 * page)));
                    }

                    for (int page = 1; page <= 17; page++)
                    {
                        await Task.Delay(AppInit.conf.Anifilm.parseDelay);
                        await parsePage("movies", page, DateTime.Today.AddDays(-(2 * page)));
                    }
                }
                else
                {
                    foreach (string cat in new List<string>() { "serials", "ova", "ona", "movies" })
                    {
                        await parsePage(cat, 1, DateTime.UtcNow);
                        log += $"{cat} - 1\n";
                    }
                }
            }
            catch { }
            finally
            {
                workParse = false;
            }

            return string.IsNullOrWhiteSpace(log) ? "ok" : log;
        }
        #endregion


        #region parsePage
        async Task<bool> parsePage(string cat, int page, DateTime createTime)
        {
            string html = await GetAnifilmHtmlWithRetry($"{AppInit.conf.Anifilm.rqHost()}/releases/page/{page}?category={cat}", referer: AppInit.conf.Anifilm.host);
            if (html == null || !html.Contains("id=\"ui-components\""))
                return false;

            var torrents = new List<TorrentBaseDetails>();

            foreach (string row in tParse.ReplaceBadNames(html).Split("class=\"releases__item\"").Skip(1))
            {
                #region Локальный метод - Match
                string Match(string pattern, int index = 1)
                {
                    string res = HttpUtility.HtmlDecode(new Regex(pattern, RegexOptions.IgnoreCase).Match(row).Groups[index].Value.Trim());
                    res = Regex.Replace(res, "[\n\r\t ]+", " ");
                    return res.Trim();
                }
                #endregion

                if (string.IsNullOrWhiteSpace(row))
                    continue;

                #region Данные раздачи
                string url = Match("<a href=\"/(releases/[^\"]+)\"");
                string name = Match("<a class=\"releases__title-russian\" [^>]+>([^<]+)</a>");
                string originalname = Match("<span class=\"releases__title-original\">([^<]+)</span>");
                string episodes = Match("([0-9]+(-[0-9]+)?) из [0-9]+ эп.,");

                if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(originalname))
                    continue;

                if (cat != "movies" && string.IsNullOrWhiteSpace(episodes))
                    continue;

                url = $"{AppInit.conf.Anifilm.host}/{url}";
                string title = $"{name} / {originalname}";

                if (!string.IsNullOrWhiteSpace(episodes))
                    title += $" ({episodes})";

                name = name.Split("(")[0].Trim();
                #endregion

                // Год выхода
                if (!int.TryParse(Match("<a href=\"/releases/releases/[^\"]+\">([0-9]{4})</a> г\\."), out int relased) || relased == 0)
                    continue;

                torrents.Add(new TorrentDetails()
                {
                    trackerName = "anifilm",
                    types = new string[] { "anime" },
                    url = url,
                    title = title,
                    sid = 1,
                    createTime = createTime,
                    name = name,
                    originalname = originalname,
                    relased = relased
                });
            }

            await FileDB.AddOrUpdate(torrents, async (t, db) =>
            {
                if (db.TryGetValue(t.url, out TorrentDetails _tcache) && _tcache.title.Replace(" [1080p]", "") == t.title)
                    return true;

                var fullNews = await GetAnifilmHtmlWithRetry(t.url, referer: AppInit.conf.Anifilm.host);
                if (fullNews != null)
                {
                    string tid = null;
                    string title = t.title;
                    string[] releasetorrents = fullNews.Split("<li class=\"release__torrents-item\">");

                    string _rnews = releasetorrents.FirstOrDefault(i => i.Contains("href=\"/releases/download-torrent/") && i.Contains(" 1080p "));
                    if (!string.IsNullOrWhiteSpace(_rnews))
                    {
                        tid = Regex.Match(_rnews, "href=\"/(releases/download-torrent/[0-9]+)\">скачать</a>").Groups[1].Value;
                        if (!string.IsNullOrWhiteSpace(tid) && !title.Contains(" [1080p]"))
                            title += " [1080p]";
                    }

                    if (string.IsNullOrWhiteSpace(tid))
                        tid = Regex.Match(fullNews, "href=\"/(releases/download-torrent/[0-9]+)\">скачать</a>").Groups[1].Value;

                    if (!string.IsNullOrWhiteSpace(tid))
                    {
                        byte[] torrent = await HttpClient.Download(
                            $"{AppInit.conf.Anifilm.host}/{tid}",
                            cookie: GetAnifilmCookie(),
                            referer: t.url,
                            useproxy: AppInit.conf.Anifilm.useproxy
                        );
                        string magnet = BencodeTo.Magnet(torrent);

                        if (!string.IsNullOrWhiteSpace(magnet))
                        {
                            t.title = title;
                            t.magnet = magnet;
                            t.sizeName = BencodeTo.SizeName(torrent);
                            return true;
                        }
                    }
                }

                return false;
            });

            return torrents.Count > 0;
        }
        #endregion
    }
}
