using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ArbiterHost.GitInterface;
using ArbiterHost.Utilities;

namespace ArbiterHost
{
    public partial class ProjectWindow : Window
    {
        public string ProjectName { get; private set; }
        public string CurrentProjectPath { get; private set; }

        private static readonly HttpClient httpClient = new HttpClient();
        private const string PythonApiBase = "http://127.0.0.1:8000";

        private readonly GitManager gitManager = new GitManager();

        public ProjectWindow(string projectName, string projectsRoot)
        {
            InitializeComponent();
            ProjectName = projectName;
            CurrentProjectPath = Path.Combine(projectsRoot, projectName);
            Title = $"Arbiter — {projectName}";
            LoadProjectFiles();
        }

        private void LoadProjectFiles()
        {
            ProjectFilesTree.Items.Clear();
            if (!Directory.Exists(CurrentProjectPath)) return;
            var root = new TreeViewItem { Header = ProjectName };
            PopulateTreeView(root, CurrentProjectPath);
            ProjectFilesTree.Items.Add(root);
            root.IsExpanded = true;
        }

        private static void PopulateTreeView(TreeViewItem parent, string path)
        {
            foreach (var dir in Directory.GetDirectories(path))
            {
                var item = new TreeViewItem { Header = Path.GetFileName(dir) };
                PopulateTreeView(item, dir);
                parent.Items.Add(item);
            }
            foreach (var file in Directory.GetFiles(path))
            {
                parent.Items.Add(new TreeViewItem { Header = Path.GetFileName(file) });
            }
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            string message = ChatInput.Text.Trim();
            if (string.IsNullOrEmpty(message)) return;

            ChatDisplay.Items.Add($"You: {message}");
            ChatInput.Clear();

            string voice = (VoiceSelector.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "British_Female";

            try
            {
                var payload = new
                {
                    message,
                    project = ProjectName,
                    use_voice = true,
                    voice
                };

                string json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                HttpResponseMessage response = await httpClient.PostAsync(PythonApiBase + "/chat", content);
                response.EnsureSuccessStatusCode();

                string responseString = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseString);
                string arbiterResponse = doc.RootElement.GetProperty("response").GetString() ?? string.Empty;

                ChatDisplay.Items.Add($"Arbiter: {arbiterResponse}");
                SuggestionsListBox.Items.Add(arbiterResponse);

                ChatDisplay.ScrollIntoView(ChatDisplay.Items[ChatDisplay.Items.Count - 1]);
            }
            catch (Exception ex)
            {
                ChatDisplay.Items.Add($"Error: {ex.Message}");
            }
        }

        private void MicButton_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Voice input (STT) stub — integrate Whisper or Coqui STT here.",
                "Mic Input", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ApproveSuggestion_Click(object sender, RoutedEventArgs e)
        {
            if (SuggestionsListBox.SelectedItem == null) return;
            string code = SuggestionsListBox.SelectedItem.ToString() ?? string.Empty;
            string filePath = Path.Combine(CurrentProjectPath, "GeneratedCode.cs");
            File.WriteAllText(filePath, code);
            LoadProjectFiles();
            MessageBox.Show($"Code saved to {filePath}", "Approved", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void MoveSuggestion_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Move suggestion to another project — stub for future implementation.",
                "Move", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ProjectFilesTree_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            foreach (var file in files)
            {
                string dest = Path.Combine(CurrentProjectPath, Path.GetFileName(file));
                File.Copy(file, dest, true);
            }
            LoadProjectFiles();
        }

        private void Commit_Click(object sender, RoutedEventArgs e)
        {
            string? message = InputDialog.Show("Enter commit message:", "Git Commit", "Arbiter: auto-commit");
            if (string.IsNullOrWhiteSpace(message)) return;
            try
            {
                gitManager.InitRepo(CurrentProjectPath);
                gitManager.Commit(message);
                MessageBox.Show("Committed successfully.", "Git", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Commit failed: {ex.Message}", "Git Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Branch_Click(object sender, RoutedEventArgs e)
        {
            string? name = InputDialog.Show("Enter branch name:", "Create Branch", "feature/new-branch");
            if (string.IsNullOrWhiteSpace(name)) return;

            // Validate branch name: only allow alphanumeric, dash, underscore, dot, and forward slash
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[\w\-./]+$"))
            {
                MessageBox.Show("Invalid branch name. Use only letters, numbers, dash, dot, underscore, or slash.",
                    "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                gitManager.CreateBranch(name);
                MessageBox.Show($"Branch '{name}' created.", "Git", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Branch failed: {ex.Message}", "Git Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Push_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Push stub — configure remote origin to enable.", "Git Push",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Pull_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Pull stub — configure remote origin to enable.", "Git Pull",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Log_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Git log stub — shows commit history.", "Git Log",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
