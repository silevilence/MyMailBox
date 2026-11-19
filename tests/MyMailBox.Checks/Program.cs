using CordiSharp;
using MyMailBox.Builtins;
using MyMailBox.Contracts;
using MyMailBox.Runtime;

if (args is ["pst-worker", var pstWorkspace, var pstAccount]) { await PstImportChecks.CrashWorker(pstWorkspace, Guid.Parse(pstAccount)); return; }

if(args.Length==2 && args[0]=="owner-probe"){WorkspaceOwnerChecks.TryOpen(args[1]);return;}
if (args.Length==2 && args[0]=="perf-prepare-ui" && int.TryParse(args[1],out var uiSize) && uiSize is 10000 or 100000)
{await PerformanceFixture.PrepareUi(uiSize);return;}
if (args.Length==2 && args[0]=="perf-index" && int.TryParse(args[1],out var indexSize) && indexSize is 10000 or 100000)
{await PerformanceFixture.Index(indexSize);return;}
if (args.Length==4 && args[0]=="perf-measure" && int.TryParse(args[1],out var measureSize) && measureSize is 10000 or 100000 && args[2] is "indexed" or "source" && args[3] is "sync" or "idle")
{await PerformanceFixture.Measure(measureSize,args[2],args[3]=="sync");return;}
if (args.Length==2 && args[0]=="perf-prepare" && int.TryParse(args[1],out var size) && size is 10000 or 100000)
{await PerformanceFixture.Prepare(size);return;}
if (args.Length == 3 && args[0] == "draft-crash-worker") { ComposerChecks.Crash(args[1],Guid.Parse(args[2]));return; }
if (args.Length == 2 && args[0] == "rule-crash-worker") { await ReceiveRuleChecks.Crash(args[1]); return; }
if (args.Length == 4 && args[0] == "transfer-crash-worker") { await TransferChecks.CrashWorker(args[1], Guid.Parse(args[2]), args[3]); return; }
if (args.Length > 0 && args[0] == "crash-writer") { StorageChecks.CrashWriter(args[1], int.Parse(args[2]), args[3]); return; }
if (args.Length > 0 && args[0] == "schedule-crash-worker") { await ScheduleRecoveryChecks.CrashWorker(args[1], args[2]); return; }
// Every full or targeted run uses disposable test data, even when the caller has a real workspace configured.
Environment.SetEnvironmentVariable("MYMAILBOX_HOME", Path.Combine(PluginChecks.Repository, "artifacts/checks/default-" + Guid.NewGuid().ToString("N")));
var context = Context.Create();
var pages = new Contributions();
context.Provide<IContributions>("mailbox.ui", pages);
await context.Inject(["mailbox.ui"], (ctx, _) => { WelcomePlugin.Apply(ctx); return null; });
if (pages.Pages.Single().Id != "welcome") throw new Exception("Welcome was not contributed");
await context.Fiber.DisposeAsync();
if (pages.Pages.Count != 0) throw new Exception("Welcome registration leaked");
Console.WriteLine("PASS bootstrap: CordiSharp welcome activation and cleanup");
if (args.Length == 0 || args.Contains("plugins")) await PluginChecks.Run();
if (args.Length == 0 || args.Contains("profiles")) await ProfileChecks.Run();
if (args.Length == 0 || args.Contains("configuration")) await ConfigurationChecks.Run();
if (args.Length == 0 || args.Contains("prototype")) await PrototypeChecks.Run();
if (args.Length == 0 || args.Contains("contributions")) await ContributionChecks.Run();
if (args.Length == 0 || args.Contains("packages")) await PackageChecks.Run();
if (args.Length == 0 || args.Contains("upgrade")) await UpgradeChecks.Run();
if (args.Length == 0 || args.Contains("diagnostics")) await DiagnosticChecks.Run();
if (args.Length == 0 || args.Contains("management")) await ManagementChecks.Run();
if (args.Length == 0 || args.Contains("mail")) await MailChecks.Run();
if (args.Contains("pst-import")) await PstImportChecks.Run();
if (args.Contains("pst-resume")) await PstImportChecks.Recovery();
if (args.Length == 0 || args.Contains("import-plugin")) await ImportPluginChecks.Run();
if (args.Contains("pst-fidelity")) await PstFidelityChecks.Run();
if (args.Length == 0 || args.Contains("exchange-eas")) await ExchangeChecks.Run();
if (args.Length == 0 || args.Contains("settings-templates")) await SettingsTemplateChecks.Run();
if (args.Length == 0 || args.Contains("outlook-rules")) await OutlookRuleChecks.Run();
if (args.Length == 0 || args.Contains("outlook-import")) await OutlookImportChecks.Run();
if (args.Length == 0 || args.Contains("accounts")) await AccountChecks.Run();
if (args.Length == 0 || args.Contains("netease-preset")) await ProtocolChecks.Preset("netease-mail");
if (args.Length == 0 || args.Contains("qq-preset")) await ProtocolChecks.Preset("qq-mail");
if (args.Length == 0 || args.Contains("protocols")) await ProtocolChecks.Run();
if (args.Length == 0 || args.Contains("graph")) await GraphChecks.Run();
if (args.Length == 0 || args.Contains("graph-write")) await GraphChecks.Writes();
if (args.Length == 0 || args.Contains("graph-delta")) await GraphChecks.Delta();
if (args.Length == 0 || args.Contains("outbox")) await ProtocolChecks.Outbox();
if (args.Length == 0 || args.Contains("mailbox-provider")) await MailboxProviderChecks.Run();
if (args.Length == 0 || args.Contains("multi-sync")) await MultiSyncChecks.Run();
if (args.Length == 0 || args.Contains("organization")) await OrganizationChecks.Run(false);
if (args.Length == 0 || args.Contains("mail-actions")) await MailActionChecks.Run(false);
if (args.Length == 0 || args.Contains("transfer-recovery")) await TransferChecks.Recovery();
if (args.Contains("transfer-live")) await TransferChecks.Live();
if (args.Contains("mail-actions-live")) await MailActionChecks.Run(true);
if (args.Contains("organization-live")) await OrganizationChecks.Run(true);
if (args.Length == 0 || args.Contains("search-conditions")) await SearchChecks.Conditions();
if (args.Length == 0 || args.Contains("search-index")) await SearchChecks.Index();
if (args.Length == 0 || args.Contains("search-builder")) await SearchChecks.Builder();
if (args.Length == 0 || args.Contains("search-extension")) await SearchChecks.Extension();
if (args.Length == 0 || args.Contains("workspace-owner")) await WorkspaceOwnerChecks.Run();
if (args.Length == 0 || args.Contains("activity")) await ActivityChecks.Run();
if (args.Length == 0 || args.Contains("task-history")) await AutomationChecks.History();
if (args.Length == 0 || args.Contains("task-plugins")) await AutomationChecks.Plugins();
if (args.Contains("task-events-live")) await AutomationChecks.Events(true);
if (args.Length == 0 || args.Contains("task-events")) await AutomationChecks.Events(false);
if (args.Length == 0 || args.Contains("automatic-mail")) await AutomationChecks.AutomaticMail();
if (args.Contains("automatic-mail-live")) await AutomationChecks.AutomaticMailLive();
if (args.Length == 0 || args.Contains("task-actions")) await AutomationChecks.Actions();
if (args.Contains("task-archive-live")) await AutomationChecks.ArchiveLive();
if (args.Length == 0 || args.Contains("task-archive")) await AutomationChecks.Archive();
if (args.Length == 0 || args.Contains("task-targets")) await AutomationChecks.Targets();
if (args.Length == 0 || args.Contains("task-conditions")) await AutomationChecks.Conditions();
if (args.Length == 0 || args.Contains("rule-stages")) await ReceiveRuleChecks.Stages();
if (args.Length == 0 || args.Contains("rule-preview")) await ReceiveRuleChecks.Preview();
if (args.Length == 0 || args.Contains("send-plan")) await ComposerChecks.SendPlans();
if (args.Contains("send-plan-live")) await ComposerChecks.SendPlanLive();
if (args.Length == 0 || args.Contains("address-book")) await ComposerChecks.Addresses();
if (args.Length == 0 || args.Contains("replies")) await ComposerChecks.Replies();
if (args.Length == 0 || args.Contains("compose-parts")) await ComposerChecks.Parts();
if (args.Contains("compose-live")) await ComposerChecks.PartsLive();
if (args.Length == 0 || args.Contains("drafts")) await ComposerChecks.Drafts();
if (args.Length == 0 || args.Contains("receive-rules")) await ReceiveRuleChecks.MainRules();
if (args.Length == 0 || args.Contains("search-fields")) await SearchChecks.Fields();
if (args.Contains("imap-conflicts")) await ConflictChecks.Run();
if (args.Contains("imap-sync")) await ProtocolChecks.ImapSync();
if (args.Contains("protocol-live")) await ProtocolChecks.Live();
if (args.Contains("protocol-faults")) await ProtocolChecks.Faults();
if (args.Length == 0 || args.Contains("selectors")) await SelectionChecks.Run();
if (args.Length == 0 || args.Contains("mime")) await MimeChecks.Run();
if (args.Length == 0 || args.Contains("storage")) await StorageChecks.Run();
if (args.Length == 0 || args.Contains("backup")) await BackupChecks.Run();
if (args.Length == 0 || args.Contains("export")) await ExportChecks.Run();
if (args.Length == 0 || args.Contains("archive")) await ArchiveChecks.Run();
if (args.Length == 0 || args.Contains("html")) await HtmlChecks.Run();
if (args.Length == 0 || args.Contains("remote")) await RemoteContentChecks.Run();
if (args.Length == 0 || args.Contains("attachments")) await AttachmentChecks.Run();
if (args.Length == 0 || args.Contains("encoding")) await EncodingChecks.Run();
if (args.Length == 0 || args.Contains("bulk-export")) await BulkExportChecks.Run();
if (args.Length == 0 || args.Contains("local-mailboxes")) await LocalMailboxChecks.Run();
if (args.Length == 0 || args.Contains("readable-export")) await ReadableExportChecks.Run();
if (args.Length == 0 || args.Contains("reminders")) await ReminderChecks.Run();
if (args.Length == 0 || args.Contains("recurrence")) await RecurrenceChecks.Run();
if (args.Length == 0 || args.Contains("cron")) await CronChecks.Run();
if (args.Length == 0 || args.Contains("schedule-recovery")) await ScheduleRecoveryChecks.Run();
if (args.Length == 0 || args.Contains("updater-spike")) await UpdaterSpikeChecks.Run();
if (args.Length == 0 || args.Contains("update-checks")) await UpdateCheckChecks.Run();
if (args.Contains("update-live")) await UpdateCheckChecks.Live();
if (args.Length == 0 || args.Contains("update-install")) await UpdateInstallChecks.Run();
if (args.Length == 0 || args.Contains("release-signing")) await ReleaseSigningChecks.Run();
