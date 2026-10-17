using Mirror.Relay.Server;

int udpPort = 9000;
int httpPort = 9001;
string relayAddress = "127.0.0.1";
string? controlFile = null;

for (int index = 0; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--udp":
            if (index + 1 >= args.Length || !int.TryParse(args[++index], out udpPort))
                return Usage();
            break;
        case "--http":
            if (index + 1 >= args.Length || !int.TryParse(args[++index], out httpPort))
                return Usage();
            break;
        case "--relay-address":
            if (index + 1 >= args.Length)
                return Usage();
            relayAddress = args[++index];
            break;
        case "--control-file":
            if (index + 1 >= args.Length) return Usage();
            controlFile = args[++index];
            break;
        case "--help":
        case "-h":
            return Usage();
        default:
            // Preserve the original positional port form.
            if (index == 0 && int.TryParse(args[index], out int positionalPort))
                udpPort = positionalPort;
            else
                return Usage();
            break;
    }
}

if (udpPort is < 1 or > 65535 || httpPort is < 1 or > 65535)
    return Usage();

RelayRoomRegistry registry = new(relayAddress, checked((ushort)udpPort));
using RelayUdpServer udp = new(udpPort, registry: registry);
using RelayHttpServer http = new(registry, udp, $"http://0.0.0.0:{httpPort}");
udp.Start();
http.Start();

Console.WriteLine($"Mirror relay UDP listening on {udpPort}.");
Console.WriteLine($"Mirror lobby HTTP listening on {httpPort}.");
Console.WriteLine("Press Ctrl+C to stop.");

using ManualResetEventSlim quit = new(false);
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    quit.Set();
};
string? previousControl = null;
while (!quit.Wait(100))
{
    // Optional local fault-injection input for the acceptance harness.
    if (controlFile == null || !File.Exists(controlFile)) continue;
    try
    {
        string control = File.ReadAllText(controlFile).Trim();
        if (control == previousControl) continue;
        previousControl = control;
        switch (control)
        {
            case "http-off": http.Stop(); break;
            case "http-on": http.Start(); break;
            case "udp-off": udp.Stop(); break;
            case "udp-on": udp.Start(); break;
        }
    }
    catch (IOException) { }
}
return 0;

static int Usage()
{
    Console.Error.WriteLine("Usage: Mirror.RelayServer [udpPort] [--udp <port>] [--http <port>] [--relay-address <host>] [--control-file <local-test-file>]");
    return 2;
}
