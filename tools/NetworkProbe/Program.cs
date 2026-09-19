using OBDim.NetworkProbe;

// N0 platform capability probe for the OB Dim network module.
// Read-only by design: it never creates ETW sessions, never alters firewall rules,
// never touches connections. Exit codes: 0 = success, 2 = usage error,
// 3 = contract validation failure (validate mode), 1 = probe error.
return args.Length == 0
    ? Usage()
    : args[0] switch
    {
        "interfaces" => InterfaceProbe.Run(),
        "connections" => ConnectionTableProbe.Run(),
        "process" => ProcessProbe.Run(),
        "perf" => PerfProbe.Run(),
        "validate" when args.Length >= 2 => SnapshotValidator.Run(args[1]),
        _ => Usage(),
    };

static int Usage()
{
    Console.Error.WriteLine(
        "usage: NetworkProbe <interfaces|connections|process|perf|all> | validate <snapshot.json>");
    return 2;
}
