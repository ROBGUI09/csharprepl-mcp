using Mykeels.CSharpRepl;
using Mykeels.CSharpRepl.Sample;
using Microsoft.Extensions.Configuration;

var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddUserSecrets<Program>()
    .AddEnvironmentVariables()
    .Build();

if (args.Contains("repl"))
{
    await Repl.Run(
        commands: [
            "using static Mykeels.CSharpRepl.Sample.ScriptGlobals;",
            "using Mykeels.CSharpRepl.Sample;"
        ]
    );
}
else if (args.Contains("slack"))
{
    await SlackReplHost.Run(
        new SlackReplOptions
        {
            BotToken = RequireConfiguration("Slack:BotToken"),
            AppToken = RequireConfiguration("Slack:AppToken"),
            // Fails closed if left unset — see SlackReplOptions.AllowedUserIds.
            AllowedChannelIds = RequireConfiguration("Slack:AllowedChannelIds")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(),
        },
        commands: [
            "using static Mykeels.CSharpRepl.Sample.ScriptGlobals;",
            "using Mykeels.CSharpRepl.Sample;"
        ]
    );
}
else
{
    // Default command is the stdio MCP server so existing client configs keep working.
    // Extra arguments are forwarded: --preload <dll|dir> --package <id@ver> --using <ns> ...
    await McpServer.RunStandalone(args, typeof(ScriptGlobals));
}

string RequireConfiguration(string name) =>
    configuration[name]
    ?? throw new InvalidOperationException($"Set the {name} configuration to run the Slack sample.");

