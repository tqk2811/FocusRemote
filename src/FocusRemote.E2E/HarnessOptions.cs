namespace FocusRemote.E2E;

/// <summary>Command line of the E2E harness.</summary>
internal sealed record HarnessOptions(string Id, string Password, string? RelayUrl, int? LanPort, bool RealInput)
{
    /// <summary>Returns null when the arguments are invalid (needs id, password and at least one of relay/LAN).</summary>
    public static HarnessOptions? Parse(string[] args)
    {
        string? id = null, password = null, relay = null;
        int? lanPort = null;
        bool realInput = false;
        for (int i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--id":
                    id = Next();
                    break;
                case "--password":
                    password = Next();
                    break;
                case "--relay":
                    relay = Next();
                    break;
                case "--lan-port":
                    if (!int.TryParse(Next(), out int port))
                        return null;
                    lanPort = port;
                    break;
                case "--real-input":
                    realInput = true;
                    break;
                default:
                    return null;
            }
        }
        if (id is null || password is null || (relay is null && lanPort is null))
            return null;
        return new HarnessOptions(id, password, relay, lanPort, realInput);
    }
}
