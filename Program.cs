using ARSoft.Tools.Net;
using ARSoft.Tools.Net.Dns;
using MaxMind.GeoIP2;
using NStack;
using System;
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

        public static DnsQueryOptions QueryOptions = new DnsQueryOptions()
        {
            IsEDnsEnabled = true,
            EDnsOptions = new OptRecord { Options = { new ClientSubnetOption(24, RegionalECS) } }
        };

        static void Main(string[] args)
        {
            Console.Clear();
            if (!File.Exists("./GeoLite2-Country.mmdb"))
                File.WriteAllBytes("./GeoLite2-Country.mmdb",
                    new HttpClient()
                        .GetByteArrayAsync(
                            "https://github.com/mili-tan/maxmind-geoip/releases/latest/download/GeoLite2-Country.mmdb")
                        .Result);
            if (!File.Exists("./public_suffix_list.dat"))
                File.WriteAllBytes("./public_suffix_list.dat",
                    new HttpClient()
                        .GetByteArrayAsync(
                            "https://publicsuffix.org/list/public_suffix_list.dat")
                        .Result);

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
            var questName = query.Questions.First().Name;
            var questExtract = TldExtract.Extract(questName.ToString().Trim().Trim('.'));
            Console.WriteLine(questExtract);
            var questRootName = DomainName.Parse(string.Join('.', string.IsNullOrWhiteSpace(questExtract.tld)
                ? questName.Labels.TakeLast(2)
                : [questExtract.root, questExtract.tld]));
            var response = query.CreateResponseInstance();
            var (rootNsIs,roorNs) = await FromNameGetNsIs(questRootName);

            Console.WriteLine(string.Join(" | ", questName.ToString(), questRootName.ToString(), "-", roorNs.ToString(), rootNsIs.ToString()));

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
                    var cnameRootName = DomainName.Parse(string.Join('.', cnameExtract.root, cnameExtract.tld));
                    var (cnameNsIs, cnameNs) = await FromNameGetNsIs(cnameRootName);

                    Console.WriteLine(string.Join(" | ",
                        new List<string>()
                        {
                            questName.ToString(), cName.ToString(), "-", cnameRootName.ToString(), cnameNs.ToString(), "-",
                            cnameNsIs.ToString()
                        }));

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
                Console.WriteLine(name.ToString());
                var client = new DnsClient([GlobalServer.Address], [new UdpClientTransport(GlobalServer.Port)], queryTimeout: TimeOut);
                var nsMsg = await client.ResolveAsync(name, RecordType.Ns, options: QueryOptions);
                if (nsMsg == null || !nsMsg.AnswerRecords.Any()) nsMsg = await client.ResolveAsync(name, RecordType.Ns);
                Console.WriteLine(nsMsg.ReturnCode);

                var nsRecord = nsMsg.AnswerRecords.OrderByDescending(x => x.Name)
                    .FirstOrDefault(x => x.RecordType == RecordType.Ns);
                Console.WriteLine(nsRecord);

                var nsName = (nsRecord as NsRecord)?.NameServer;
                var nsAMsg = (await client.ResolveAsync(nsName, options: QueryOptions));
                if (nsAMsg == null || !nsAMsg.AnswerRecords.Any()) nsAMsg = await client.ResolveAsync(nsName);
                Console.WriteLine(nsAMsg.ReturnCode);
                Console.WriteLine(nsAMsg.AnswerRecords.FirstOrDefault());
                var nsAddress = (nsAMsg.AnswerRecords.First(x => x.RecordType == RecordType.A) as ARecord)?.Address;
                Console.WriteLine(CountryReader.Country(nsAddress).Country.IsoCode ?? "UN");
                return (string.Equals(CountryReader.Country(nsAddress).Country.IsoCode, TargetRegion,
                    StringComparison.CurrentCultureIgnoreCase), nsName);
            }
            catch (Exception e)
            {
                return (false, DomainName.Root);
            }
        }
    }
}
