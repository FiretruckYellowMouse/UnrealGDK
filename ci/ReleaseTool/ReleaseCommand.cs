using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CommandLine;
using Octokit;

namespace ReleaseTool
{
    /// <summary>
    ///     Runs the commands required for releasing a candidate.
    ///     * Merges the candidate branch into the release branch.
    ///     * Pushes the release branch.
    ///     * Creates a GitHub release draft.
    ///     * Creates a PR from the release branch into the master branch.
    /// </summary>
    internal class ReleaseCommand
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        // TODO: Possibly change these PR messages.
        private const string PullRequestNameTemplate = "Release {0} - Merge release into master";
        private const string pullRequestBody = "Merging the release branch into master. This may include version updates.";

        private const string ChangeLogFilename = "CHANGELOG.md";

        [Verb("release", HelpText = "Merge a release branch and create a github release draft.")]
        public class Options : GitHubClient.IGitHubOptions, BuildkiteMetadataSink.IBuildkiteOptions
        {
            [Value(0, MetaName = "version", HelpText = "The version that is being released.")]
            public string Version { get; set; }

            [Option('u', "pull-request-url", HelpText = "The link to the release candidate branch to merge.",
                Required = true)]
            public string PullRequestUrl { get; set; }

            [Option("source-branch", HelpText = "The source branch name from which we are cutting the candidate.", Required = true)]
            public string SourceBranch { get; set; }

            [Option("candidate-branch", HelpText = "The candidate branch name.", Required = true)]
            public string CandidateBranch { get; set; }

            [Option("release-branch", HelpText = "The name of the branch into which we are merging the candidate.", Required = true)]
            public string ReleaseBranch { get; set; }

            public string GitHubTokenFile { get; set; }

            public string GitHubToken { get; set; }

            public string MetadataFilePath { get; set; }
        }

        private readonly Options options;

        public ReleaseCommand(Options options)
        {
            this.options = options;
        }

        /*
         *     This tool is designed to execute most of the git operations required when releasing:
         *         1. Merge the RC PR into the release branch.
         *         2. Draft a GitHub release using the changelog notes.
         *         3. Open a PR from the release branch into master
         */
        public int Run()
        {
            Common.VerifySemanticVersioningFormat(options.Version);

            try
            {
                var gitHubClient = new GitHubClient(options);

                var (repoName, pullRequestId) = ExtractPullRequestInfo(options.PullRequestUrl);

                var spatialOsRemote = string.Format(Common.RemoteUrlTemplate, Common.SpatialOsOrg, repoName);
                var gitHubRepo = gitHubClient.GetRepositoryFromRemote(spatialOsRemote);

                // Merge into release
                var mergeResult = gitHubClient.MergePullRequest(gitHubRepo, pullRequestId);

                if (!mergeResult.Merged)
                {
                    throw new InvalidOperationException(
                        $"Was unable to merge pull request at: {options.PullRequestUrl}. Received error: {mergeResult.Message}");
                }

                // Delete remote on the forked repository.
                var forkedRepoRemote = string.Format(Common.RemoteUrlTemplate, Common.GithubBotUser, repoName);
                gitHubClient.DeleteBranch(gitHubClient.GetRepositoryFromRemote(forkedRepoRemote), options.CandidateBranch);

                var remoteUrl = string.Format(Common.RemoteUrlTemplate, Common.SpatialOsOrg, repoName);

                using (var gitClient = GitClient.FromRemote(remoteUrl))
                {
                    // Create GitHub release in the repo
                    gitClient.Fetch();
                    gitClient.CheckoutRemoteBranch(options.ReleaseBranch);
                    var release = CreateRelease(gitHubClient, gitHubRepo, gitClient, repoName);

                    Logger.Info("Release Successful!");
                    Logger.Info("Release hash: {0}", gitClient.GetHeadCommit().Sha);
                    Logger.Info("Draft release: {0}", release.HtmlUrl);
                }

                // Open a PR for merging the release branch into master.
                var branchFrom = $"{Common.GithubBotUser}:{options.ReleaseBranch}";
                var branchTo = options.SourceBranch;

                // Only open a PR if one does not exist yet.
                if (!gitHubClient.TryGetPullRequest(gitHubRepo, branchFrom, branchTo, out var pullRequest))
                {
                    pullRequest = gitHubClient.CreatePullRequest(gitHubRepo,
                        branchFrom,
                        branchTo,
                        string.Format(PullRequestNameTemplate, options.Version),
                        pullRequestBody);
                }

                Logger.Info("Pull request available: {0}", pullRequest.HtmlUrl);
                Logger.Info("Successfully created PR for merging the release into master!");
                Logger.Info("Merge hash: {0}", pullRequest.MergeCommitSha);
            }
            catch (Exception e)
            {
                Logger.Error(e, "ERROR: Unable to release candidate branch or merge the release branch back into master. Error: {0}", e);
                return 1;
            }

            return 0;
        }

        private Release CreateRelease(GitHubClient gitHubClient, Repository gitHubRepo, GitClient gitClient, string repoName)
        {
            var headCommit = gitClient.GetHeadCommit().Sha;

            string name;
            string releaseBody;

            // TODO: Modify release bodies for the UnrealGDK repos
            switch (repoName)
            {
                case "UnrealGDK":
                    string changelog;
                    using (new WorkingDirectoryScope(gitClient.RepositoryPath))
                    {
                        changelog = GetReleaseNotesFromChangeLog();
                    }
                    name = $"GDK for Unity Alpha Release {options.Version}";
                    releaseBody =
$@"In this release, we've ...

We've also fixed ... 

Keep giving us your feedback and/or suggestions! Check out [our Discord](https://discord.gg/SCZTCYm), [our forums](https://forums.improbable.io/), or here in the [Github issues](https://github.com/spatialos/gdk-for-unity/issues?q=is%3Aissue+is%3Aopen+sort%3Aupdated-desc)!

See the full release notes below! 👇

---

{changelog}";
                    break;
                case "UnrealEngine":
                    name = $"GDK for Unity FPS Starter Project Alpha Release {options.Version}";
                    releaseBody =
$@"This release of the FPS Starter Project is intended for use with the GDK for Unity Alpha Release {options.Version}.

Keep giving us your feedback and/or suggestions! Check out [our Discord](https://discord.gg/SCZTCYm), [our forums](https://forums.improbable.io/), or here in the [Github issues](https://github.com/spatialos/gdk-for-unity/issues?q=is%3Aissue+is%3Aopen+sort%3Aupdated-desc)!";
                    break;
                case "UnrealGDKTestGyms":
                    name = $"GDK for Unity Blank Project Alpha Release {options.Version}";
                    releaseBody =
$@"This release of the Blank Project is intended for use with the GDK for Unity Alpha Release {options.Version}.

Keep giving us your feedback and/or suggestions! Check out [our Discord](https://discord.gg/SCZTCYm), [our forums](https://forums.improbable.io/), or here in the [Github issues](https://github.com/spatialos/gdk-for-unity/issues?q=is%3Aissue+is%3Aopen+sort%3Aupdated-desc)!";
                    break;
                case "UnrealGDKExampleProject":
                    name = $"GDK for Unity Blank Project Alpha Release {options.Version}";
                    releaseBody =
$@"This release of the Blank Project is intended for use with the GDK for Unity Alpha Release {options.Version}.

Keep giving us your feedback and/or suggestions! Check out [our Discord](https://discord.gg/SCZTCYm), [our forums](https://forums.improbable.io/), or here in the [Github issues](https://github.com/spatialos/gdk-for-unity/issues?q=is%3Aissue+is%3Aopen+sort%3Aupdated-desc)!";
                    break;
                default:
                    throw new ArgumentException("Unsupported repository.", nameof(repoName));
            }

            return gitHubClient.CreateDraftRelease(gitHubRepo, options.Version, releaseBody, name, headCommit);
        }

        private static (string, int) ExtractPullRequestInfo(string pullRequestUrl)
        {
            const string regexString = "github\\.com\\/spatialos\\/(.*)\\/pull\\/([0-9]*)";

            var match = Regex.Match(pullRequestUrl, regexString);

            if (!match.Success)
            {
                throw new ArgumentException($"Malformed pull request url: {pullRequestUrl}");
            }

            if (match.Groups.Count < 3)
            {
                throw new ArgumentException($"Malformed pull request url: {pullRequestUrl}");
            }

            var repoName = match.Groups[1].Value;
            var pullRequestIdStr = match.Groups[2].Value;

            if (!int.TryParse(pullRequestIdStr, out int pullRequestId))
            {
                throw new Exception(
                    $"Parsing pull request URL failed. Expected number for pull request id, received: {pullRequestIdStr}");
            }

            return (repoName, pullRequestId);
        }

        private static string GetReleaseNotesFromChangeLog()
        {
            if (!File.Exists(ChangeLogFilename))
            {
                throw new InvalidOperationException("Could not get draft release notes, as the change log file, " +
                    $"{ChangeLogFilename}, does not exist.");
            }

            Logger.Info("Reading {0}...", ChangeLogFilename);

            var releaseBody = new StringBuilder();
            var changedSection = 0;

            using (var reader = new StreamReader(ChangeLogFilename))
            {
                while (!reader.EndOfStream)
                {
                    // Here we target the second Heading2 ("##") section.
                    // The first section will be the "Unreleased" section. The second will be the correct release notes.
                    var line = reader.ReadLine();
                    if (line.StartsWith("## "))
                    {
                        changedSection += 1;

                        if (changedSection == 3)
                        {
                            break;
                        }

                        continue;
                    }

                    if (changedSection == 2)
                    {
                        releaseBody.AppendLine(line);
                    }
                }
            }

            return releaseBody.ToString();
        }
    }
}
