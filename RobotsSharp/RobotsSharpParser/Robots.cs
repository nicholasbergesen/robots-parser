﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;

namespace RobotsSharpParser
{
    public interface IRobots
    {
        void Load();
        Task LoadAsync();
        int UserAgentCount();
        IEnumerable<string> UserAgents { get; }
        IEnumerable<string> Sitemaps { get; }
        int Crawldelay { get; }
        IEnumerable<string> GetAllowedPaths(string userAgent = "*");
        IEnumerable<string> GetDisallowedPaths(string userAgent = "*");
        bool IsPathAllowed(string path, string userAgent = "*");
        bool IsPathDisallowed(string path, string userAgent = "*");
        IEnumerable<tUrl> GetSitemapLinks(string sitemapUrl = "");
        Task<IEnumerable<tUrl>> GetSitemapLinksAsync(string sitemapUrl = "");
    }

    public class Robots : IRobots
    {
        Uri _robotsUri;
        private string _robots;
        private HttpClient _client = new HttpClient();

        private class Const
        {
            public const string Disallow = "Disallow:";
            public const short DisallowLength = 9;

            public const string Allow = "Allow:";
            public const short AllowLength = 6;

            public const string UserAgent = "User-agent:";
            public const short UserAgentLength = 11;

            public const string Sitemap = "Sitemap:";
            public const short SitemapLength = 8;

            public const string Crawldelay = "Crawl-delay:";
            public const short CrawldelayLength = 12;
        }

        public Robots(Uri websiteUri, string userAgent)
        {
            if (Uri.TryCreate(websiteUri, "robots.txt", out Uri robots))
                _robotsUri = robots;
            else
                throw new ArgumentException($"Unable to append robots.txt to {websiteUri}");

            _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        }

        public async Task LoadAsync()
        {
            _robots = await _client.GetStringAsync(_robotsUri);
            await ParseRobotsAsync();
        }

        public void Load()
        {
            LoadAsync().Wait();
            ParseRobotsAsync().Wait();
        }

        private async Task ParseRobotsAsync()
        {
            _userAgents = new HashSet<string>();
            _disallow = new Dictionary<string, HashSet<string>>();
            _allow = new Dictionary<string, HashSet<string>>();
            _sitemaps = new HashSet<string>();

            string line;
            using (StringReader sr = new StringReader(_robots))
            {
                string currentAgent = "*";
                while ((line = await sr.ReadLineAsync()) != null)
                {
                    if (line.StartsWith(Const.UserAgent))
                    {
                        currentAgent = line.Substring(Const.UserAgentLength, line.Length - Const.UserAgentLength).Trim(' ');
                        _userAgents.Add(currentAgent);
                        _allow[currentAgent] = new HashSet<string>();
                        _disallow[currentAgent] = new HashSet<string>();
                    }
                    else if (line.StartsWith(Const.Disallow))
                        _disallow[currentAgent].Add(line.Substring(Const.DisallowLength, line.Length - Const.DisallowLength).Trim(' '));
                    else if (line.StartsWith(Const.Allow))
                        _allow[currentAgent].Add(line.Substring(Const.AllowLength, line.Length - Const.AllowLength).Trim(' '));
                    else if (line.StartsWith(Const.Sitemap))
                        _sitemaps.Add(line.Substring(Const.SitemapLength, line.Length - Const.SitemapLength).Trim(' '));
                    else if (line.StartsWith(Const.Crawldelay))
                        Crawldelay = int.Parse(line.Substring(Const.CrawldelayLength, line.Length - Const.CrawldelayLength).Trim(' '));
                    else if (line == string.Empty || line[0] == '#')
                        continue;
                    else
                        throw new Exception($"Unable to parse {line} in robots.txt");
                }
            }
        }
        public int Crawldelay { get; private set; }

        private HashSet<string> _userAgents;
        public IEnumerable<string> UserAgents
        {
            get
            {
                if (_userAgents == null)
                    throw new RobotsNotloadedException("Please call Load or LoadAsync.");
                return _userAgents;
            }
        }

        private HashSet<string> _sitemaps;
        public IEnumerable<string> Sitemaps
        {
            get
            {
                if (_sitemaps == null)
                    throw new RobotsNotloadedException("Please call Load or LoadAsync.");
                return _sitemaps;
            }
        }

        private Dictionary<string, HashSet<string>> _allow;
        public IEnumerable<string> GetAllowedPaths(string userAgent = "*")
        {
            return _allow[userAgent];
        }

        public bool IsPathAllowed(string path, string userAgent = "*")
        {
            if (_allow.TryGetValue(userAgent, out HashSet<string> allowed))
                return allowed.Contains(path);
            return false;
        }

        private Dictionary<string, HashSet<string>> _disallow;
        public IEnumerable<string> GetDisallowedPaths(string userAgent = "*")
        {
            return _disallow[userAgent];
        }

        public bool IsPathDisallowed(string path, string userAgent = "*")
        {
            if (_disallow.TryGetValue(userAgent, out HashSet<string> disallowed))
                return disallowed.Contains(path);
            return false;
        }

        public async Task<IEnumerable<tUrl>> GetSitemapLinksAsync(string sitemapUrl = "")
        {
            List<tUrl> sitemapLinks = new List<tUrl>();

            if(sitemapUrl == string.Empty)
                foreach (var siteIndex in _sitemaps)
                    await GetSitemalLinksInternal(sitemapLinks, siteIndex);
            else
                await GetSitemalLinksInternal(sitemapLinks, sitemapUrl);

            return sitemapLinks;
        }

        private async Task GetSitemalLinksInternal(List<tUrl> sitemapLinks, string siteIndex)
        {
            Stream siteIndexStream = await _client.GetStreamAsync(siteIndex);
            if (siteIndex.EndsWith(".gz"))
                siteIndexStream = new GZipStream(siteIndexStream, CompressionMode.Decompress);
            if (TryDeserializeXMLStream(siteIndexStream, out sitemapindex sitemapIndex))
            {
                foreach (tSitemap sitemap in sitemapIndex.sitemap)
                {
                    Stream httpStream = await _client.GetStreamAsync(sitemap.loc);
                    if (TryDeserializeXMLStream(httpStream, out urlset urlSet) && urlSet.url != null)
                    {
                        sitemapLinks.AddRange(urlSet.url);
                    }
                }
            }
        }

        public IEnumerable<tUrl> GetSitemapLinks(string sitemapUrl = "")
        {
            return GetSitemapLinksAsync(sitemapUrl).Result;
        }

        private bool TryDeserializeXMLStream<T>(Stream stream, out T xmlValue)
        {
            try
            {
                using (XmlReader xmlReader = XmlReader.Create(stream))
                {
                    XmlSerializer serializer = new XmlSerializer(typeof(T));
                    xmlValue = (T)serializer.Deserialize(xmlReader);
                    return xmlValue != null;
                }
            }
            catch
            {
                xmlValue = default(T);
                return false;
            }
        }

        public int UserAgentCount()
        {
            return _userAgents.Count;
        }
    }
}
