using LibGit2Sharp;
using System;
using System.IO;
using System.Text.Json;

namespace ArbiterHost.GitInterface
{
    public class GitManager
    {
        private Repository? repo;
        private string authorName = "ArbiterUser";
        private string authorEmail = "arbiter@local";

        public GitManager()
        {
            LoadIdentityFromSettings();
        }

        private void LoadIdentityFromSettings()
        {
            try
            {
                string settingsPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "Config", "settings.json");
                if (!File.Exists(settingsPath)) return;
                using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
                var root = doc.RootElement;
                if (root.TryGetProperty("git_author_name", out var nameProp))
                    authorName = nameProp.GetString() ?? authorName;
                if (root.TryGetProperty("git_author_email", out var emailProp))
                    authorEmail = emailProp.GetString() ?? authorEmail;
            }
            catch
            {
                // Use defaults if settings cannot be read
            }
        }

        public void InitRepo(string path)
        {
            if (!Repository.IsValid(path))
                repo = new Repository(Repository.Init(path));
            else
                repo = new Repository(path);
        }

        public void Commit(string message)
        {
            if (repo == null) throw new InvalidOperationException("Repository not initialized.");
            Commands.Stage(repo, "*");
            var author = new Signature(authorName, authorEmail, DateTimeOffset.Now);
            repo.Commit(message, author, author);
        }

        public void CreateBranch(string branchName)
        {
            if (repo == null) throw new InvalidOperationException("Repository not initialized.");
            var branch = repo.CreateBranch(branchName);
            Commands.Checkout(repo, branch);
        }

        public void CheckoutBranch(string branchName)
        {
            if (repo == null) throw new InvalidOperationException("Repository not initialized.");
            Commands.Checkout(repo, branchName);
        }

        public void Push()
        {
            // TODO: configure remote and credentials for push
        }

        public void Pull()
        {
            // TODO: configure remote and credentials for pull
        }
    }
}
