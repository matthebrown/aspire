// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Diagnostics;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Spectre.Console;

namespace Aspire.Cli.Commands;

internal sealed class PublishCommand : BaseCommand
{
    private readonly ActivitySource _activitySource = new ActivitySource(nameof(PublishCommand));
    private readonly IDotNetCliRunner _runner;

    public PublishCommand(IDotNetCliRunner runner)
        : base("publish", "Generates deployment artifacts for an Aspire app host project.")
    {
        ArgumentNullException.ThrowIfNull(runner, nameof(runner));
        _runner = runner;

        var projectOption = new Option<FileInfo?>("--project");
        projectOption.Description = "The path to the Aspire app host project file.";
        projectOption.Validators.Add(ProjectFileHelper.ValidateProjectOption);
        Options.Add(projectOption);

        var publisherOption = new Option<string>("--publisher", "-p");
        publisherOption.Description = "The name of the publisher to use.";
        Options.Add(publisherOption);

        var outputPath = new Option<string>("--output-path", "-o");
        outputPath.Description = "The output path for the generated artifacts.";
        outputPath.DefaultValueFactory = (result) => Path.Combine(Environment.CurrentDirectory);
        Options.Add(outputPath);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        (bool IsCompatibleAppHost, bool SupportsBackchannel, string? AspireHostingSdkVersion)? appHostCompatibilityCheck = null;

        try
        {
            using var activity = _activitySource.StartActivity();

            var passedAppHostProjectFile = parseResult.GetValue<FileInfo?>("--project");
            var effectiveAppHostProjectFile = ProjectFileHelper.UseOrFindAppHostProjectFile(passedAppHostProjectFile);
            
            if (effectiveAppHostProjectFile is null)
            {
                return ExitCodeConstants.FailedToFindProject;
            }

            var env = new Dictionary<string, string>();

            if (parseResult.GetValue<bool?>("--wait-for-debugger") ?? false)
            {
                env[KnownConfigNames.WaitForDebugger] = "true";
            }

            appHostCompatibilityCheck = await AppHostHelper.CheckAppHostCompatibilityAsync(_runner, effectiveAppHostProjectFile, cancellationToken);

            if (!appHostCompatibilityCheck?.IsCompatibleAppHost ?? throw new InvalidOperationException("IsCompatibleAppHost is null"))
            {
                return ExitCodeConstants.FailedToDotnetRunAppHost;
            }

            var buildExitCode = await AppHostHelper.BuildAppHostAsync(_runner, effectiveAppHostProjectFile, cancellationToken);

            if (buildExitCode != 0)
            {
                AnsiConsole.MarkupLine($"[red bold]:thumbs_down: The project could not be built. For more information run with --debug switch.[/]");
                return ExitCodeConstants.FailedToBuildArtifacts;
            }

            var publisher = parseResult.GetValue<string>("--publisher");
            var outputPath = parseResult.GetValue<string>("--output-path");
            var fullyQualifiedOutputPath = Path.GetFullPath(outputPath ?? ".");

            var publishersResult = await InteractionUtils.ShowStatusAsync<(int ExitCode, string[] Publishers)>(
                publisher is { } ? ":package:  Getting publisher..." : ":package:  Getting publishers...",
                async () => {
                    using var getPublishersActivity = _activitySource.StartActivity(
                        $"{nameof(ExecuteAsync)}-Action-GetPublishers",
                        ActivityKind.Client);

                    var backchannelCompletionSource = new TaskCompletionSource<AppHostBackchannel>();
                    var pendingInspectRun = _runner.RunAsync(
                        effectiveAppHostProjectFile,
                        false,
                        true,
                        ["--operation", "inspect"],
                        null,
                        backchannelCompletionSource,
                        cancellationToken).ConfigureAwait(false);

                    var backchannel = await backchannelCompletionSource.Task.ConfigureAwait(false);
                    var publishers = await backchannel.GetPublishersAsync(cancellationToken).ConfigureAwait(false);
                    
                    await backchannel.RequestStopAsync(cancellationToken).ConfigureAwait(false);
                    var exitCode = await pendingInspectRun;

                    return (exitCode, publishers);
                }
            );

            if (publishersResult.ExitCode != 0)
            {
                AnsiConsole.MarkupLine($"[red bold]:thumbs_down:  The publisher inspection failed with exit code {publishersResult.ExitCode}. For more information run with --debug switch.[/]");
                return ExitCodeConstants.FailedToBuildArtifacts;
            }

            var publishers = publishersResult.Publishers;
            if (publishers is null || publishers.Length == 0)
            {
                AnsiConsole.MarkupLine("[red bold]:thumbs_down:  No publishers were found.[/]");
                return ExitCodeConstants.FailedToBuildArtifacts;
            }

            if (publishers?.Contains(publisher) != true)
            {
                if (publisher is not null)
                {
                    AnsiConsole.MarkupLine($"[red bold]:warning:  The specified publisher '{publisher}' was not found.[/]");
                }

                var publisherPrompt = new SelectionPrompt<string>()
                    .Title("Select a publisher:")
                    .UseConverter(p => p)
                    .PageSize(10)
                    .EnableSearch()
                    .HighlightStyle(Style.Parse("darkmagenta"))
                    .AddChoices(publishers!);

                publisher = await AnsiConsole.PromptAsync(publisherPrompt, cancellationToken);
            }

            AnsiConsole.MarkupLine($":hammer_and_wrench:  Generating artifacts for '{publisher}' publisher...");

            var exitCode = await AnsiConsole.Progress()
                .AutoRefresh(true)
                .Columns(
                    new TaskDescriptionColumn() { Alignment = Justify.Left },
                    new ProgressBarColumn() { Width = 10 },
                    new ElapsedTimeColumn())
                .StartAsync(async context => {

                    using var generateArtifactsActivity = _activitySource.StartActivity(
                        $"{nameof(ExecuteAsync)}-Action-GenerateArtifacts",
                        ActivityKind.Internal);
                    
                    var backchannelCompletionSource = new TaskCompletionSource<AppHostBackchannel>();

                    var launchingAppHostTask = context.AddTask(":play_button:  Launching apphost");
                    launchingAppHostTask.IsIndeterminate();
                    launchingAppHostTask.StartTask();

                    var pendingRun = _runner.RunAsync(
                        effectiveAppHostProjectFile,
                        false,
                        true,
                        ["--publisher", publisher ?? "manifest", "--output-path", fullyQualifiedOutputPath],
                        env,
                        backchannelCompletionSource,
                        cancellationToken);

                    var backchannel = await backchannelCompletionSource.Task.ConfigureAwait(false);

                    launchingAppHostTask.Description = $":check_mark:  Launching apphost";
                    launchingAppHostTask.Value = 100;
                    launchingAppHostTask.StopTask();

                    var publishingActivities = backchannel.GetPublishingActivitiesAsync(cancellationToken);

                    var progressTasks = new Dictionary<string, ProgressTask>();

                    await foreach (var publishingActivity in publishingActivities)
                    {
                        if (!progressTasks.TryGetValue(publishingActivity.Id, out var progressTask))
                        {
                            progressTask = context.AddTask(publishingActivity.Id);
                            progressTask.StartTask();
                            progressTask.IsIndeterminate();
                            progressTasks.Add(publishingActivity.Id, progressTask);
                        }

                        progressTask.Description = $":play_button:  {publishingActivity.StatusText}";

                        if (publishingActivity.IsComplete && !publishingActivity.IsError)
                        {
                            progressTask.Description = $":check_mark:  {publishingActivity.StatusText}";
                            progressTask.Value = 100;
                            progressTask.StopTask();
                        }
                        else if (publishingActivity.IsError)
                        {
                            progressTask.Description = $"[red bold]:cross_mark:  {publishingActivity.StatusText}[/]";
                            progressTask.Value = 0;
                            break;
                        }
                        else
                        {
                            // Keep going man!
                        }
                    }

                    // When we are running in publish mode we don't want the app host to
                    // stop itself while we might still be streaming data back across
                    // the RPC backchannel. So we need to take responsibility for stopping
                    // the app host. If the CLI exits/crashes without explicitly stopping
                    // the app host the orphan detector in the app host will kick in.
                    if (progressTasks.Any(kvp => !kvp.Value.IsFinished))
                    {
                        // Depending on the failure the publisher may return a zero
                        // exit code.
                        await backchannel.RequestStopAsync(cancellationToken).ConfigureAwait(false);
                        var exitCode = await pendingRun;

                        // If we are in the state where we've detected an error because there
                        // is an incomplete task then we stop the app host, but depending on
                        // where/how the failure occured, we might still get a zero exit
                        // code. If we get a non-zero exit code we want to return that
                        // as it might be useful for diagnostic purposes, however if we don't
                        // get a non-zero exit code we want to return our built-in exit code
                        // for failed artifact build.
                        return exitCode == 0 ? ExitCodeConstants.FailedToBuildArtifacts : exitCode;
                    }
                    else
                    {
                        // If we are here then all the tasks are finished and we can
                        // stop the app host.
                        await backchannel.RequestStopAsync(cancellationToken).ConfigureAwait(false);
                        var exitCode = await pendingRun;
                        return exitCode; // should be zero for orderly shutdown but we pass it along anyway.
                    }
                });

            if (exitCode != 0)
            {
                AnsiConsole.MarkupLine($"[red bold]:thumbs_down: Publishing artifacts failed with exit code {exitCode}. For more information run with --debug switch.[/]");
                return ExitCodeConstants.FailedToBuildArtifacts;
            }
            else
            {
                AnsiConsole.MarkupLine($"[green bold]:thumbs_up: Successfully published artifacts to: {fullyQualifiedOutputPath}[/]");
                return ExitCodeConstants.Success;
            }
        }
        catch (AppHostIncompatibleException ex)
        {
            return InteractionUtils.DisplayIncompatibleVersionError(
                ex,
                appHostCompatibilityCheck?.AspireHostingSdkVersion ?? throw new InvalidOperationException("AspireHostingSdkVersion is null")
                );
        }
    }
}
