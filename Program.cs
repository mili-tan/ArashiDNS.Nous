using ARSoft.Tools.Net;
using ARSoft.Tools.Net.Dns;
using MaxMind.GeoIP2;
using NStack;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using IPAddress = System.Net.IPAddress;

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
        public static IPAddress RegionalECS = IPAddress.Parse("123.123.123.0");
        public static int TimeOut = 3000;

        public static ConcurrentDictionary<DomainName, string> DomainRegionMap = new();

        public static DnsQueryOptions QueryOptions = new DnsQueryOptions()
        {
            IsEDnsEnabled = true,
            EDnsOptions = new OptRecord { Options = { new ClientSubnetOption(24, RegionalECS) } }
        };

        static void Main(string[] args)
        {
            Console.Clear();

            Console.WriteLine("ArashiDNS Nous - Experimental");

            var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "curl/8.5.0");

            foreach (var item in new HttpClient().GetStringAsync("https://fastly.jsdelivr.net/gh/felixonmars/dnsmasq-china-list@master/ns-whitelist.txt").Result.Split('\n'))
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

            foreach (var item in new HttpClient().GetStringAsync("https://fastly.jsdelivr.net/gh/felixonmars/dnsmasq-china-list@master/ns-blacklist.txt").Result.Split('\n'))
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

            Console.WriteLine("RootNAME Result: " + string.Join(" | ", questName.ToString(), questRootName.ToString(), "-",
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

                    Console.WriteLine("CNAME Result: " + string.Join(" | ", questName.ToString(), cName.ToString(), "-",
                        cnameRootName.ToString(), cnameNs.ToString(), "-", cnameNsIs.ToString()));

                    if (cnameNsIs)
                        response = await new DnsClient([RegionalServer.Address], [new UdpClientTransport(RegionalServer.Port)], queryTimeout: TimeOut).SendMessageAsync(query);
                }
            }
            Console.WriteLine("-----------------------------------");
            e.Response = response;
        }

        private static async Task<(bool isRegion, DomainName ns)> FromNameGetNsIs(DomainName name)
        {
            try
            {
                var findName = DomainRegionMap.Keys.FirstOrDefault(name.IsEqualOrSubDomainOf);
                if (findName != null)
                {
                    Console.WriteLine($"Found Cache: {findName} -> {DomainRegionMap[findName]}");
                    return (string.Equals(DomainRegionMap[findName], TargetRegion, StringComparison.CurrentCultureIgnoreCase), findName);
                }

                Console.WriteLine("Root Name: " + name);
                var client = new DnsClient([GlobalServer.Address], [new UdpClientTransport(GlobalServer.Port)], queryTimeout: TimeOut);
                var nsMsg = await client.ResolveAsync(name, RecordType.Ns, options: QueryOptions);
                if (nsMsg == null || !nsMsg.AnswerRecords.Any()) nsMsg = await client.ResolveAsync(name, RecordType.Ns);
                Console.WriteLine("NS RCode: " + nsMsg.ReturnCode);


                var nsRecord = nsMsg.AnswerRecords.OrderBy(x => x.Name.Labels.First())
                    .FirstOrDefault(x => x.RecordType == RecordType.Ns);

                Console.WriteLine("NS Record: " + nsRecord);

                var nsName = (nsRecord as NsRecord)?.NameServer;
                var findNs = DomainRegionMap.Keys.FirstOrDefault(nsName.IsEqualOrSubDomainOf);
                if (findNs != null)
                {
                    Console.WriteLine($"Found NS Cache: {findNs} -> {DomainRegionMap[findNs]}");
                    DomainRegionMap.TryAdd(name, DomainRegionMap[findNs]);
                    return (string.Equals(DomainRegionMap[findNs], TargetRegion, StringComparison.CurrentCultureIgnoreCase), findNs);
                }

                var nsAMsg = (await client.ResolveAsync(nsName, options: QueryOptions));
                if (nsAMsg == null || !nsAMsg.AnswerRecords.Any()) nsAMsg = await client.ResolveAsync(nsName);

                Console.WriteLine("NS-A RCode: " + nsAMsg.ReturnCode);
                Console.WriteLine("NS-A Record: " + nsAMsg.AnswerRecords.FirstOrDefault());

                var nsAddress = (nsAMsg.AnswerRecords.First(x => x.RecordType == RecordType.A) as ARecord)?.Address;
                var nsCountry = CountryReader.Country(nsAddress).Country.IsoCode ?? "UN";

                Stopwatch sp = Stopwatch.StartNew();
                var nsExtract = TldExtract.Extract(nsName.ToString().Trim().Trim('.'));
                sp.Stop();
                Console.WriteLine($"TLD Extract Time: {sp.ElapsedMilliseconds}ms");
                var nsRootName = DomainName.Parse(string.Join('.', string.IsNullOrWhiteSpace(nsExtract.tld)
                    ? nsName.Labels.TakeLast(2)
                    : [nsExtract.root, nsExtract.tld]));

                Console.WriteLine(nsCountry);
                DomainRegionMap.TryAdd(name, nsCountry);
                DomainRegionMap.TryAdd(nsRootName, nsCountry);
                return (string.Equals(nsCountry, TargetRegion, StringComparison.CurrentCultureIgnoreCase), nsName);
            }
            catch (Exception e)
            {
                return (false, DomainName.Root);
            }
        }
    }
}
