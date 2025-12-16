using ARSoft.Tools.Net;
using ARSoft.Tools.Net.Dns;
using MaxMind.GeoIP2;
using McMaster.Extensions.CommandLineUtils;
using NStack;
using System.Collections.Concurrent;
using System.Net;

namespace ArashiDNS.Nous
{
    internal class Program
    {
        public static IPEndPoint GlobalServer = IPEndPoint.Parse("9.9.9.11:9953");
        public static IPEndPoint RegionalServer = IPEndPoint.Parse("223.5.5.5:53");
        public static IPEndPoint ListenerEndPoint = new(IPAddress.Loopback, 6653);
        public static DatabaseReader CountryReader;
        public static TldExtract TldExtract;
        public static string TargetRegion = "CN";
        public static int TimeOut = 3000;
        public static int LogLevel = 1; // 0: Error, 1: Info, 2: Debug
        public static IPAddress RegionalECS = IPAddress.Parse("123.123.123.0");
        public static bool NoList = false;
        public static string CountryMmdbPath = "./GeoLite2-Country.mmdb";
        public static string PslDatPath = "./public_suffix_list.dat";
        public static bool UseDnsResponseCache = false;
        public static bool UseEcsCache = false;

        public static Timer CacheCleanupTimer;

        public class CacheItem<T>
        {
            public T Value { get; set; }
            public DateTime ExpiryTime { get; set; }
            public bool IsExpired => DateTime.UtcNow >= ExpiryTime;
        }

        public static ConcurrentDictionary<DomainName, CacheItem<string>> DomainRegionMap = new();
        public static ConcurrentDictionary<string, CacheItem<DnsMessage>> DnsResponseCache = new();
        public static ConcurrentDictionary<string, CacheItem<DnsMessage>> NsQueryCache = new();


        public static DnsQueryOptions QueryOptions = new()
        {
            IsEDnsEnabled = true,
            EDnsOptions = new OptRecord {Options = {new ClientSubnetOption(24, RegionalECS)}}
        };

        static void Main(string[] args)
        {
            var cmd = new CommandLineApplication
            {
                Name = "ArashiDNS.Nous",
                Description = "ArashiDNS.Nous - ListFree Geo Diversion DNS Forwarder" +
                              Environment.NewLine +
                              $"Copyright (c) {DateTime.Now.Year} Milkey Tan. Code released under the FSL-1.1-ALv2 License"
            };
            cmd.HelpOption("-?|-he|--help");
            var wOption = cmd.Option<int>("-w <TimeOut>", "等待回复的超时时间（毫秒）。", CommandOptionType.SingleValue);
            var sOption = cmd.Option<string>("-s <IPEndPoint>", "设置目标区域服务器的地址。[223.5.5.5:53]",
                CommandOptionType.SingleValue);
            var gOption =
                cmd.Option<string>("-g <IPEndPoint>", "设置全局服务器地址。[8.8.8.8:53]", CommandOptionType.SingleValue);
            var rOption = cmd.Option<string>("-r <Region>", "设置目标区域。[CN]", CommandOptionType.SingleValue);
            var ecsOption = cmd.Option<string>("-e <IPAddress>", "设置目标区域 ECS 地址。[123.123.123.123]",
                CommandOptionType.SingleValue);
            var lOption = cmd.Option<string>("-l <ListenerEndPoint>", "设置监听地址。[0.0.0.0:6653]",
                CommandOptionType.SingleValue);
            var logOption = cmd.Option<int>("--log <LogLevel>", "设置日志级别。" + Environment.NewLine + "0: 错误, 1: 信息, 2: 调试",
                CommandOptionType.SingleValue);
            var noListOption = cmd.Option<bool>("-n|--no-list", "不加载 NS 域名列表。", CommandOptionType.NoValue);
            var countryMmdbOption = cmd.Option<string>("--mmdb <Path>", "设置 GeoLite2-Country.mmdb 的路径。",
                CommandOptionType.SingleValue);
            var pslDatOption = cmd.Option<string>("--psl <Path>", "设置 public_suffix_list.dat 的路径。",
                CommandOptionType.SingleValue);
            var useDnsResponseCacheOption =
                cmd.Option<bool>("-c|--use-response-cache", "使用响应缓存", CommandOptionType.NoValue);

            cmd.OnExecute(() =>
            {
                if (wOption.HasValue()) TimeOut = wOption.ParsedValue;
                if (sOption.HasValue()) RegionalServer = IPEndPoint.Parse(sOption.ParsedValue);
                if (gOption.HasValue()) GlobalServer = IPEndPoint.Parse(gOption.ParsedValue);
                if (rOption.HasValue()) TargetRegion = rOption.ParsedValue;
                if (lOption.HasValue()) ListenerEndPoint = IPEndPoint.Parse(lOption.ParsedValue);
                if (logOption.HasValue()) LogLevel = logOption.ParsedValue;
                if (noListOption.HasValue()) NoList = noListOption.ParsedValue;
                if (countryMmdbOption.HasValue()) CountryMmdbPath = countryMmdbOption.ParsedValue;
                if (pslDatOption.HasValue()) PslDatPath = pslDatOption.ParsedValue;
                if (useDnsResponseCacheOption.HasValue()) UseDnsResponseCache = useDnsResponseCacheOption.ParsedValue;

                if (RegionalServer.Port == 0) RegionalServer = new IPEndPoint(RegionalServer.Address, 53);
                if (GlobalServer.Port == 0) GlobalServer = new IPEndPoint(GlobalServer.Address, 53);
                if (ListenerEndPoint.Port == 0) ListenerEndPoint = new IPEndPoint(ListenerEndPoint.Address, 6653);

                var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "curl/8.5.0");

                if (ecsOption.HasValue()) RegionalECS = IPAddress.Parse(ecsOption.ParsedValue);
                else
                {
                    try
                    {
                        var info = client.GetStringAsync("https://www.cloudflare-cn.com/cdn-cgi/trace").Result;
                        if (info.Contains("loc=CN"))
                            RegionalECS = IPAddress.Parse(info.Split('\n').First(i => i.StartsWith("ip=")).Split("=")
                                .LastOrDefault()?.Trim() ?? string.Empty);
                    }
                    catch (Exception)
                    {
                        RegionalECS =
                            IPAddress.Parse(client.GetStringAsync("http://whatismyip.akamai.com/").Result);
                    }

                    Console.WriteLine("Regional ECS: " + RegionalECS);
                }


                if (!NoList)
                {
                    foreach (var item in new HttpClient()
                                 .GetStringAsync(
                                     "https://fastly.jsdelivr.net/gh/felixonmars/dnsmasq-china-list@master/ns-whitelist.txt")
                                 .Result.Split('\n'))
                    {
                        try
                        {
                            if (string.IsNullOrWhiteSpace(item) || item.StartsWith('#')) continue;
                            DomainRegionMap.Set(DomainName.Parse(item.Trim().Trim('.')),
                                new CacheItem<string> {Value = "CN", ExpiryTime = DateTime.UtcNow.AddDays(30)});
                            Console.WriteLine($"Add Ns Cache: {item.Trim().Trim('.')} -> CN");
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine(e);
                        }
                    }

                    foreach (var item in new HttpClient()
                                 .GetStringAsync(
                                     "https://fastly.jsdelivr.net/gh/felixonmars/dnsmasq-china-list@master/ns-blacklist.txt")
                                 .Result.Split('\n'))
                    {
                        try
                        {
                            if (string.IsNullOrWhiteSpace(item) || item.StartsWith('#')) continue;
                            DomainRegionMap.Set(DomainName.Parse(item.Trim().Trim('.')),
                                new CacheItem<string> {Value = "UN", ExpiryTime = DateTime.UtcNow.AddDays(30)});
                            Console.WriteLine($"Add Ns Cache: {item.Trim().Trim('.')} -> UN");
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine(e);
                        }
                    }

                    foreach (var item in new HttpClient()
                                 .GetStringAsync("https://fastly.jsdelivr.net/gh/mili-tan/ArashiDNS.Nous@master/ns.csv")
                                 .Result
                                 .Split('\n'))
                    {
                        try
                        {
                            if (string.IsNullOrWhiteSpace(item) || item.StartsWith('#')) continue;
                            var i = item.Split(',');
                            DomainRegionMap.Set(DomainName.Parse(i[0].Trim().Trim('.')),
                                new CacheItem<string> {Value = i[1], ExpiryTime = DateTime.UtcNow.AddDays(30)});
                            Console.WriteLine($"Add Ns Cache: {i[0].Trim().Trim('.')} -> {i[1]}");
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine(e);
                        }
                    }
                }

                if (!countryMmdbOption.HasValue() && !File.Exists("./GeoLite2-Country.mmdb"))
                {
                    Console.WriteLine(
                        "This product includes GeoLite2 data created by MaxMind, available from https://www.maxmind.com");
                    Console.WriteLine("Downloading GeoLite2-Country.mmdb...");
                    File.WriteAllBytes("./GeoLite2-Country.mmdb",
                        new HttpClient()
                            .GetByteArrayAsync(
                                "https://fastly.jsdelivr.net/gh/P3TERX/GeoLite.mmdb@download/GeoLite2-Country.mmdb")
                            .Result);
                }

                if (!pslDatOption.HasValue() && !File.Exists("./public_suffix_list.dat"))
                {
                    Console.WriteLine("Downloading public_suffix_list.dat...");
                    File.WriteAllBytes("./public_suffix_list.dat",
                        new HttpClient()
                            .GetByteArrayAsync(
                                "https://publicsuffix.org/list/public_suffix_list.dat")
                            .Result);
                }

                CountryReader = new DatabaseReader(CountryMmdbPath);
                TldExtract = new TldExtract(PslDatPath);

                QueryOptions = new DnsQueryOptions()
                {
                    IsEDnsEnabled = true,
                    EDnsOptions = new OptRecord {Options = {new ClientSubnetOption(24, RegionalECS)}}
                };

                var dnsServer = new DnsServer(new UdpServerTransport(ListenerEndPoint),
                    new TcpServerTransport(ListenerEndPoint));
                dnsServer.QueryReceived += DnsServerOnQueryReceived;
                dnsServer.Start();

                CleanupCacheTask();

                Console.WriteLine("Now listening on: " + ListenerEndPoint);
                Console.WriteLine("Application started. Press Ctrl+C / q to shut down.");
                if (!Console.IsInputRedirected && Console.KeyAvailable)
                {
                    while (true)
                        if (Console.ReadKey().KeyChar == 'q')
                            Environment.Exit(0);
                }

                EventWaitHandle wait = new AutoResetEvent(false);
                while (true) wait.WaitOne();
            });
            cmd.Execute(args);
        }

        private static async Task DnsServerOnQueryReceived(object sender, QueryReceivedEventArgs e)
        {
            if (e.Query is not DnsMessage query || query.Questions.Count == 0) return;

            if (query.Questions.First().Name.IsEqualOrSubDomainOf(DomainName.Parse("use-application-dns.net")))
            {
                var msg = query.CreateResponseInstance();
                msg.IsRecursionAllowed = true;
                msg.IsRecursionDesired = true;
                msg.ReturnCode = ReturnCode.NoError;
                e.Response = msg;
                return;
            }

            if (query.Questions.First().RecordClass == RecordClass.Chaos &&
                query.Questions.First().RecordType == RecordType.Txt &&
                query.Questions.First().Name.IsEqualOrSubDomainOf(DomainName.Parse("version.bind")))
            {
                var msg = query.CreateResponseInstance();
                msg.IsRecursionAllowed = true;
                msg.IsRecursionDesired = true;
                msg.AnswerRecords.Add(
                    new TxtRecord(query.Questions.First().Name, 3600, "ArashiDNS.Nous"));
                e.Response = msg;
                return;
            }

            var questName = query.Questions.First().Name;
            var questType = query.Questions.First().RecordType;
            var cacheKey = $"{questName}|{questType}";
            if (UseEcsCache) cacheKey += $"|{GetIpFromDns(query)}";

            if (UseDnsResponseCache && DnsResponseCache.TryGetValue(cacheKey, out var cacheItem) && !cacheItem.IsExpired)
            {
                if (LogLevel >= 2) Console.WriteLine($"DNS Cache Hit: {questName} {questType}");
                e.Response = cacheItem.Value;
                e.Response.TransactionID = query.TransactionID;
                return;
            }

            var questExtract = TldExtract.Extract(questName.ToString().Trim().Trim('.'));

            if (!query.IsEDnsEnabled || (query.EDnsOptions != null && query.EDnsOptions.Options.Any(x =>
                    x.Type == EDnsOptionType.ClientSubnet)))
            {
                query.IsEDnsEnabled = true;
                query.EDnsOptions = new OptRecord
                {
                    Options = { new ClientSubnetOption(24, RegionalECS) }
                };
            }

            if (LogLevel >= 2) Console.WriteLine(questExtract);
            var questRName = DomainName.Parse(string.Join('.', string.IsNullOrWhiteSpace(questExtract.tld)
                ? questName.Labels.TakeLast(2)
                : [questExtract.root, questExtract.tld]));
            DnsMessage? response;
            var (RNsIs, roorNs) = await FromNameGetNsIs(questRName);

            if (LogLevel >= 1)
                Console.WriteLine("RNAME Result: " + string.Join(" | ", questName.ToString(),
                    questRName.ToString(), "-",
                    roorNs.ToString(), RNsIs.ToString()));

            if (RNsIs)
                response = await new DnsClient([RegionalServer.Address], [
                    new UdpClientTransport(RegionalServer.Port),
                    new TcpClientTransport(RegionalServer.Port)
                ], queryTimeout: TimeOut).SendMessageAsync(query);
            else
            {
                response = await new DnsClient([GlobalServer.Address],
                    [new UdpClientTransport(RegionalServer.Port), new TcpClientTransport(RegionalServer.Port)],
                    queryTimeout: TimeOut).SendMessageAsync(query);
                if (response != null && response.AnswerRecords.Any(x => x.RecordType == RecordType.CName))
                {
                    var cName =
                        (response.AnswerRecords.LastOrDefault(x => x.RecordType == RecordType.CName) as CNameRecord)
                        ?.CanonicalName;
                    var cnameExtract = TldExtract.Extract(cName.ToString().Trim().Trim('.'));
                    var cnameRName = DomainName.Parse(string.Join('.', string.IsNullOrWhiteSpace(cnameExtract.tld)
                        ? cName.Labels.TakeLast(2)
                        : [cnameExtract.root, cnameExtract.tld]));
                    var (cnameNsIs, cnameNs) = await FromNameGetNsIs(cnameRName);

                    if (LogLevel >= 1)
                        Console.WriteLine("CNAME Result: " + string.Join(" | ", questName.ToString(), cName.ToString(),
                            "-",
                            cnameRName.ToString(), cnameNs.ToString(), "-", cnameNsIs.ToString()));

                    if (cnameNsIs)
                        response = await new DnsClient([RegionalServer.Address],
                            [new UdpClientTransport(RegionalServer.Port), new TcpClientTransport(RegionalServer.Port)],
                            queryTimeout: TimeOut).SendMessageAsync(query);
                }
            }

            if (UseDnsResponseCache && response != null && response.ReturnCode == ReturnCode.NoError && response.AnswerRecords.Any())
            {
                var minTTL = Math.Max(response.AnswerRecords.Min(r => r.TimeToLive), 60);
                var expiryTime = DateTime.UtcNow.AddSeconds(minTTL);

                DnsResponseCache.Set(cacheKey, new CacheItem<DnsMessage>
                {
                    Value = response,
                    ExpiryTime = expiryTime
                });

                if (LogLevel >= 2) Console.WriteLine($"DNS Cache Set: {questName} {questType} TTL: {minTTL}s");
            }

            if (LogLevel >= 1)
                Console.WriteLine("-----------------------------------");
            e.Response = response;
        }

        private static async Task<(bool isRegion, DomainName ns)> FromNameGetNsIs(DomainName name)
        {
            try
            {
                if (LogLevel >= 2) Console.WriteLine("Root Name: " + name);

                if (DomainRegionMap.TryGetValue(name, out var cacheItem) && !cacheItem.IsExpired)
                {
                    if (LogLevel >= 2)
                        Console.WriteLine($"Found Cache: {name} -> {cacheItem.Value}");
                    return (string.Equals(cacheItem.Value, TargetRegion, StringComparison.CurrentCultureIgnoreCase),
                        name);
                }

                var findName = DomainRegionMap.Keys.FirstOrDefault(name.IsEqualOrSubDomainOf);
                if (findName != null && DomainRegionMap.TryGetValue(findName, out var parentCache) &&
                    !parentCache.IsExpired)
                {
                    if (LogLevel >= 2)
                        Console.WriteLine($"Found Parent Cache: {findName} -> {parentCache.Value}");
                    return (string.Equals(parentCache.Value, TargetRegion, StringComparison.CurrentCultureIgnoreCase),
                        findName);
                }

                var nsCacheKey = $"NS|{name}";
                if (NsQueryCache.TryGetValue(nsCacheKey, out var nsCacheItem) && !nsCacheItem.IsExpired)
                {
                    if (LogLevel >= 2) Console.WriteLine($"NS Cache Hit: {name}");
                    var cachedNsMsg = nsCacheItem.Value;

                    if (cachedNsMsg != null && cachedNsMsg.AnswerRecords.Any(x => x.RecordType == RecordType.Ns))
                    {
                        var nRecord = cachedNsMsg.AnswerRecords.First(x => x.RecordType == RecordType.Ns) as NsRecord;
                        var nName = nRecord?.NameServer;

                        if (DomainRegionMap.TryGetValue(nName, out var nsCache) && !nsCache.IsExpired)
                            return (
                                string.Equals(nsCache.Value, TargetRegion, StringComparison.CurrentCultureIgnoreCase),
                                nName);
                    }
                }

                var client = new DnsClient([GlobalServer.Address],
                    [new UdpClientTransport(RegionalServer.Port), new TcpClientTransport(RegionalServer.Port)],
                    queryTimeout: TimeOut);
                var nsMsg = await client.ResolveAsync(name, RecordType.Ns, options: QueryOptions);
                if (nsMsg == null || !nsMsg.AnswerRecords.Any()) nsMsg = await client.ResolveAsync(name, RecordType.Ns);
                if (LogLevel >= 2) Console.WriteLine("NS RCode: " + nsMsg.ReturnCode);

                if (nsMsg != null && nsMsg.AnswerRecords.Any())
                {
                    var nsTTL = Math.Max(nsMsg.AnswerRecords.First().TimeToLive, 60);
                    NsQueryCache.Set(nsCacheKey, new CacheItem<DnsMessage>
                    {
                        Value = nsMsg,
                        ExpiryTime = DateTime.UtcNow.AddSeconds(nsTTL)
                    });
                }

                var nsRecord = nsMsg.AnswerRecords.OrderBy(x => x.Name.Labels.First())
                    .FirstOrDefault(x => x.RecordType == RecordType.Ns);
                if (LogLevel >= 2) Console.WriteLine("NS Record: " + nsRecord);

                var nsName = (nsRecord as NsRecord)?.NameServer;
                var nsTTLValue = nsRecord?.TimeToLive ?? 86400;

                if (nsName.ToString().Contains("awsdns-cn-"))
                {
                    if (LogLevel >= 2) Console.WriteLine($"Found AWSDNS-CN: {nsName} -> CN");
                    DomainRegionMap.Set(name, new CacheItem<string>
                    {
                        Value = "CN",
                        ExpiryTime = DateTime.UtcNow.AddSeconds(nsTTLValue * 6)
                    });
                    return (string.Equals("CN", TargetRegion, StringComparison.CurrentCultureIgnoreCase),
                        DomainName.Parse("awsdns-cn-1.com"));
                }

                if (nsName.ToString().Contains("awsdns-"))
                {
                    if (LogLevel >= 2) Console.WriteLine($"Found AWSDNS: {nsName} -> US");
                    DomainRegionMap.Set(name, new CacheItem<string>
                    {
                        Value = "US",
                        ExpiryTime = DateTime.UtcNow.AddSeconds(nsTTLValue * 6)
                    });
                    return (string.Equals("US", TargetRegion, StringComparison.CurrentCultureIgnoreCase),
                        DomainName.Parse("awsdns-1.com"));
                }

                var findNs = DomainRegionMap.Keys.FirstOrDefault(nsName.IsEqualOrSubDomainOf);
                if (findNs != null && DomainRegionMap.TryGetValue(findNs, out var nsRegionCache) &&
                    !nsRegionCache.IsExpired)
                {
                    if (LogLevel >= 2) Console.WriteLine($"Found NS Cache: {findNs} -> {nsRegionCache.Value}");
                    DomainRegionMap.Set(name, new CacheItem<string>
                    {
                        Value = nsRegionCache.Value,
                        ExpiryTime = DateTime.UtcNow.AddSeconds(nsTTLValue * 6)
                    });
                    return (string.Equals(nsRegionCache.Value, TargetRegion, StringComparison.CurrentCultureIgnoreCase),
                        findNs);
                }

                var nsACacheKey = $"A|{nsName}";
                DnsMessage nsAMsg = null;
                if (NsQueryCache.TryGetValue(nsACacheKey, out var nsACache) && !nsACache.IsExpired)
                {
                    nsAMsg = nsACache.Value;
                    if (LogLevel >= 2) Console.WriteLine($"NS-A Cache Hit: {nsName}");
                }
                else
                {
                    nsAMsg = await client.ResolveAsync(nsName, options: QueryOptions);
                    if (nsAMsg == null || !nsAMsg.AnswerRecords.Any()) nsAMsg = await client.ResolveAsync(nsName);

                    if (nsAMsg != null && nsAMsg.AnswerRecords.Any())
                    {
                        var nsATTL = Math.Max(nsAMsg.AnswerRecords.First().TimeToLive, 60);
                        NsQueryCache.Set(nsACacheKey, new CacheItem<DnsMessage>
                        {
                            Value = nsAMsg,
                            ExpiryTime = DateTime.UtcNow.AddSeconds(nsATTL)
                        });
                    }
                }

                if (LogLevel >= 2) Console.WriteLine("NS-A RCode: " + nsAMsg?.ReturnCode);
                if (LogLevel >= 2 && nsAMsg?.AnswerRecords.Any() == true)
                    Console.WriteLine("NS-A Record: " + nsAMsg.AnswerRecords.FirstOrDefault());

                var nsAddress = (nsAMsg?.AnswerRecords.FirstOrDefault(x => x.RecordType == RecordType.A) as ARecord)
                    ?.Address;
                var nsCountry = nsAddress != null ? (CountryReader.Country(nsAddress)?.Country?.IsoCode ?? "UN") : "UN";

                var nsExtract = TldExtract.Extract(nsName.ToString().Trim().Trim('.'));
                var nsRName = DomainName.Parse(string.Join('.', string.IsNullOrWhiteSpace(nsExtract.tld)
                    ? nsName.Labels.TakeLast(2)
                    : [nsExtract.root, nsExtract.tld]));

                if (LogLevel >= 2) Console.WriteLine("NS GEO: " + nsCountry);

                DomainRegionMap.Set(name, new CacheItem<string>
                {
                    Value = nsCountry,
                    ExpiryTime = DateTime.UtcNow.AddSeconds(nsTTLValue * 6)
                });

                DomainRegionMap.Set(nsRName, new CacheItem<string>
                {
                    Value = nsCountry,
                    ExpiryTime = DateTime.UtcNow.AddSeconds(nsTTLValue * 6)
                });

                return (string.Equals(nsCountry, TargetRegion, StringComparison.CurrentCultureIgnoreCase), nsName);
            }
            catch (Exception e)
            {
                if (LogLevel >= 0) Console.WriteLine("Error: " + e.Message);
                return (false, DomainName.Root);
            }
        }

        private static void CleanupCacheTask()
        {
            CacheCleanupTimer = new Timer(_ =>
            {
                try
                {
                    var expiredKeys = DomainRegionMap.Where(kv => kv.Value.IsExpired)
                        .Select(kv => kv.Key).ToList();
                    foreach (var key in expiredKeys) DomainRegionMap.TryRemove(key, out var _);

                    var expiredDnsKeys = DnsResponseCache.Where(kv => kv.Value.IsExpired)
                        .Select(kv => kv.Key).ToList();
                    foreach (var key in expiredDnsKeys) DnsResponseCache.TryRemove(key, out var _);

                    var expiredNsKeys = NsQueryCache.Where(kv => kv.Value.IsExpired)
                        .Select(kv => kv.Key).ToList();
                    foreach (var key in expiredNsKeys) NsQueryCache.TryRemove(key, out var _);

                    if (LogLevel >= 2 && (expiredKeys.Any() || expiredDnsKeys.Any() || expiredNsKeys.Any()))
                        Console.WriteLine($"Cache cleanup: {expiredKeys.Count} region entries, " +
                                          $"{expiredDnsKeys.Count} DNS entries, " +
                                          $"{expiredNsKeys.Count} NS entries removed.");
                }
                catch (Exception ex)
                {
                    if (LogLevel >= 0) Console.WriteLine($"Cache cleanup error: {ex.Message}");
                }
            }, true, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));
        }

        public static IPAddress GetIpFromDns(DnsMessage dnsMsg)
        {
            try
            {
                if (dnsMsg is { IsEDnsEnabled: false }) return IPAddress.Any;
                foreach (var eDnsOptionBase in dnsMsg.EDnsOptions.Options.ToList())
                {
                    if (eDnsOptionBase is ClientSubnetOption option)
                        return option.Address;
                }

                return IPAddress.Any;
            }
            catch (Exception)
            {
                return IPAddress.Any;
            }
        }
    }

    internal static class ConcurrentDictionaryExtensions
    {
        public static void Set<TKey, TValue>(this ConcurrentDictionary<TKey, TValue> dictionary,
            TKey key, TValue value) where TKey : notnull =>
            dictionary.AddOrUpdate(key, value, (k, oldValue) => value);
    }
}