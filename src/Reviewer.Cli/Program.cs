using System.CommandLine;

var prUrl = new Argument<Uri>("pr-url")
{
    Description = "URL of the pull request to review",
    CustomParser = result =>
    {
        var value = result.Tokens.Single().Value;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
        {
            return uri;
        }

        result.AddError($"'{value}' is not a pull request URL.");
        return null;
    },
};
var dryRun = new Option<bool>("--dry-run") { Description = "Show the review plan without publishing it" };
var agent = new Option<string>("--agent") { Description = "Agent to use instead of the configured default", DefaultValueFactory = _ => "claude-code" };

var review = new Command("review", "Review one pull request now") { prUrl, dryRun, agent };
review.SetAction(parse =>
{
    Console.Error.WriteLine($"review {parse.GetValue(prUrl)} (agent: {parse.GetValue(agent)}, dry run: {parse.GetValue(dryRun)}) is not implemented yet.");
    return 1;
});

var status = new Command("status", "Show engine and agent health");
status.SetAction(_ =>
{
    Console.Error.WriteLine("status is not implemented yet.");
    return 1;
});

var root = new RootCommand("Local-first AI code reviewer") { review, status };
return await root.Parse(args).InvokeAsync();
