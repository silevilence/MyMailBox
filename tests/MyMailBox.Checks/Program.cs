using CordiSharp;
using MyMailBox.Builtins;
using MyMailBox.Contracts;
using MyMailBox.Runtime;

var context = Context.Create();
var pages = new Contributions();
context.Provide<IContributions>("mailbox.ui", pages);
await context.Inject(["mailbox.ui"], (ctx, _) => { WelcomePlugin.Apply(ctx); return null; });
if (pages.Pages.Single().Id != "welcome") throw new Exception("Welcome was not contributed");
await context.Fiber.DisposeAsync();
if (pages.Pages.Count != 0) throw new Exception("Welcome registration leaked");
Console.WriteLine("PASS bootstrap: CordiSharp welcome activation and cleanup");
if (args.Length == 0 || args.Contains("plugins")) await PluginChecks.Run();
