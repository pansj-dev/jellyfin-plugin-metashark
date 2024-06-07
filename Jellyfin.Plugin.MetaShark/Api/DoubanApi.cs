using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Extensions.Json;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.MetaShark.Model;
using System.Threading;
using System.Net.Http.Json;
using System.Net;
using Jellyfin.Plugin.MetaShark.Api.Http;
using System.Web;
using Microsoft.Extensions.Caching.Memory;
using AngleSharp;
using AngleSharp.Dom;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.MetaShark.Core;
using RateLimiter;
using ComposableAsync;

namespace Jellyfin.Plugin.MetaShark.Api
{
    public class DoubanApi : IDisposable
    {
        public const string HTTP_USER_AGENT = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/93.0.4577.63 Safari/537.36 Edg/93.0.961.44";
        public const string HTTP_REFERER = "https://www.douban.com/";
        private readonly ILogger<DoubanApi> _logger;
        private HttpClient httpClient;
        private CookieContainer _cookieContainer;
        private readonly JsonSerializerOptions _jsonOptions = JsonDefaults.Options;
        private readonly IMemoryCache _memoryCache;
        private static readonly object _lock = new object();
        private DateTime lastRequestTime = DateTime.Now.AddDays(-1);

        Regex regId = new Regex(@"/(\d+?)/", RegexOptions.Compiled);
        Regex regSid = new Regex(@"sid: (\d+?),", RegexOptions.Compiled);
        Regex regCat = new Regex(@"\[(.+?)\]", RegexOptions.Compiled);
        Regex regYear = new Regex(@"([12][890][0-9][0-9])", RegexOptions.Compiled);
        Regex regTitle = new Regex(@"<title>([\w\W]+?)</title>", RegexOptions.Compiled);
        Regex regKeywordMeta = new Regex(@"<meta name=""keywords"" content=""(.+?)""", RegexOptions.Compiled);
        Regex regOriginalName = new Regex(@"原名[:：](.+?)\s*?\/", RegexOptions.Compiled);
        Regex regDirector = new Regex(@"导演: (.+?)\n", RegexOptions.Compiled);
        Regex regWriter = new Regex(@"编剧: (.+?)\n", RegexOptions.Compiled);
        Regex regActor = new Regex(@"主演: (.+?)\n", RegexOptions.Compiled);
        Regex regGenre = new Regex(@"类型: (.+?)\n", RegexOptions.Compiled);
        Regex regCountry = new Regex(@"制片国家/地区: (.+?)\n", RegexOptions.Compiled);
        Regex regLanguage = new Regex(@"语言: (.+?)\n", RegexOptions.Compiled);
        Regex regDuration = new Regex(@"片长: (.+?)\n", RegexOptions.Compiled);
        Regex regScreen = new Regex(@"(上映日期|首播): (.+?)\n", RegexOptions.Compiled);
        Regex regSubname = new Regex(@"又名: (.+?)\n", RegexOptions.Compiled);
        Regex regImdb = new Regex(@"IMDb: (tt\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        Regex regSite = new Regex(@"官方网站: (.+?)\n", RegexOptions.Compiled);
        Regex regRole = new Regex(@"\([饰|配]?\s*?(.+?)\)", RegexOptions.Compiled);
        Regex regBackgroundImage = new Regex(@"url\(([^)]+?)\)$", RegexOptions.Compiled);
        Regex regLifedate = new Regex(@"(.+?) 至 (.+)", RegexOptions.Compiled);
        Regex regHtmlTag = new Regex(@"<.?>", RegexOptions.Compiled);
        Regex regImgHost = new Regex(@"\/\/(img\d+?)\.", RegexOptions.Compiled);
        // 匹配除了换行符之外所有空白
        Regex regOverviewSpace = new Regex(@"\n[^\S\n]+", RegexOptions.Compiled);
        Regex regPhotoId = new Regex(@"/photo/(\d+?)/", RegexOptions.Compiled);
        Regex regLoginName = new Regex(@"<div[^>]*?db-usr-profile[^>]*?>[\w\W]*?<h1>([^>]*?)<", RegexOptions.Compiled);

        // 默认200毫秒请求1次
        private TimeLimiter _defaultTimeConstraint = TimeLimiter.GetFromMaxCountByInterval(1, TimeSpan.FromMilliseconds(200));
        // 未登录最多1分钟10次请求，不然5分钟后会被封ip
        private TimeLimiter _guestTimeConstraint = TimeLimiter.Compose(new CountByIntervalAwaitableConstraint(10, TimeSpan.FromMinutes(1)), new CountByIntervalAwaitableConstraint(1, TimeSpan.FromMilliseconds(5000)));
        // 登录后最多1分钟20次请求，不然会触发机器人检验
        private TimeLimiter _loginedTimeConstraint = TimeLimiter.Compose(new CountByIntervalAwaitableConstraint(20, TimeSpan.FromMinutes(1)), new CountByIntervalAwaitableConstraint(1, TimeSpan.FromMilliseconds(3000)));


        /// <summary>
        /// Initializes a new instance of the <see cref="DoubanApi"/> class.
        /// </summary>
        /// <param name="loggerFactory">The <see cref="ILoggerFactory"/>.</param>
        public DoubanApi(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<DoubanApi>();
            _memoryCache = new MemoryCache(new MemoryCacheOptions());

            var handler = new HttpClientHandlerEx();
            this._cookieContainer = handler.CookieContainer;
            httpClient = new HttpClient(handler);
            httpClient.Timeout = TimeSpan.FromSeconds(20);
            httpClient.DefaultRequestHeaders.Add("User-Agent", HTTP_USER_AGENT);
            httpClient.DefaultRequestHeaders.Add("Origin", "https://movie.douban.com");
            httpClient.DefaultRequestHeaders.Add("Referer", "https://movie.douban.com/");

            this.LoadLoadDoubanCookie();
            if (Plugin.Instance != null)
            {
                Plugin.Instance.ConfigurationChanged += (_, _) =>
                {
                    this.LoadLoadDoubanCookie();
                };
            }
        }



        private void LoadLoadDoubanCookie()
        {
            var configCookie = Plugin.Instance?.Configuration.DoubanCookies.Trim() ?? string.Empty;

            lock (_lock)
            {
                var uri = new Uri("https://douban.com/");

                // 清空旧的cookie
                var cookies = _cookieContainer.GetCookies(uri);
                foreach (Cookie co in cookies)
                {
                    co.Expires = DateTime.Now.Subtract(TimeSpan.FromDays(1));
                }


                // 附加新的cookie
                if (!string.IsNullOrEmpty(configCookie))
                {
                    var arr = configCookie.Split(';');
                    foreach (var str in arr)
                    {
                        var cookieArr = str.Split('=');
                        if (cookieArr.Length != 2)
                        {
                            continue;
                        }

                        var key = cookieArr[0].Trim();
                        var value = cookieArr[1].Trim();
                        try
                        {
                            _cookieContainer.Add(new Cookie(key, value, "/", ".douban.com"));
                        }
                        catch (Exception ex)
                        {
                            this._logger.LogDebug(ex, ex.Message);
                        }
                    }

                }
            }
        }

        public async Task<List<DoubanSubject>> SearchMovieAsync(string keyword, CancellationToken cancellationToken)
        {
            var result = await this.SearchAsync(keyword, cancellationToken).ConfigureAwait(false);
            return result.Where(x => x.Category == "电影").ToList();
        }

        public async Task<List<DoubanSubject>> SearchTVAsync(string keyword, CancellationToken cancellationToken)
        {
            var result = await this.SearchAsync(keyword, cancellationToken).ConfigureAwait(false);
            return result.Where(x => x.Category == "电视剧").ToList();
        }

        public async Task<List<DoubanSubject>> SearchAsync(string keyword, CancellationToken cancellationToken)
        {
            var list = new List<DoubanSubject>();
            if (string.IsNullOrEmpty(keyword))
            {
                return list;
            }

            var cacheKey = $"search_{keyword}";
            var expiredOption = new MemoryCacheEntryOptions() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) };
            List<DoubanSubject> searchResult;
            if (_memoryCache.TryGetValue<List<DoubanSubject>>(cacheKey, out searchResult))
            {
                return searchResult;
            }

            await LimitRequestFrequently();

            var encodedKeyword = HttpUtility.UrlEncode(keyword);
            var url = $"https://www.douban.com/search?cat=1002&q={encodedKeyword}";
            var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                this._logger.LogWarning("douban搜索请求失败. keyword: {0} statusCode: {1}", keyword, response.StatusCode);
                return list;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var context = BrowsingContext.New();
            var doc = await context.OpenAsync(req => req.Content(body), cancellationToken).ConfigureAwait(false);
            var movieElements = doc.QuerySelectorAll("div.result-list .result");

            foreach (var movieElement in movieElements)
            {

                var ratingStr = movieElement.GetText("div.rating-info") ?? string.Empty;
                if (ratingStr.Contains("尚未播出"))
                {
                    continue;
                }

                var rating = movieElement.GetText("div.rating-info>.rating_nums") ?? "0";
                var img = movieElement.GetAttr("a.nbg>img", "src") ?? string.Empty;
                var oncick = movieElement.GetAttr("div.title a", "onclick") ?? string.Empty;
                var sid = oncick.GetMatchGroup(this.regSid);
                var name = movieElement.GetText("div.title a") ?? string.Empty;
                var titleStr = movieElement.GetText("div.title>h3>span") ?? string.Empty;
                var cat = titleStr.GetMatchGroup(this.regCat);
                var subjectStr = movieElement.GetText("div.rating-info>span:last-child") ?? string.Empty;
                var year = subjectStr.GetMatchGroup(this.regYear);
                var originalName = subjectStr.GetMatchGroup(this.regOriginalName);
                var desc = movieElement.GetText("div.content>p") ?? string.Empty;
                if (cat != "电影" && cat != "电视剧")
                {
                    continue;
                }

                var movie = new DoubanSubject();
                movie.Sid = sid;
                movie.Name = name;
                movie.OriginalName = !string.IsNullOrEmpty(originalName) ? originalName : name;
                movie.Genre = cat;
                movie.Category = cat;
                movie.Img = img;
                movie.Rating = rating.ToFloat();
                movie.Year = year.ToInt();
                movie.Intro = desc;
                list.Add(movie);
            }

            if (list.Count > 0)
            {
                _memoryCache.Set<List<DoubanSubject>>(cacheKey, list, expiredOption);
            }
            else
            {
                if (body.Contains("sec.douban.com"))
                {
                    this._logger.LogWarning("douban触发风控，可能ip被封，请到插件配置中打开防封禁功能。。。keyword: {0}", keyword);
                }
                else
                {
                    this._logger.LogWarning("douban搜索不到内容，这消息大量出现时，可能触发了爬虫风控。。。keyword: {0}", keyword);
                }
            }

            return list;
        }

        public async Task<List<DoubanSubject>> SearchBySuggestAsync(string keyword, CancellationToken cancellationToken)
        {
            var list = new List<DoubanSubject>();
            if (string.IsNullOrEmpty(keyword))
            {
                return list;
            }

            await LimitRequestFrequently();

            try
            {
                var encodedKeyword = HttpUtility.UrlEncode(keyword);
                var url = $"https://www.douban.com/j/search_suggest?q={encodedKeyword}";

                using (var requestMessage = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    requestMessage.Headers.Add("Origin", "https://www.douban.com");
                    requestMessage.Headers.Add("Referer", "https://www.douban.com/");

                    var response = await httpClient.SendAsync(requestMessage, cancellationToken).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        this._logger.LogWarning("douban suggest请求失败. keyword: {0} statusCode: {1}", keyword, response.StatusCode);
                        return list;
                    }

                    JsonSerializerOptions? serializeOptions = null;
                    var result = await response.Content.ReadFromJsonAsync<DoubanSuggestResult>(serializeOptions, cancellationToken).ConfigureAwait(false);

                    if (result != null && result.Cards != null)
                    {
                        foreach (var suggest in result.Cards)
                        {
                            if (suggest.Type != "movie")
                            {
                                continue;
                            }

                            var movie = new DoubanSubject();
                            movie.Sid = suggest.Sid;
                            movie.Name = suggest.Title;
                            movie.Year = suggest.Year.ToInt();
                            list.Add(movie);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                this._logger.LogError(ex, "SearchBySuggestAsync error. keyword: {0}", keyword);
            }

            return list;
        }

        public async Task<DoubanSubject?> GetMovieAsync(string sid, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(sid))
            {
                return null;
            }

            var cacheKey = $"movie_{sid}";
            var expiredOption = new MemoryCacheEntryOptions() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30) };
            DoubanSubject? movie;
            if (_memoryCache.TryGetValue<DoubanSubject?>(cacheKey, out movie))
            {
                return movie;
            }

            await LimitRequestFrequently();

            var url = $"https://movie.douban.com/subject/{sid}/";
            var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            movie = new DoubanSubject();
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var context = BrowsingContext.New();
            var doc = await context.OpenAsync(req => req.Content(body), cancellationToken).ConfigureAwait(false);
            var contentNode = doc.QuerySelector("#content");
            if (contentNode != null)
            {
                var nameStr = contentNode.GetText("h1>span:first-child") ?? string.Empty;
                var name = GetTitle(body);
                var orginalName = nameStr.Replace(name, "").Trim();
                var yearStr = contentNode.GetText("h1>span.year") ?? string.Empty;
                var year = yearStr.GetMatchGroup(this.regYear);
                var rating = contentNode.GetText("div.rating_self strong.rating_num") ?? "0";
                var img = contentNode.GetAttr("a.nbgnbg>img", "src") ?? string.Empty;
                var category = contentNode.QuerySelector("div.episode_list") == null ? "电影" : "电视剧";
                var intro = contentNode.GetText("div#link-report-intra>span.all") ?? contentNode.GetText("div#link-report-intra>span") ?? string.Empty;
                intro = formatOverview(intro);



                var info = contentNode.GetText("#info") ?? string.Empty;
                var director = info.GetMatchGroup(this.regDirector);
                var writer = info.GetMatchGroup(this.regWriter);
                var actor = info.GetMatchGroup(this.regActor);
                var genre = info.GetMatchGroup(this.regGenre);
                var country = info.GetMatchGroup(this.regCountry);
                var language = info.GetMatchGroup(this.regLanguage);
                var duration = info.GetMatchGroup(this.regDuration);
                var subname = info.GetMatchGroup(this.regSubname);
                var imdb = info.GetMatchGroup(this.regImdb);
                var site = info.GetMatchGroup(this.regSite);
                var matchs = this.regScreen.Match(info);
                var screen = matchs.Groups.Count > 2 ? matchs.Groups[2].Value : string.Empty;

                movie.Sid = sid;
                movie.Name = name;
                movie.OriginalName = orginalName;
                movie.Year = year.ToInt();
                movie.Rating = rating.ToFloat();
                movie.Img = img;
                movie.Intro = intro;
                movie.Subname = subname;
                movie.Director = director;
                movie.Genre = genre;
                movie.Category = category;
                movie.Country = country;
                movie.Language = language;
                movie.Duration = duration;
                movie.Screen = screen;
                movie.Site = site;
                movie.Actor = actor;
                movie.Writer = writer;
                movie.Imdb = imdb;

                movie.Celebrities = new List<DoubanCelebrity>();
                var celebrityNodes = contentNode.QuerySelectorAll("#celebrities li.celebrity");
                foreach (var node in celebrityNodes)
                {
                    var celebrityIdStr = node.GetAttr("div.info a.name", "href") ?? string.Empty;
                    var celebrityId = celebrityIdStr.GetMatchGroup(this.regId);
                    var celebrityImgStr = node.GetAttr("div.avatar", "style") ?? string.Empty;
                    var celebrityImg = celebrityImgStr.GetMatchGroup(this.regBackgroundImage);
                    var celebrityName = node.GetText("div.info a.name") ?? string.Empty;
                    var celebrityRole = node.GetText("div.info span.role") ?? string.Empty;
                    var celebrityRoleType = string.Empty;

                    var celebrity = new DoubanCelebrity();
                    celebrity.Id = celebrityId;
                    celebrity.Name = celebrityName;
                    celebrity.Role = celebrityRole;
                    celebrity.RoleType = celebrityRoleType;
                    celebrity.Img = celebrityImg;
                    movie.Celebrities.Add(celebrity);
                }
                _memoryCache.Set<DoubanSubject?>(cacheKey, movie, expiredOption);
                return movie;
            }


            _memoryCache.Set<DoubanSubject?>(cacheKey, null, expiredOption);
            return null;
        }

        public async Task<List<DoubanCelebrity>> GetCelebritiesBySidAsync(string sid, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(sid))
            {
                return new List<DoubanCelebrity>();
            }

            var cacheKey = $"celebrities_{sid}";
            var expiredOption = new MemoryCacheEntryOptions() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30) };
            List<DoubanCelebrity> celebrities;
            if (this._memoryCache.TryGetValue(cacheKey, out celebrities))
            {
                return celebrities;
            }

            await LimitRequestFrequently();

            var list = new List<DoubanCelebrity>();
            var url = $"https://movie.douban.com/subject/{sid}/celebrities";
            var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new List<DoubanCelebrity>();
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var context = BrowsingContext.New();
            var doc = await context.OpenAsync(req => req.Content(body), cancellationToken).ConfigureAwait(false);

            var celebritiesElements = doc.QuerySelectorAll("div#celebrities>.list-wrapper");
            foreach (var celebritiesNode in celebritiesElements)
            {
                var celebritiesTitle = celebritiesNode.GetText("h2") ?? string.Empty;
                if (!celebritiesTitle.Contains("导演") && !celebritiesTitle.Contains("演员"))
                {
                    continue;
                }

                var celebrityElements = celebritiesNode.QuerySelectorAll("ul.celebrities-list li.celebrity");
                foreach (var node in celebrityElements)
                {

                    var celebrityIdStr = node.GetAttr("div.info a.name", "href") ?? string.Empty;
                    var celebrityId = celebrityIdStr.GetMatchGroup(this.regId);
                    var celebrityImgStr = node.GetAttr("div.avatar", "style") ?? string.Empty;
                    var celebrityImg = celebrityImgStr.GetMatchGroup(this.regBackgroundImage);
                    var celebrityNameStr = node.GetText("div.info a.name") ?? string.Empty;
                    var celebrityName = this.ParseCelebrityName(celebrityNameStr);
                    // 有时存在演员信息缺少名字的
                    if (string.IsNullOrEmpty(celebrityName))
                    {
                        continue;
                    }
                    var celebrityRoleStr = node.GetText("div.info span.role") ?? string.Empty;
                    var celebrityRole = celebrityRoleStr.GetMatchGroup(this.regRole);
                    var arrRole = celebrityRoleStr.Split(" ");
                    var celebrityRoleType = arrRole.Length > 1 ? arrRole[0] : string.Empty;
                    if (string.IsNullOrEmpty(celebrityRole))
                    {
                        celebrityRole = celebrityRoleType;
                    }

                    var celebrity = new DoubanCelebrity();
                    celebrity.Id = celebrityId;
                    celebrity.Name = celebrityName;
                    celebrity.Role = celebrityRole;
                    celebrity.RoleType = celebrityRoleType;
                    celebrity.Img = celebrityImg;

                    list.Add(celebrity);
                }
            }

            _memoryCache.Set<List<DoubanCelebrity>>(cacheKey, list, expiredOption);
            return list;
        }


        public async Task<DoubanCelebrity?> GetCelebrityAsync(string id, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            var cacheKey = $"celebrity_{id}";
            var expiredOption = new MemoryCacheEntryOptions() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30) };
            DoubanCelebrity? celebrity;
            if (_memoryCache.TryGetValue<DoubanCelebrity?>(cacheKey, out celebrity))
            {
                return celebrity;
            }

            var url = $"https://movie.douban.com/celebrity/{id}/";
            var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            celebrity = new DoubanCelebrity();
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var context = BrowsingContext.New();
            var doc = await context.OpenAsync(req => req.Content(body), cancellationToken).ConfigureAwait(false);
            var contentNode = doc.QuerySelector("#content");
            if (contentNode != null)
            {
                celebrity.Img = contentNode.GetAttr("img.avatar", "src") ?? string.Empty;
                var nameStr = contentNode.GetText("h1.subject-name") ?? string.Empty;
                celebrity.Name = this.ParseCelebrityName(nameStr);
                celebrity.EnglishName = nameStr.Replace(celebrity.Name, "").Trim();

                
                var family = string.Empty;
                var propertyNodes = contentNode.QuerySelectorAll("ul.subject-property>li");
                foreach (var li in propertyNodes)
                {
                    var label = li.GetText("span.label") ?? string.Empty;
                    var value = li.GetText("span.value") ?? string.Empty;
                    switch (label)
                    {
                        case "性别:":
                            celebrity.Gender = value;
                            break;
                        case "星座:":
                            celebrity.Constellation = value;
                            break;
                        case "出生日期:":
                            celebrity.Birthdate = value;
                            break;
                        case "去世日期:":
                            celebrity.Enddate = value;
                            break;
                        case "生卒日期:":
                            var match = this.regLifedate.Match(value);
                            if (match.Success && match.Groups.Count > 2)
                            {
                                celebrity.Birthdate = match.Groups[1].Value.Trim();
                                celebrity.Enddate = match.Groups[2].Value.Trim();
                            }
                            break;
                        case "出生地:":
                            celebrity.Birthplace = value;
                            break;
                        case "职业:":
                            celebrity.Role = value;
                            break;
                        case "更多外文名:":
                            celebrity.NickName = value;
                            break;
                        case "家庭成员:":
                            family = value;
                            break;
                        case "IMDb编号:":
                            celebrity.Imdb = value;
                            break;
                        default:
                            break;
                    }
                }

                // 保留段落关系，把段落替换为换行符
                var intro = contentNode.GetHtml("section.subject-intro div.content") ?? string.Empty;
                intro = regHtmlTag.Replace(intro.Replace("</p>", "\n"), "");
                celebrity.Intro = formatOverview(intro);
                _memoryCache.Set<DoubanCelebrity?>(cacheKey, celebrity, expiredOption);
                return celebrity;
            }


            _memoryCache.Set<DoubanCelebrity?>(cacheKey, null, expiredOption);
            return null;
        }


        public async Task<List<DoubanPhoto>> GetCelebrityPhotosAsync(string cid, CancellationToken cancellationToken)
        {
            var list = new List<DoubanPhoto>();
            if (string.IsNullOrEmpty(cid))
            {
                return list;
            }

            var cacheKey = $"celebrity_photo_{cid}";
            var expiredOption = new MemoryCacheEntryOptions() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30) };
            if (_memoryCache.TryGetValue<List<DoubanPhoto>>(cacheKey, out var photos))
            {
                return photos;
            }

            await LimitRequestFrequently();

            try
            {
                var url = $"https://movie.douban.com/celebrity/{cid}/photos/";
                var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return list;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var context = BrowsingContext.New();
                var doc = await context.OpenAsync(req => req.Content(body), cancellationToken).ConfigureAwait(false);
                var elements = doc.QuerySelectorAll(".poster-col3>li");

                foreach (var node in elements)
                {

                    var href = node.QuerySelector("a")?.GetAttribute("href") ?? string.Empty;
                    var id = href.GetMatchGroup(this.regPhotoId);
                    var raw = node.QuerySelector("img")?.GetAttribute("src") ?? string.Empty;
                    var size = node.GetText("div.prop") ?? string.Empty;

                    var photo = new DoubanPhoto();
                    photo.Id = id;
                    photo.Size = size;
                    photo.Raw = raw;
                    if (!string.IsNullOrEmpty(size))
                    {
                        var arr = size.Split('x');
                        if (arr.Length == 2)
                        {
                            photo.Width = arr[0].ToInt();
                            photo.Height = arr[1].ToInt();
                        }
                    }

                    list.Add(photo);
                }

                _memoryCache.Set<List<DoubanPhoto>>(cacheKey, list, expiredOption);
            }
            catch (Exception ex)
            {
                this._logger.LogError(ex, "GetCelebrityPhotosAsync error. cid: {0}", cid);
            }

            return list;
        }

        public string ParseCelebrityName(string nameString)
        {
            if (string.IsNullOrEmpty(nameString))
            {
                return string.Empty;
            }

            // 只有中文名情况
            var idx = nameString.IndexOf(" ", StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return nameString.Trim();
            }

            // 中英名混合情况
            var firstName = nameString.Substring(0, idx);
            if (firstName.HasChinese())
            {
                return firstName.Trim();
            }

            // 英文名重复两次的情况
            var nextIndex = nameString[idx..].IndexOf(firstName, StringComparison.OrdinalIgnoreCase);
            if (nextIndex >= 0)
            {
                nextIndex = idx + nextIndex;
                return nameString[..nextIndex].Trim();
            }

            // 只有英文名情况
            return nameString.Trim();
        }


        public async Task<List<DoubanCelebrity>> SearchCelebrityAsync(string keyword, CancellationToken cancellationToken)
        {
            var list = new List<DoubanCelebrity>();
            if (string.IsNullOrEmpty(keyword))
            {
                return list;
            }

            var cacheKey = $"search_celebrity_{keyword}";
            var expiredOption = new MemoryCacheEntryOptions() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30) };
            if (_memoryCache.TryGetValue<List<DoubanCelebrity>>(cacheKey, out var searchResult))
            {
                return searchResult;
            }


            keyword = HttpUtility.UrlEncode(keyword);
            var url = $"https://movie.douban.com/celebrities/search?search_text={keyword}";
            var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return list;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var context = BrowsingContext.New();
            var doc = await context.OpenAsync(req => req.Content(body), cancellationToken).ConfigureAwait(false);
            var elements = doc.QuerySelectorAll("div.article .result");

            foreach (var el in elements)
            {

                var celebrity = new DoubanCelebrity();
                var img = el.GetAttr("div.pic img", "src") ?? string.Empty;
                var href = el.GetAttr("h3>a", "href") ?? string.Empty;
                var cid = href.GetMatchGroup(this.regId);
                var nameStr = el.GetText("h3>a") ?? string.Empty;
                var arr = nameStr.Split(" ");
                var name = arr.Length > 1 ? arr[0] : nameStr;

                celebrity.Name = name;
                celebrity.Img = img;
                celebrity.Id = cid;
                list.Add(celebrity);
            }


            _memoryCache.Set<List<DoubanCelebrity>>(cacheKey, list, expiredOption);
            return list;
        }

        public async Task<List<DoubanPhoto>> GetWallpaperBySidAsync(string sid, CancellationToken cancellationToken)
        {
            var list = new List<DoubanPhoto>();
            if (string.IsNullOrEmpty(sid))
            {
                return list;
            }

            var cacheKey = $"photo_{sid}";
            var expiredOption = new MemoryCacheEntryOptions() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30) };
            if (_memoryCache.TryGetValue<List<DoubanPhoto>>(cacheKey, out var photos))
            {
                return photos;
            }

            await LimitRequestFrequently();

            try
            {
                var url = $"https://movie.douban.com/subject/{sid}/photos?type=W&start=0&sortby=size&size=a&subtype=a";
                var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return list;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var context = BrowsingContext.New();
                var doc = await context.OpenAsync(req => req.Content(body), cancellationToken).ConfigureAwait(false);
                var elements = doc.QuerySelectorAll(".poster-col3>li");

                foreach (var node in elements)
                {

                    var id = node.GetAttribute("data-id") ?? string.Empty;
                    var imgUrl = node.QuerySelector("img")?.GetAttribute("src") ?? string.Empty;
                    var imgHost = regImgHost.FirstMatchGroup(imgUrl, "img2");
                    var small = $"https://{imgHost}.doubanio.com/view/photo/s/public/p{id}.jpg";
                    var medium = $"https://{imgHost}.doubanio.com/view/photo/m/public/p{id}.jpg";
                    var large = $"https://{imgHost}.doubanio.com/view/photo/l/public/p{id}.jpg";
                    var raw = $"https://{imgHost}.doubanio.com/view/photo/raw/public/p{id}.jpg";
                    var size = node.GetText("div.prop") ?? string.Empty;

                    var photo = new DoubanPhoto();
                    photo.Id = id;
                    photo.Size = size;
                    photo.Small = small;
                    photo.Medium = medium;
                    photo.Large = large;
                    photo.Raw = raw;
                    if (!string.IsNullOrEmpty(size))
                    {
                        var arr = size.Split('x');
                        if (arr.Length == 2)
                        {
                            photo.Width = arr[0].ToInt();
                            photo.Height = arr[1].ToInt();
                        }
                    }

                    list.Add(photo);
                }

                _memoryCache.Set<List<DoubanPhoto>>(cacheKey, list, expiredOption);
            }
            catch (Exception ex)
            {
                this._logger.LogError(ex, "GetWallpaperBySidAsync error. sid: {0}", sid);
            }

            return list;
        }

        public async Task<bool> CheckLoginAsync(CancellationToken cancellationToken)
        {
            try
            {
                var url = "https://www.douban.com/mine/";
                var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                var requestUrl = response.RequestMessage?.RequestUri?.ToString();
                if (requestUrl == null || requestUrl.Contains("accounts.douban.com") || requestUrl.Contains("login") || requestUrl.Contains("sec.douban.com"))
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                this._logger.LogError(ex, "CheckLoginAsync error.");
            }

            return true;
        }

        public async Task<DoubanLoginInfo> GetLoginInfoAsync(CancellationToken cancellationToken)
        {
            var loginInfo = new DoubanLoginInfo();
            try
            {
                var url = "https://www.douban.com/mine/";
                var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                var requestUrl = response.RequestMessage?.RequestUri?.ToString();
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var loginName = this.Match(body, regLoginName).Trim();
                loginInfo.Name = loginName;
                loginInfo.IsLogined = !(requestUrl == null || requestUrl.Contains("accounts.douban.com") || requestUrl.Contains("login") || requestUrl.Contains("sec.douban.com"));
            }
            catch (Exception ex)
            {
                this._logger.LogError(ex, "GetLoginInfoAsync error.");
            }

            return loginInfo;
        }

        protected async Task LimitRequestFrequently()
        {
            if (IsEnableAvoidRiskControl())
            {
                var configCookie = Plugin.Instance?.Configuration.DoubanCookies.Trim() ?? string.Empty;
                if (!string.IsNullOrEmpty(configCookie))
                {
                    await this._loginedTimeConstraint;
                }
                else
                {
                    await this._guestTimeConstraint;
                }
            }
            else
            {
                await this._defaultTimeConstraint;
            }
        }

        private string GetTitle(string body)
        {
            var title = string.Empty;

            var keyword = Match(body, regKeywordMeta);
            if (!string.IsNullOrEmpty(keyword))
            {
                title = keyword.Split(",").FirstOrDefault();
                if (!string.IsNullOrEmpty(title))
                {
                    return title.Trim();
                }
            }

            title = Match(body, regTitle);
            return title.Replace("(豆瓣)", "").Trim();
        }

        private string? GetText(IElement el, string css)
        {
            var node = el.QuerySelector(css);
            if (node != null)
            {
                return node.Text();
            }

            return null;
        }

        private string? GetAttr(IElement el, string css, string attr)
        {
            var node = el.QuerySelector(css);
            if (node != null)
            {
                return node.GetAttribute(attr);
            }

            return null;
        }

        private string Match(string text, Regex reg)
        {
            var match = reg.Match(text);
            if (match.Success && match.Groups.Count > 1)
            {
                return match.Groups[1].Value;
            }

            return string.Empty;
        }

        private string formatOverview(string intro)
        {
            intro = intro.Replace("©豆瓣", string.Empty);
            return regOverviewSpace.Replace(intro, "\n").Trim();
        }

        private bool IsEnableAvoidRiskControl()
        {
            return Plugin.Instance?.Configuration.EnableDoubanAvoidRiskControl ?? false;
        }


        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                httpClient.Dispose();
                _memoryCache.Dispose();
            }
        }
    }
}
