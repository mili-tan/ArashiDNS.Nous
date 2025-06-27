using ARSoft.Tools.Net;
using ARSoft.Tools.Net.Dns;
using MaxMind.GeoIP2;
using McMaster.Extensions.CommandLineUtils;
using NStack;
using System.Collections.Concurrent;
using System.Diagnostics;
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
        public static int LogLevel = 0; // 0: Error, 1: Info, 2: Debug
        public static IPAddress RegionalECS = IPAddress.Parse("123.123.123.0");

        public static ConcurrentDictionary<DomainName, string> DomainRegionMap = new();
        public static DnsQueryOptions QueryOptions = new()
        {
            IsEDnsEnabled = true,
            EDnsOptions = new OptRecord { Options = { new ClientSubnetOption(24, RegionalECS) } }
        };

        static void Main(string[] args)
        {

            var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "curl/8.5.0");

            foreach (var item in new HttpClient()
                         .GetStringAsync(
                             "https://fastly.jsdelivr.net/gh/felixonmars/dnsmasq-china-list@master/ns-whitelist.txt")
                         .Result.Split('\n'))
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(item) || item.StartsWith('#')) continue;
                    DomainRegionMap.TryAdd(DomainName.Parse(item.Trim().Trim('.')), "CN");
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
                    DomainRegionMap.TryAdd(DomainName.Parse(item.Trim().Trim('.')), "UN");
                    Console.WriteLine($"Add Ns Cache: {item.Trim().Trim('.')} -> UN");
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }
            }

            foreach (var item in new HttpClient()
                         .GetStringAsync("https://fastly.jsdelivr.net/gh/mili-tan/ArashiDNS.Nous@master/ns.csv").Result
                         .Split('\n'))
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(item) || item.StartsWith('#')) continue;
                    var i = item.Split(',');
                    DomainRegionMap.TryAdd(DomainName.Parse(i[0].Trim().Trim('.')), i[1]);
                    Console.WriteLine($"Add Ns Cache: {i[0].Trim().Trim('.')} -> {i[1]}");
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }
            }

            if (!File.Exists("./GeoLite2-Country.mmdb"))
            {
                Console.WriteLine("Downloading GeoLite2-Country.mmdb...");
                File.WriteAllBytes("./GeoLite2-Country.mmdb",
                    new HttpClient()
                        .GetByteArrayAsync(
                            "https://fastly.jsdelivr.net/gh/P3TERX/GeoLite.mmdb@download/GeoLite2-Country.mmdb")
                        .Result);
            }

            if (!File.Exists("./public_suffix_list.dat"))
            {
                Console.WriteLine("Downloading public_suffix_list.dat...");
                File.WriteAllBytes("./public_suffix_list.dat",
                    new HttpClient()
                        .GetByteArrayAsync(
                            "https://publicsuffix.org/list/public_suffix_list.dat")
                        .Result);
            }

            CountryReader = new DatabaseReader("./GeoLite2-Country.mmdb");
            TldExtract = new TldExtract("./public_suffix_list.dat");

            var cmd = new CommandLineApplication
            {
                Name = "ArashiDNS.Nous",
                Description = "ArashiDNS.Nous - ListFree Geo Diversion DNS Forwarder" +
                              Environment.NewLine +
                              $"Copyright (c) {DateTime.Now.Year} Milkey Tan. Code released under the FSL-1.1-ALv2 License"
            };
            cmd.HelpOption("-?|-h|--help");
            var wOption = cmd.Option<int>("-w <TimeOut>", "等待回复的超时时间（毫秒）。", CommandOptionType.SingleValue);
            var sOption = cmd.Option<string>("-s <IPEndPoint>", "设置的目标区域服务器的地址。[223.5.5.5:53]", CommandOptionType.SingleValue);
            var gOption = cmd.Option<string>("-g <IPEndPoint>", "设置的全局服务器地址。。[8.8.8.8:53]", CommandOptionType.SingleValue);
            var rOption = cmd.Option<string>("-r <Region>", "设置的目标区域。", CommandOptionType.SingleValue);
            var ecsOption = cmd.Option<string>("-e <IPAddress>", "设置目标区域 ECS 地址。", CommandOptionType.SingleValue);
            var lOption = cmd.Option<string>("-l <ListenerEndPoint>", "设置的监听地址。", CommandOptionType.SingleValue);

            cmd.OnExecute(() =>
            {
                if (wOption.HasValue()) TimeOut = wOption.ParsedValue;
                if (sOption.HasValue()) RegionalServer = IPEndPoint.Parse(sOption.ParsedValue);
                if (gOption.HasValue()) GlobalServer = IPEndPoint.Parse(gOption.ParsedValue);
                if (rOption.HasValue()) TargetRegion = rOption.ParsedValue;
                if (ecsOption.HasValue()) RegionalECS = IPAddress.Parse(ecsOption.ParsedValue);
                if (lOption.HasValue()) ListenerEndPoint = IPEndPoint.Parse(lOption.ParsedValue);

                if (RegionalServer.Port == 0) RegionalServer = new IPEndPoint(RegionalServer.Address, 53);
                if (GlobalServer.Port == 0) GlobalServer = new IPEndPoint(GlobalServer.Address, 53);
                if (ListenerEndPoint.Port == 0) ListenerEndPoint = new IPEndPoint(ListenerEndPoint.Address, 6653);

                QueryOptions = new DnsQueryOptions()
                {
                    IsEDnsEnabled = true,
                    EDnsOptions = new OptRecord {Options = {new ClientSubnetOption(24, RegionalECS)}}
                };

                var dnsServer = new DnsServer(new UdpServerTransport(ListenerEndPoint),
                    new TcpServerTransport(ListenerEndPoint));
                dnsServer.QueryReceived += DnsServerOnQueryReceived;
                dnsServer.Start();

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

            if (query.Questions.First().RecordClass == RecordClass.Chaos && query.Questions.First().RecordType == RecordType.Txt &&
                query.Questions.First().Name.IsEqualOrSubDomainOf(DomainName.Parse("version.bind")))
            {
                var msg = query.CreateResponseInstance();
                msg.IsRecursionAllowed = true;
                msg.IsRecursionDesired = true;
                msg.AnswerRecords.Add(
                    new TxtRecord(query.Questions.First().Name, 3600, "ArashiDNS.Aha"));
                e.Response = msg;
                return;
            }

            var questName = query.Questions.First().Name;
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

            Console.WriteLine(questExtract);
            var questRootName = DomainName.Parse(string.Join('.', string.IsNullOrWhiteSpace(questExtract.tld)
                ? questName.Labels.TakeLast(2)
                : [questExtract.root, questExtract.tld]));
            var response = query.CreateResponseInstance();
            var (rootNsIs,roorNs) = await FromNameGetNsIs(questRootName);

            if (LogLevel >= 1)
                Console.WriteLine("RootNAME Result: " + string.Join(" | ", questName.ToString(),
                    questRootName.ToString(), "-",
                    roorNs.ToString(), rootNsIs.ToString()));

            if (rootNsIs)
                response = await new DnsClient([RegionalServer.Address], [new UdpClientTransport(RegionalServer.Port)],queryTimeout: TimeOut).SendMessageAsync(query);
            else
            {
                response = await new DnsClient([GlobalServer.Address], [new UdpClientTransport(GlobalServer.Port)], queryTimeout: TimeOut).SendMessageAsync(query);
                if (response != null && response.AnswerRecords.Any(x => x.RecordType == RecordType.CName))
                {
                    var cName =
                        (response.AnswerRecords.LastOrDefault(x => x.RecordType == RecordType.CName) as CNameRecord)
                        ?.CanonicalName;
                    var cnameExtract = TldExtract.Extract(cName.ToString().Trim().Trim('.'));
                    var cnameRootName = DomainName.Parse(string.Join('.', string.IsNullOrWhiteSpace(cnameExtract.tld)
                        ? cName.Labels.TakeLast(2)
                        : [cnameExtract.root, cnameExtract.tld]));
                    var (cnameNsIs, cnameNs) = await FromNameGetNsIs(cnameRootName);

                    if (LogLevel >= 1)
                        Console.WriteLine("CNAME Result: " + string.Join(" | ", questName.ToString(), cName.ToString(), "-",
                            cnameRootName.ToString(), cnameNs.ToString(), "-", cnameNsIs.ToString()));

                    if (cnameNsIs)
                        response = await new DnsClient([RegionalServer.Address], [new UdpClientTransport(RegionalServer.Port)], queryTimeout: TimeOut).SendMessageAsync(query);
                }
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

                var findName = DomainRegionMap.Keys.FirstOrDefault(name.IsEqualOrSubDomainOf);
                if (findName != null)
                {
                    if (LogLevel >= 2)
                        Console.WriteLine($"Found Cache: {findName} -> {DomainRegionMap[findName]}");
                    return (string.Equals(DomainRegionMap[findName], TargetRegion, StringComparison.CurrentCultureIgnoreCase), findName);
                }

                var client = new DnsClient([GlobalServer.Address], [new UdpClientTransport(GlobalServer.Port)], queryTimeout: TimeOut);
                var nsMsg = await client.ResolveAsync(name, RecordType.Ns, options: QueryOptions);
                if (nsMsg == null || !nsMsg.AnswerRecords.Any()) nsMsg = await client.ResolveAsync(name, RecordType.Ns);
                if (LogLevel >= 2) Console.WriteLine("NS RCode: " + nsMsg.ReturnCode);

                var nsRecord = nsMsg.AnswerRecords.OrderBy(x => x.Name.Labels.First())
                    .FirstOrDefault(x => x.RecordType == RecordType.Ns);
                if (LogLevel >= 2) Console.WriteLine("NS Record: " + nsRecord);

                var nsName = (nsRecord as NsRecord)?.NameServer;

                if (nsName.ToString().Contains("awsdns-cn-"))
                {
                    if (LogLevel >= 2) Console.WriteLine($"Found AWSDNS-CN: {nsName} -> CN");
                    DomainRegionMap.TryAdd(name, "CN");
                    return (string.Equals("CN", TargetRegion, StringComparison.CurrentCultureIgnoreCase),
                        DomainName.Parse("awsdns-cn-1.com"));
                }
                if (nsName.ToString().Contains("awsdns-"))
                {
                    if (LogLevel >= 2) Console.WriteLine($"Found AWSDNS: {nsName} -> US");
                    DomainRegionMap.TryAdd(name, "US");
                    return (string.Equals("US", TargetRegion, StringComparison.CurrentCultureIgnoreCase), DomainName.Parse("awsdns-1.com"));
                }

                var findNs = DomainRegionMap.Keys.FirstOrDefault(nsName.IsEqualOrSubDomainOf);
                if (findNs != null)
                {
                    if (LogLevel >= 2) Console.WriteLine($"Found NS Cache: {findNs} -> {DomainRegionMap[findNs]}");
                    DomainRegionMap.TryAdd(name, DomainRegionMap[findNs]);
                    return (string.Equals(DomainRegionMap[findNs], TargetRegion, StringComparison.CurrentCultureIgnoreCase), findNs);
                }

                var nsAMsg = (await client.ResolveAsync(nsName, options: QueryOptions));
                if (nsAMsg == null || !nsAMsg.AnswerRecords.Any()) nsAMsg = await client.ResolveAsync(nsName);

                if (LogLevel >= 2) Console.WriteLine("NS-A RCode: " + nsAMsg.ReturnCode);
                if (LogLevel >= 2) Console.WriteLine("NS-A Record: " + nsAMsg.AnswerRecords.FirstOrDefault());


                var nsAddress = (nsAMsg.AnswerRecords.First(x => x.RecordType == RecordType.A) as ARecord)?.Address;
                var nsCountry = CountryReader.Country(nsAddress).Country.IsoCode ?? "UN";

                var nsExtract = TldExtract.Extract(nsName.ToString().Trim().Trim('.'));
                var nsRootName = DomainName.Parse(string.Join('.', string.IsNullOrWhiteSpace(nsExtract.tld)
                    ? nsName.Labels.TakeLast(2)
                    : [nsExtract.root, nsExtract.tld]));

                if (LogLevel >= 2) Console.WriteLine("NS GEO: " + nsCountry);
                DomainRegionMap.TryAdd(name, nsCountry);
                DomainRegionMap.TryAdd(nsRootName, nsCountry);
                return (string.Equals(nsCountry, TargetRegion, StringComparison.CurrentCultureIgnoreCase), nsName);
            }
            catch (Exception e)
            {
                if (LogLevel >= 0) Console.WriteLine("Error: " + e.Message);
                return (false, DomainName.Root);
            }
        }
    }
}
