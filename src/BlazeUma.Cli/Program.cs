using BlazeUma.Cli;

// BlazeUma.exe: an interactive console app. The main menu picks the mode: Advisor (you play, it recommends)
// or Auto bot (it plays; Insert pauses, Page Down stops). The command-line uses are `replay`
// (see ReplayCommand) and `event-odds` (see EventOddsCommand).

if (args is ["replay", .. var replayArgs]) return ReplayCommand.Run(replayArgs);
if (args is ["event-odds", .. var oddsArgs]) return EventOddsCommand.Run(oddsArgs);
if (args is ["buttons", .. var buttonArgs]) return ButtonsCommand.Run(buttonArgs);
if (args is ["fields", .. var fieldArgs]) return FieldsCommand.Run(fieldArgs);
if (args is ["uistate", .. var uiArgs]) return UiStateCommand.Run(uiArgs);

ConsoleUi.Setup();
using var control = new BotControl();
var bot = new AutoBot(control);

try
{
    var mode = Mode.Menu;
    while (mode != Mode.Exit)
    {
        mode = mode switch
        {
            Mode.Advisor => AdvisorMode.Run(),
            Mode.AutoBot => bot.Run(),
            _ => MainMenu(bot.LastCareer),
        };
    }
    Console.Clear();
    return 0;
}
catch (Exception e)
{
    // Started by double-click, the window would close with the error; keep it readable.
    Console.WriteLine();
    Console.WriteLine(ConsoleUi.Warn("  BlazeUma hit an unexpected error:"));
    Console.WriteLine("  " + e);
    ConsoleUi.WaitForKey("Press any key to exit");
    return 1;
}

static Mode MainMenu(CompletedCareer? last)
{
    ConsoleUi.Banner("Main menu");
    if (last is not null)
    {
        Console.WriteLine("  " + ConsoleUi.Accent("Career completed"));
        foreach (string line in last.Describe().Split('\n')) Console.WriteLine("  " + line);
        Console.WriteLine();
    }
    Console.WriteLine("  Welcome! Start a career in the game, then choose how BlazeUma should help.");
    Console.WriteLine();
    int choice = ConsoleUi.Choose("What would you like to do?",
    [
        new("Advisor", "you play; BlazeUma reads the game and recommends every move"),
        new("Auto bot", "BlazeUma plays the career for you · Insert pauses, Page Down stops"),
        new("Exit"),
    ], escape: 2);
    return choice switch { 0 => Mode.Advisor, 1 => Mode.AutoBot, _ => Mode.Exit };
}
