using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ArbiterHost.BuildInterface;
using ArbiterHost.GitInterface;
using ArbiterHost.Utilities;

namespace ArbiterHost
{
    public partial class MainWindow : Window
    {
        // ── Project state ────────────────────────────────────────────────────
        private string? _currentProjectName;
        private string? _currentProjectPath;
        private readonly string _projectsRoot;

        // ── Managers ─────────────────────────────────────────────────────────
        private readonly GitManager _gitManager = new GitManager();
        private BuildManager? _buildManager;

        // ── HTTP clients ──────────────────────────────────────────────────────
        // Short-timeout client for status/health polling
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        // Long-timeout client for LLM inference (models can take minutes to respond)
        private static readonly HttpClient _chatHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
        private static string PythonApiBase => AppConfig.ApiBaseUrl;

        // ── Server constants ──────────────────────────────────────────────────
        private const int MaxServerStartupSeconds = 30;
        private const int MaxServerOutputChars    = 800;
        private Process? _serverProcess;

        // ── Build output pop-out ──────────────────────────────────────────────
        private Window? _buildOutputWindow;

        // ── View state ────────────────────────────────────────────────────────
        private bool _chatModeActive = true;

        // ── LLM / download timers ─────────────────────────────────────────────
        private DispatcherTimer? _llmStatusTimer;
        private DispatcherTimer? _downloadPollTimer;

        // ── Persona list (matches persona_manager.py built-ins) ───────────────
        private static readonly string[] DefaultPersonas =
        {
            "Arbiter", "Coder", "Teacher", "Organizer",
        };

        // ════════════════════════════════════════════════════════════════════
        //  CONSTRUCTOR
        // ════════════════════════════════════════════════════════════════════

        public MainWindow()
        {
            InitializeComponent();
            _projectsRoot = Path.Combine(Directory.GetCurrentDirectory(), "Projects");
            Directory.CreateDirectory(_projectsRoot);
            LoadProjects();
            PopulatePersonaList();
            UpdateToolbarState();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            DarkTitleBar.Apply(this);
        }

        // ════════════════════════════════════════════════════════════════════
        //  WINDOW LIFETIME
        // ════════════════════════════════════════════════════════════════════

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            AppendConsole(AppConsoleBox, $"Arbiter started. Projects root: {_projectsRoot}");
            await CheckServerStatusAsync();
            StartLlmStatusPolling();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            bool serverRunning = _serverProcess   != null && !_serverProcess.HasExited;
            bool engineRunning = AppConfig.EngineProcess != null && !AppConfig.EngineProcess.HasExited;
            bool hasUnsent     = !string.IsNullOrWhiteSpace(ChatInput.Text) ||
                                 !string.IsNullOrWhiteSpace(ConfigChatInput.Text);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Are you sure you want to exit Arbiter?");
            if (serverRunning || engineRunning)
            {
                sb.AppendLine();
                sb.AppendLine("The following services will be stopped:");
                if (serverRunning) sb.AppendLine("  •  Arbiter server  (port 8000)");
                if (engineRunning) sb.AppendLine($"  •  Arbiter Engine  (port {AppConfig.ArbiterEnginePort})");
            }
            if (hasUnsent) { sb.AppendLine(); sb.AppendLine("⚠  You have an unsent message in the chat input."); }

            var answer = MessageBox.Show(
                sb.ToString().TrimEnd(), "Exit Arbiter",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes) { e.Cancel = true; return; }

            _llmStatusTimer?.Stop();
            _downloadPollTimer?.Stop();

            try
            {
                if (_serverProcess != null && !_serverProcess.HasExited)
                    _serverProcess.Kill(entireProcessTree: true);
            }
            catch { /* best-effort */ }

            try
            {
                if (AppConfig.EngineProcess != null && !AppConfig.EngineProcess.HasExited)
                    AppConfig.EngineProcess.Kill(entireProcessTree: true);
            }
            catch { /* best-effort */ }
        }

        // ════════════════════════════════════════════════════════════════════
        //  VIEW TOGGLE  (Chat ↔ Config)
        // ════════════════════════════════════════════════════════════════════

        private void ViewToggle_Click(object sender, RoutedEventArgs e)
        {
            _chatModeActive = !_chatModeActive;
            ChatViewGrid.Visibility   = _chatModeActive ? Visibility.Visible   : Visibility.Collapsed;
            ConfigViewGrid.Visibility = _chatModeActive ? Visibility.Collapsed : Visibility.Visible;

            // Sync project selection between views
            if (!_chatModeActive && _currentProjectName != null)
            {
                var idx = ProjectListBox.Items.IndexOf(_currentProjectName);
                if (idx >= 0) ProjectListBox.SelectedIndex = idx;
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  PROJECTS (both views)
        // ════════════════════════════════════════════════════════════════════

        private void LoadProjects()
        {
            ProjectListBox.Items.Clear();
            ChatProjectListBox.Items.Clear();
            if (!Directory.Exists(_projectsRoot)) return;

            foreach (var dir in Directory.GetDirectories(_projectsRoot).OrderBy(d => d))
            {
                string name = Path.GetFileName(dir);
                ProjectListBox.Items.Add(name);
                ChatProjectListBox.Items.Add(name);
            }
        }

        private void ProjectListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ProjectListBox.SelectedItem == null) return;
            string name = ProjectListBox.SelectedItem.ToString() ?? string.Empty;
            // Sync selection to chat sidebar without re-triggering its handler
            var chatIdx = ChatProjectListBox.Items.IndexOf(name);
            if (chatIdx >= 0 && ChatProjectListBox.SelectedIndex != chatIdx)
                ChatProjectListBox.SelectedIndex = chatIdx;
            ActivateProject(name, Path.Combine(_projectsRoot, name));
        }

        private void ChatProjectListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ChatProjectListBox.SelectedItem == null) return;
            string name = ChatProjectListBox.SelectedItem.ToString() ?? string.Empty;
            // Sync selection to config sidebar
            var cfgIdx = ProjectListBox.Items.IndexOf(name);
            if (cfgIdx >= 0 && ProjectListBox.SelectedIndex != cfgIdx)
                ProjectListBox.SelectedIndex = cfgIdx;
            ActivateProject(name, Path.Combine(_projectsRoot, name));
        }

        private void CreateProject_Click(object sender, RoutedEventArgs e)
        {
            string? name = InputDialog.Show("Enter project name:", "Create Project", "NewProject");
            if (string.IsNullOrWhiteSpace(name)) return;
            name = SanitizeProjectName(name);
            if (string.IsNullOrWhiteSpace(name)) return;

            string newPath = Path.Combine(_projectsRoot, name);
            Directory.CreateDirectory(newPath);
            File.WriteAllText(Path.Combine(newPath, "roadmap.json"), "{ \"phases\": [], \"tasks\": [] }");

            // Add to both list boxes
            if (!ProjectListBox.Items.Contains(name))     ProjectListBox.Items.Add(name);
            if (!ChatProjectListBox.Items.Contains(name)) ChatProjectListBox.Items.Add(name);

            // Select in the active view
            if (_chatModeActive)
                ChatProjectListBox.SelectedItem = name;
            else
                ProjectListBox.SelectedItem = name;
        }

        private static string SanitizeProjectName(string raw) =>
            Regex.Replace(raw.Trim(), @"[^\w\s\-]", "").Replace(' ', '_');

        private void OpenArbiterSelf_Click(object sender, RoutedEventArgs e)
        {
            string appDir    = AppDomain.CurrentDomain.BaseDirectory;
            string hostAppSrc = Path.GetFullPath(Path.Combine(appDir, "..", "..", ".."));
            if (!Directory.Exists(hostAppSrc))
            {
                MessageBox.Show($"Could not locate Arbiter source at:\n{hostAppSrc}",
                    "Self-Iterate", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ActivateProject("Arbiter (Self)", hostAppSrc, isSelfProject: true);
        }

        private void ProjectListBox_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            foreach (var file in files)
            {
                string folderName = Path.GetFileNameWithoutExtension(file);
                string destPath   = Path.Combine(_projectsRoot, folderName);
                Directory.CreateDirectory(destPath);
                File.Copy(file, Path.Combine(destPath, Path.GetFileName(file)), true);
                if (!ProjectListBox.Items.Contains(folderName))     ProjectListBox.Items.Add(folderName);
                if (!ChatProjectListBox.Items.Contains(folderName)) ChatProjectListBox.Items.Add(folderName);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  PERSONA (chat view sidebar)
        // ════════════════════════════════════════════════════════════════════

        private void PopulatePersonaList()
        {
            ChatPersonaListBox.Items.Clear();
            foreach (var p in DefaultPersonas)
                ChatPersonaListBox.Items.Add(p);
            if (ChatPersonaListBox.Items.Count > 0)
                ChatPersonaListBox.SelectedIndex = 0;
        }

        private async void ChatPersonaListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_currentProjectName == null || ChatPersonaListBox.SelectedItem == null) return;
            string persona = ChatPersonaListBox.SelectedItem.ToString() ?? "Arbiter";
            try
            {
                var payload = JsonSerializer.Serialize(new { persona });
                var content = new StringContent(payload, Encoding.UTF8, "application/json");
                await _httpClient.PostAsync(
                    $"{PythonApiBase}/persona/{Uri.EscapeDataString(_currentProjectName)}", content);
            }
            catch { /* best-effort */ }
        }

        // ════════════════════════════════════════════════════════════════════
        //  PROJECT ACTIVATION
        // ════════════════════════════════════════════════════════════════════

        private void ActivateProject(string name, string path, bool isSelfProject = false)
        {
            _currentProjectName = name;
            _currentProjectPath = path;
            _buildManager       = new BuildManager(path);

            // Update both view headers
            Title      = isSelfProject ? "Arbiter — Self-Iteration Mode" : $"Arbiter — {name}";
            ChatHeader.Text     = isSelfProject ? $"Chat  [Self-Iterating: {name}]" : $"Chat  [{name}]";
            ChatViewHeader.Text = isSelfProject ? $"Self-Iterating: {name}" : name;

            // Clear displays in both views
            ChatDisplay.Items.Clear();
            ChatMessageStack.Children.Clear();
            SuggestionsListBox.Items.Clear();

            if (isSelfProject)
            {
                string selfMsg =
                    "Arbiter: Self-iteration mode active. I can read and suggest changes to my own " +
                    "source code. Describe what you'd like me to improve.";
                ChatDisplay.Items.Add(selfMsg);
                AddChatViewMessage("Arbiter", selfMsg.Substring("Arbiter: ".Length));
            }

            LoadProjectFiles();
            LoadPhaseSelector();
            UpdateToolbarState();

            // Fetch current persona for this project and select it in the sidebar
            _ = LoadProjectPersonaAsync(name);
        }

        private async Task LoadProjectPersonaAsync(string projectName)
        {
            try
            {
                var resp = await _httpClient.GetAsync(
                    $"{PythonApiBase}/persona/{Uri.EscapeDataString(projectName)}");
                if (!resp.IsSuccessStatusCode) return;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                string persona = doc.RootElement.TryGetProperty("persona", out var p)
                    ? p.GetString() ?? "Arbiter" : "Arbiter";

                int idx = ChatPersonaListBox.Items.IndexOf(persona);
                if (idx >= 0) ChatPersonaListBox.SelectedIndex = idx;
            }
            catch { /* server may not be running yet */ }
        }

        private void UpdateToolbarState()
        {
            bool hp = _currentProjectPath != null;

            // Config view toolbar buttons
            CommitBtn.IsEnabled = hp; BranchBtn.IsEnabled = hp;
            PushBtn.IsEnabled   = hp; PullBtn.IsEnabled   = hp;
            LogBtn.IsEnabled    = hp;
            BuildBtn.IsEnabled  = hp; RunBtn.IsEnabled    = hp;
            TestBtn.IsEnabled   = hp;
            PhaseSelector.IsEnabled      = hp;
            ConfigChatInput.IsEnabled    = hp;
            ExportChatBtn.IsEnabled      = hp;
            GenerateRoadmapBtn.IsEnabled = hp;

            // Chat view buttons
            ChatInput.IsEnabled      = hp;
            ChatExportBtn.IsEnabled  = hp;
            ChatRoadmapBtn.IsEnabled = hp;
        }

        // ════════════════════════════════════════════════════════════════════
        //  NEW CHAT
        // ════════════════════════════════════════════════════════════════════

        private void NewChat_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProjectName == null)
            {
                MessageBox.Show("Select a project first.", "New Chat",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            ChatDisplay.Items.Clear();
            ChatMessageStack.Children.Clear();
            SuggestionsListBox.Items.Clear();
            ChatInput.Clear();
            ConfigChatInput.Clear();
        }

        // ════════════════════════════════════════════════════════════════════
        //  PROJECT FILES + PHASE SELECTOR
        // ════════════════════════════════════════════════════════════════════

        private void LoadProjectFiles()
        {
            ProjectFilesTree.Items.Clear();
            if (_currentProjectPath == null || !Directory.Exists(_currentProjectPath)) return;
            var root = new TreeViewItem { Header = _currentProjectName };
            PopulateTreeView(root, _currentProjectPath);
            ProjectFilesTree.Items.Add(root);
            root.IsExpanded = true;
        }

        private static void PopulateTreeView(TreeViewItem parent, string path)
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(path)
                             .Where(d => !Path.GetFileName(d).StartsWith(".")))
                {
                    var item = new TreeViewItem { Header = Path.GetFileName(dir) };
                    PopulateTreeView(item, dir);
                    parent.Items.Add(item);
                }
                foreach (var file in Directory.GetFiles(path))
                    parent.Items.Add(new TreeViewItem { Header = Path.GetFileName(file) });
            }
            catch { }
        }

        private void LoadPhaseSelector()
        {
            PhaseSelector.Items.Clear();
            if (_currentProjectPath == null) return;
            string roadmapPath = Path.Combine(_currentProjectPath, "roadmap.json");
            if (!File.Exists(roadmapPath)) return;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(roadmapPath));
                if (!doc.RootElement.TryGetProperty("phases", out var phases)) return;
                foreach (var phase in phases.EnumerateArray())
                {
                    string name = phase.TryGetProperty("name", out var n)
                        ? n.GetString() ?? string.Empty
                        : phase.TryGetProperty("id", out var id)
                            ? $"Phase {id.GetInt32()}"
                            : "Phase";
                    PhaseSelector.Items.Add(new ComboBoxItem { Content = name });
                }
                if (PhaseSelector.Items.Count > 0) PhaseSelector.SelectedIndex = 0;
            }
            catch { }
        }

        private void ProjectFilesTree_Drop(object sender, DragEventArgs e)
        {
            if (_currentProjectPath == null) return;
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            foreach (var file in (string[])e.Data.GetData(DataFormats.FileDrop))
                File.Copy(file, Path.Combine(_currentProjectPath, Path.GetFileName(file)), true);
            LoadProjectFiles();
        }

        // ════════════════════════════════════════════════════════════════════
        //  CHAT — shared send logic
        // ════════════════════════════════════════════════════════════════════

        /// <summary>Send a message from Chat view (primary input).</summary>
        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProjectName == null) return;
            string message = ChatInput.Text.Trim();
            if (string.IsNullOrEmpty(message)) return;
            ChatInput.Clear();
            await SendMessageAsync(message);
        }

        /// <summary>Send a message from Config view chat panel.</summary>
        private async void ConfigSendButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProjectName == null) return;
            string message = ConfigChatInput.Text.Trim();
            if (string.IsNullOrEmpty(message)) return;
            ConfigChatInput.Clear();
            await SendMessageAsync(message);
        }

        /// <summary>Shared message-send logic; updates both Chat and Config view displays.</summary>
        private async Task SendMessageAsync(string message)
        {
            if (_currentProjectName == null || string.IsNullOrWhiteSpace(message)) return;

            // Show user message in both views
            string displayMsg = message;
            if (_currentProjectName == "Arbiter (Self)") displayMsg = "[Self-iteration request] " + message;

            ChatDisplay.Items.Add($"You: {displayMsg}");
            AddChatViewMessage("You", displayMsg);

            string voice = (VoiceSelector.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "British_Female";
            AppendConsole(LlmConsoleBox, $">> {displayMsg}");

            try
            {
                var payload = new { message = displayMsg, project = _currentProjectName, use_voice = true, voice };
                string json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // Use long-timeout client — LLM inference can take a while
                HttpResponseMessage response = await _chatHttpClient.PostAsync(PythonApiBase + "/chat", content);
                response.EnsureSuccessStatusCode();

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                string arbiterResponse = doc.RootElement.GetProperty("response").GetString() ?? string.Empty;

                // Show response in both views
                ChatDisplay.Items.Add($"Arbiter: {arbiterResponse}");
                ChatDisplay.ScrollIntoView(ChatDisplay.Items[ChatDisplay.Items.Count - 1]);
                AddChatViewMessage("Arbiter", arbiterResponse);
                SuggestionsListBox.Items.Add(arbiterResponse);
                AppendConsole(LlmConsoleBox, $"<< {arbiterResponse}");

                SetServerOnline();
            }
            catch (HttpRequestException ex) when (
                ex.InnerException is System.Net.Sockets.SocketException se &&
                se.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused)
            {
                SetServerOffline();
                AppendConsole(AppConsoleBox, "Chat error: server connection refused.");
                string err = "Error: Python server is not running. Click 'Start' or run: " +
                             "cd AIEngine/PythonBridge && python fastapi_bridge.py";
                ChatDisplay.Items.Add(err);
                AddChatViewMessage("System", err);
            }
            catch (TaskCanceledException)
            {
                SetServerOffline();
                AppendConsole(AppConsoleBox, "Chat error: request timed out.");
                string err = "Error: Request timed out. The Python server may not be running.";
                ChatDisplay.Items.Add(err);
                AddChatViewMessage("System", err);
            }
            catch (Exception ex)
            {
                AppendConsole(AppConsoleBox, $"Chat error: {ex.Message}");
                ChatDisplay.Items.Add($"Error: {ex.Message}");
                AddChatViewMessage("System", $"Error: {ex.Message}");
            }
        }

        // ── Key handlers ──────────────────────────────────────────────────

        private void ChatInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !e.IsRepeat &&
                !e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Shift))
            { e.Handled = true; SendButton_Click(sender, new RoutedEventArgs()); }
        }

        private void ConfigChatInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !e.IsRepeat &&
                !e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Shift))
            { e.Handled = true; ConfigSendButton_Click(sender, new RoutedEventArgs()); }
        }

        // ── Mic ───────────────────────────────────────────────────────────

        private const int SpeechRecognitionTimeoutSeconds = 10;

        private async void MicButton_Click(object sender, RoutedEventArgs e)
        {
            var btn = (Button)sender;
            btn.IsEnabled = false;
            btn.Content   = "…";
            try
            {
                using var recognizer = new System.Speech.Recognition.SpeechRecognitionEngine(
                    new System.Globalization.CultureInfo("en-US"));
                recognizer.LoadGrammar(new System.Speech.Recognition.DictationGrammar());
                recognizer.SetInputToDefaultAudioDevice();
                var result = await Task.Run(() =>
                    recognizer.Recognize(TimeSpan.FromSeconds(SpeechRecognitionTimeoutSeconds)));
                if (result != null)
                {
                    if (_chatModeActive) ChatInput.Text      = result.Text;
                    else                ConfigChatInput.Text = result.Text;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Voice input error: {ex.Message}", "Mic Input",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                btn.IsEnabled = true;
                btn.Content   = "Mic";
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  CHAT VIEW — styled message bubbles
        // ════════════════════════════════════════════════════════════════════

        private void AddChatViewMessage(string role, string text)
        {
            bool isUser   = role == "You";
            bool isSystem = role == "System";

            Color bgColor = isUser
                ? Color.FromRgb(0x00, 0x7A, 0xCC)  // accent blue for user
                : isSystem
                    ? Color.FromRgb(0x5A, 0x1A, 0x1A)  // dark red for errors
                    : Color.FromRgb(0x2D, 0x2D, 0x30);  // dark panel for AI

            var bubble = new Border
            {
                Background       = new SolidColorBrush(bgColor),
                CornerRadius     = new CornerRadius(12),
                Padding          = new Thickness(14, 10, 14, 10),
                Margin           = new Thickness(
                    isUser ? 80 : 0, 4,
                    isUser ? 0  : 80, 4),
                MaxWidth             = 700,
                HorizontalAlignment  = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            };

            bubble.Child = new TextBlock
            {
                Text         = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground   = Brushes.White,
                FontSize     = 13,
            };

            ChatMessageStack.Children.Add(bubble);
            ChatViewScroller.ScrollToEnd();
        }

        // ════════════════════════════════════════════════════════════════════
        //  EXPORT CHAT / ROADMAP (shared by both views)
        // ════════════════════════════════════════════════════════════════════

        private async void ExportChat_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProjectName == null) return;
            ExportChatBtn.IsEnabled = false; ChatExportBtn.IsEnabled = false;
            ExportChatBtn.Content   = "..."; ChatExportBtn.Content   = "...";
            try
            {
                string url = $"{PythonApiBase}/chat/export/{Uri.EscapeDataString(_currentProjectName)}";
                var resp = await _httpClient.GetAsync(url);
                resp.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                int    count = doc.RootElement.TryGetProperty("messages", out var m) ? m.GetInt32()    : 0;
                string path  = doc.RootElement.TryGetProperty("path",     out var p) ? p.GetString() ?? "" : "";
                AppendConsole(AppConsoleBox, $"Chat exported: {count} messages -> {path}");
                ShowToast($"Chat exported — {count} messages", "📥");
                MessageBox.Show($"Exported {count} messages to:\n{path}", "Export Chat",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed: {ex.Message}", "Export Chat",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                ExportChatBtn.IsEnabled = true; ChatExportBtn.IsEnabled = true;
                ExportChatBtn.Content   = "Export"; ChatExportBtn.Content = "Export";
            }
        }

        private async void GenerateRoadmap_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProjectName == null) return;
            GenerateRoadmapBtn.IsEnabled = false; ChatRoadmapBtn.IsEnabled = false;
            GenerateRoadmapBtn.Content   = "..."; ChatRoadmapBtn.Content   = "...";
            ChatDisplay.Items.Add("Arbiter: Generating roadmap — please wait...");
            AddChatViewMessage("Arbiter", "Generating roadmap — please wait...");
            try
            {
                string url    = $"{PythonApiBase}/roadmap/generate/{Uri.EscapeDataString(_currentProjectName)}";
                var    resp   = await _chatHttpClient.PostAsync(url, new StringContent("{}", Encoding.UTF8, "application/json"));
                resp.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                string roadmap = doc.RootElement.TryGetProperty("roadmap", out var r) ? r.GetString() ?? "" : "";
                string path    = doc.RootElement.TryGetProperty("path",    out var p) ? p.GetString() ?? "" : "";

                ChatDisplay.Items.Add($"Arbiter (Roadmap):\n{roadmap}");
                ChatDisplay.ScrollIntoView(ChatDisplay.Items[ChatDisplay.Items.Count - 1]);
                AddChatViewMessage("Arbiter", roadmap);
                SuggestionsListBox.Items.Add(roadmap);
                AppendConsole(AppConsoleBox, $"Roadmap saved -> {path}");
                LoadPhaseSelector();
            }
            catch (Exception ex)
            {
                AppendConsole(AppConsoleBox, $"Roadmap generation failed: {ex.Message}");
                ChatDisplay.Items.Add($"Error: {ex.Message}");
            }
            finally
            {
                GenerateRoadmapBtn.IsEnabled = true; ChatRoadmapBtn.IsEnabled = true;
                GenerateRoadmapBtn.Content   = "Roadmap"; ChatRoadmapBtn.Content = "Roadmap";
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  SUGGESTIONS
        // ════════════════════════════════════════════════════════════════════

        private void ApproveSuggestion_Click(object sender, RoutedEventArgs e)
        {
            if (SuggestionsListBox.SelectedItem == null || _currentProjectPath == null) return;
            string code = SuggestionsListBox.SelectedItem.ToString() ?? string.Empty;

            if (_currentProjectName == "Arbiter (Self)")
            {
                string? targetFile = InputDialog.Show(
                    "Enter the relative path of the source file to update (e.g. MainWindow.xaml.cs):",
                    "Self-Iterate: Apply Change", "MainWindow.xaml.cs");
                if (string.IsNullOrWhiteSpace(targetFile)) return;

                targetFile = Path.GetFileName(targetFile);
                string? filePath = FindSourceFile(_currentProjectPath, targetFile);
                if (filePath == null)
                {
                    MessageBox.Show($"File '{targetFile}' not found under the Arbiter source tree.",
                        "Self-Iterate", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string safeRoot = Path.GetFullPath(_currentProjectPath) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(filePath).StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("Resolved path is outside the project directory.",
                        "Self-Iterate", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var confirm = MessageBox.Show(
                    $"Overwrite:\n{filePath}\n\nWith AI suggestion? This cannot be undone.",
                    "Self-Iterate: Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes) return;

                File.WriteAllText(filePath, code);
                LoadProjectFiles();
                ShowToast($"Applied to {Path.GetFileName(filePath)}", "✓");
                MessageBox.Show($"Applied to {filePath}\n\nRebuild Arbiter to apply the change.",
                    "Self-Iterate: Done", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                string filePath = Path.Combine(_currentProjectPath, "GeneratedCode.cs");
                File.WriteAllText(filePath, code);
                LoadProjectFiles();
                ShowToast($"Saved to {Path.GetFileName(filePath)}", "✓");
                MessageBox.Show($"Code saved to {filePath}", "Approved",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private static string? FindSourceFile(string root, string fileName)
        {
            try { return Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).FirstOrDefault(); }
            catch { return null; }
        }

        private void MoveSuggestion_Click(object sender, RoutedEventArgs e)
        {
            if (SuggestionsListBox.SelectedItem == null) return;
            string code = SuggestionsListBox.SelectedItem.ToString() ?? string.Empty;

            var otherProjects = Directory.Exists(_projectsRoot)
                ? Directory.GetDirectories(_projectsRoot)
                    .Select(d => Path.GetFileName(d))
                    .Where(p => !string.IsNullOrEmpty(p) && p != _currentProjectName)
                    .Select(p => p!)
                    .ToList()
                : new List<string>();

            if (otherProjects.Count == 0)
            {
                MessageBox.Show("No other projects available.", "Move",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string? targetProject = InputDialog.Show(
                $"Enter target project name ({string.Join(", ", otherProjects)}):",
                "Move Suggestion", otherProjects[0]);
            if (string.IsNullOrWhiteSpace(targetProject)) return;

            string targetDir = Path.Combine(_projectsRoot, targetProject);
            if (!Directory.Exists(targetDir))
            {
                MessageBox.Show($"Project '{targetProject}' does not exist.", "Move",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            File.WriteAllText(Path.Combine(targetDir, "GeneratedCode.cs"), code);
            SuggestionsListBox.Items.Remove(SuggestionsListBox.SelectedItem);
            ShowToast($"Moved to '{targetProject}'", "→");
            MessageBox.Show($"Suggestion moved to project '{targetProject}'.", "Moved",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ════════════════════════════════════════════════════════════════════
        //  PYTHON SERVER
        // ════════════════════════════════════════════════════════════════════

        private async Task CheckServerStatusAsync()
        {
            SetServerChecking();
            bool online = false;
            try
            {
                var response = await _httpClient.GetAsync(PythonApiBase + "/health");
                online = response.IsSuccessStatusCode;
            }
            catch { online = false; }

            if (online) SetServerOnline();
            else        SetServerOffline();
        }

        private void SetServerChecking()
        {
            ServerStatusDot.Fill    = Brushes.Gray;
            ServerStatusText.Text   = "Server: checking...";
            StartServerButton.IsEnabled = false;
            ChatServerDot.Fill      = Brushes.Gray;
            ChatServerText.Text     = "Checking...";
            ChatStartServerBtn.Visibility = Visibility.Collapsed;
        }

        private void SetServerOnline()
        {
            ServerStatusDot.Fill    = Brushes.LimeGreen;
            ServerStatusText.Text   = "Server: Online";
            StartServerButton.IsEnabled = false;
            ChatServerDot.Fill      = Brushes.LimeGreen;
            ChatServerText.Text     = "Server online";
            ChatStartServerBtn.Visibility = Visibility.Collapsed;
        }

        private void SetServerOffline()
        {
            ServerStatusDot.Fill    = Brushes.Red;
            ServerStatusText.Text   = "Server: Offline";
            StartServerButton.IsEnabled = true;
            ChatServerDot.Fill      = Brushes.Red;
            ChatServerText.Text     = "Server offline";
            ChatStartServerBtn.Visibility = Visibility.Visible;
        }

        private async void StartServer_Click(object sender, RoutedEventArgs e)
        {
            StartServerButton.IsEnabled = false;
            ChatStartServerBtn.Visibility = Visibility.Collapsed;
            ServerStatusText.Text = "Server: starting...";
            ChatServerText.Text   = "Starting...";
            ServerStatusDot.Fill  = Brushes.Orange;
            ChatServerDot.Fill    = Brushes.Orange;
            AppendConsole(AppConsoleBox, "Server start requested.");

            try
            {
                string appDir     = AppDomain.CurrentDomain.BaseDirectory;
                string bridgePath = Path.GetFullPath(
                    Path.Combine(appDir, "..", "..", "..", "..", "AIEngine", "PythonBridge", "fastapi_bridge.py"));

                if (!File.Exists(bridgePath))
                {
                    string msg = $"Could not find fastapi_bridge.py at:\n{bridgePath}\n\n" +
                                 "Start it manually:\n  cd AIEngine/PythonBridge\n  python fastapi_bridge.py";
                    AppendConsole(ServerConsoleBox, "ERROR: " + msg);
                    MessageBox.Show(msg, "Server Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                    SetServerOffline();
                    return;
                }

                string bridgeDir = Path.GetDirectoryName(bridgePath)!;
                string python    = PythonHelper.FindExecutable();
                AppendConsole(ServerConsoleBox, $"Python: {python}");
                AppendConsole(ServerConsoleBox, $"Bridge: {bridgePath}");

                var psi = new ProcessStartInfo
                {
                    FileName               = python,
                    Arguments              = $"\"{bridgePath}\"",
                    WorkingDirectory       = bridgeDir,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                };

                _serverProcess         = Process.Start(psi);
                AppConfig.BridgeProcess = _serverProcess;

                if (_serverProcess == null)
                {
                    MessageBox.Show($"Failed to start the Python server process using '{python}'.",
                        "Start Server Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    SetServerOffline();
                    return;
                }

                var serverOutput = new System.Text.StringBuilder();
                _serverProcess.OutputDataReceived += (_, args) =>
                {
                    if (args.Data == null) return;
                    serverOutput.AppendLine(args.Data);
                    Dispatcher.Invoke(() => AppendConsole(ServerConsoleBox, args.Data));
                };
                _serverProcess.ErrorDataReceived += (_, args) =>
                {
                    if (args.Data == null) return;
                    serverOutput.AppendLine(args.Data);
                    Dispatcher.Invoke(() => AppendConsole(ServerConsoleBox, args.Data));
                };
                _serverProcess.BeginOutputReadLine();
                _serverProcess.BeginErrorReadLine();

                bool serverOnline = false;
                for (int i = 0; i < MaxServerStartupSeconds; i++)
                {
                    await Task.Delay(1000);

                    if (_serverProcess.HasExited)
                    {
                        await Task.Delay(500);
                        AppendConsole(AppConsoleBox, $"Server exited (code {_serverProcess.ExitCode}).");
                        string captured = serverOutput.ToString().Trim();
                        if (captured.Length > MaxServerOutputChars)
                            captured = "...\n" + captured[^MaxServerOutputChars..];

                        string msg = $"The Python server process exited unexpectedly " +
                                     $"(exit code {_serverProcess.ExitCode}).\n\n" +
                                     "Python output:\n" +
                                     "──────────────────────────────\n" +
                                     (string.IsNullOrEmpty(captured)
                                         ? "No output captured — check Console > Server tab."
                                         : captured) + "\n" +
                                     "──────────────────────────────\n\n" +
                                     "If packages are missing, run:\n" +
                                     "  pip install -r AIEngine/PythonBridge/requirements.txt --prefer-binary\n\n" +
                                     "The server will attempt to install them automatically on next start.";
                        MessageBox.Show(msg, "Server Exited", MessageBoxButton.OK, MessageBoxImage.Error);
                        SetServerOffline();
                        return;
                    }

                    ServerStatusText.Text = $"Server: starting... ({i + 1}/{MaxServerStartupSeconds}s)";
                    ChatServerText.Text   = $"Starting... ({i + 1}s)";

                    try
                    {
                        var resp = await _httpClient.GetAsync(PythonApiBase + "/health");
                        if (resp.IsSuccessStatusCode) { serverOnline = true; break; }
                    }
                    catch { }
                }

                if (serverOnline)
                {
                    SetServerOnline();
                    AppendConsole(ServerConsoleBox, $"Server online at {PythonApiBase}");
                    ShowToast("Server online", "🟢");
                }
                else
                {
                    MessageBox.Show(
                        $"Server process is running but has not responded after {MaxServerStartupSeconds}s.\n" +
                        "It may still be loading. Check Console > Server for details.",
                        "Server Timeout", MessageBoxButton.OK, MessageBoxImage.Warning);
                    SetServerOffline();
                }
            }
            catch (Exception ex)
            {
                string msg = $"Failed to start Python server:\n{ex.Message}\n\n" +
                             "Ensure Python is installed and in PATH.";
                AppendConsole(ServerConsoleBox, $"Exception: {ex.Message}");
                MessageBox.Show(msg, "Start Server Error", MessageBoxButton.OK, MessageBoxImage.Error);
                SetServerOffline();
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  LLM STATUS POLLING
        // ════════════════════════════════════════════════════════════════════

        private void StartLlmStatusPolling()
        {
            _llmStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _llmStatusTimer.Tick += async (_, _) => await RefreshLlmStatusAsync();
            _llmStatusTimer.Start();
            _ = RefreshLlmStatusAsync(); // immediate first check
        }

        private async Task RefreshLlmStatusAsync()
        {
            try
            {
                var resp = await _httpClient.GetAsync(PythonApiBase + "/llm/status");
                if (!resp.IsSuccessStatusCode) return;

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                string backend = doc.RootElement.TryGetProperty("backend", out var b) ? b.GetString() ?? "stub" : "stub";
                string detail  = doc.RootElement.TryGetProperty("detail",  out var d) ? d.GetString() ?? ""     : "";

                Dispatcher.Invoke(() => UpdateLlmStatusUI(backend, detail));
            }
            catch { /* server offline — status dot already shows this */ }
        }

        private void UpdateLlmStatusUI(string backend, string detail)
        {
            switch (backend)
            {
                case "gguf":
                    LlmStatusDot.Fill = Brushes.LimeGreen;
                    LlmStatusText.Text = $"GGUF: {Path.GetFileName(detail)}";
                    LlmNoModelPanel.Visibility = Visibility.Collapsed;
                    break;
                case "ollama":
                    LlmStatusDot.Fill = Brushes.LimeGreen;
                    LlmStatusText.Text = $"Ollama: {detail}";
                    LlmNoModelPanel.Visibility = Visibility.Collapsed;
                    break;
                case "stub":
                case "not_loaded":
                    LlmStatusDot.Fill = Brushes.Orange;
                    LlmStatusText.Text = "No LLM \u2014 stub mode";
                    LlmNoModelPanel.Visibility = Visibility.Visible;
                    break;
                default:
                    LlmStatusDot.Fill = Brushes.Gray;
                    LlmStatusText.Text = $"LLM: {backend}";
                    LlmNoModelPanel.Visibility = Visibility.Visible;
                    break;
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  MODEL DOWNLOAD + AUTO-RELOAD
        // ════════════════════════════════════════════════════════════════════

        private async void DownloadModel_Click(object sender, RoutedEventArgs e)
        {
            DownloadModelBtn.IsEnabled = false;
            DownloadModelBtn.Content   = "Downloading...";
            LlmDownloadProgress.Visibility    = Visibility.Visible;
            LlmDownloadStatusText.Visibility  = Visibility.Visible;
            LlmDownloadStatusText.Text        = "Starting download...";

            try
            {
                var payload = JsonSerializer.Serialize(new { auto = true });
                var content = new StringContent(payload, Encoding.UTF8, "application/json");
                var resp = await _httpClient.PostAsync(PythonApiBase + "/models/download", content);
                if (!resp.IsSuccessStatusCode)
                {
                    LlmDownloadStatusText.Text = "Could not start download.";
                    DownloadModelBtn.IsEnabled = true;
                    DownloadModelBtn.Content   = "Download model automatically";
                    return;
                }
            }
            catch (Exception ex)
            {
                LlmDownloadStatusText.Text = $"Error: {ex.Message}";
                DownloadModelBtn.IsEnabled = true;
                DownloadModelBtn.Content   = "Download model automatically";
                return;
            }

            // Poll download progress
            _downloadPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _downloadPollTimer.Tick += async (_, _) => await PollDownloadStatusAsync();
            _downloadPollTimer.Start();
        }

        private async Task PollDownloadStatusAsync()
        {
            try
            {
                var resp = await _httpClient.GetAsync(PythonApiBase + "/models/download/status");
                if (!resp.IsSuccessStatusCode) return;

                using var doc    = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                bool   running   = doc.RootElement.TryGetProperty("running",  out var r) && r.GetBoolean();
                double progress  = doc.RootElement.TryGetProperty("progress", out var p) ? p.GetDouble() : 0;
                string message   = doc.RootElement.TryGetProperty("message",  out var m) ? m.GetString() ?? "" : "";
                string? error    = doc.RootElement.TryGetProperty("error",    out var er) && er.ValueKind != JsonValueKind.Null
                                   ? er.GetString() : null;
                string? modelPath = doc.RootElement.TryGetProperty("model_path", out var mp) && mp.ValueKind != JsonValueKind.Null
                                    ? mp.GetString() : null;

                LlmDownloadProgress.Value  = progress;
                LlmDownloadStatusText.Text = message;

                if (error != null)
                {
                    _downloadPollTimer?.Stop();
                    LlmDownloadStatusText.Text = $"Error: {error}";
                    DownloadModelBtn.IsEnabled = true;
                    DownloadModelBtn.Content   = "Retry download";
                    LlmDownloadProgress.Visibility = Visibility.Collapsed;
                    return;
                }

                if (!running && (modelPath != null || progress >= 100))
                {
                    _downloadPollTimer?.Stop();
                    LlmDownloadStatusText.Text = "Download complete — loading model...";

                    // Trigger server-side model reload
                    await ReloadModelAsync();
                }
            }
            catch { /* poll errors are transient */ }
        }

        private async Task ReloadModelAsync()
        {
            try
            {
                var resp = await _httpClient.PostAsync(PythonApiBase + "/models/reload",
                    new StringContent("{}", Encoding.UTF8, "application/json"));

                if (resp.IsSuccessStatusCode)
                {
                    using var doc    = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    string   backend = "";
                    string   detail  = "";
                    if (doc.RootElement.TryGetProperty("status", out var s))
                    {
                        backend = s.TryGetProperty("backend", out var b) ? b.GetString() ?? "" : "";
                        detail  = s.TryGetProperty("detail",  out var d) ? d.GetString() ?? "" : "";
                    }
                    UpdateLlmStatusUI(backend, detail);
                    LlmDownloadProgress.Visibility   = Visibility.Collapsed;
                    LlmDownloadStatusText.Visibility = Visibility.Collapsed;
                    DownloadModelBtn.IsEnabled = (backend == "stub");
                    DownloadModelBtn.Content   = "Download model automatically";

                    if (backend == "gguf")
                        ShowToast($"Model loaded: {Path.GetFileName(detail)}", "✓");
                    else
                        ShowToast("Model reload complete", "✓");
                }
            }
            catch (Exception ex)
            {
                LlmDownloadStatusText.Text = $"Reload error: {ex.Message}";
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  TOAST NOTIFICATIONS
        // ════════════════════════════════════════════════════════════════════

        /// <summary>Show a brief toast notification in the bottom-right corner.</summary>
        private void ShowToast(string message, string emoji = "✓")
        {
            Dispatcher.Invoke(() =>
            {
                var toast = new Border
                {
                    Background    = new SolidColorBrush(Color.FromRgb(0x1A, 0x27, 0x1A)),
                    BorderBrush   = new SolidColorBrush(Color.FromRgb(0x2D, 0x7A, 0x2D)),
                    BorderThickness = new Thickness(1),
                    CornerRadius  = new CornerRadius(8),
                    Padding       = new Thickness(14, 10, 14, 10),
                    Margin        = new Thickness(0, 4, 0, 0),
                    Opacity       = 0,
                };

                toast.Child = new TextBlock
                {
                    Text         = $"{emoji}  {message}",
                    Foreground   = new SolidColorBrush(Color.FromRgb(0x7F, 0xE5, 0x7F)),
                    FontSize     = 12,
                    TextWrapping = TextWrapping.Wrap,
                };

                ToastContainer.Children.Insert(0, toast);

                var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
                toast.BeginAnimation(OpacityProperty, fadeIn);

                var autoRemove = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                autoRemove.Tick += (s, _) =>
                {
                    autoRemove.Stop();
                    var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
                    fadeOut.Completed += (_, _) =>
                    {
                        if (ToastContainer.Children.Contains(toast))
                            ToastContainer.Children.Remove(toast);
                    };
                    toast.BeginAnimation(OpacityProperty, fadeOut);
                };
                autoRemove.Start();
            });
        }

        // ════════════════════════════════════════════════════════════════════
        //  SETTINGS (gear button)
        // ════════════════════════════════════════════════════════════════════

        private void GearBtn_Click(object sender, RoutedEventArgs e)
        {
            var win = new SettingsWindow { Owner = this };
            win.ShowDialog();
        }

        // ════════════════════════════════════════════════════════════════════
        //  CONSOLE HELPERS
        // ════════════════════════════════════════════════════════════════════

        private static void AppendConsole(TextBox box, string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            box.AppendText(line + Environment.NewLine);
            box.ScrollToEnd();
        }

        private void CopyLlmConsole_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(LlmConsoleBox.Text)) Clipboard.SetText(LlmConsoleBox.Text);
        }
        private void CopyAppConsole_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(AppConsoleBox.Text)) Clipboard.SetText(AppConsoleBox.Text);
        }
        private void CopyServerConsole_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(ServerConsoleBox.Text)) Clipboard.SetText(ServerConsoleBox.Text);
        }

        // ════════════════════════════════════════════════════════════════════
        //  GIT OPERATIONS
        // ════════════════════════════════════════════════════════════════════

        private void Commit_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProjectPath == null) return;
            string? message = InputDialog.Show("Enter commit message:", "Git Commit", "Arbiter: auto-commit");
            if (string.IsNullOrWhiteSpace(message)) return;
            try
            {
                _gitManager.InitRepo(_currentProjectPath);
                _gitManager.Commit(message);
                ShowToast("Committed successfully", "✓");
                MessageBox.Show("Committed successfully.", "Git", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Commit failed: {ex.Message}", "Git Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Branch_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProjectPath == null) return;
            string? name = InputDialog.Show("Enter branch name:", "Create Branch", "feature/new-branch");
            if (string.IsNullOrWhiteSpace(name)) return;
            if (!Regex.IsMatch(name, @"^[\w\-./]+$"))
            {
                MessageBox.Show("Invalid branch name.", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                _gitManager.CreateBranch(name);
                MessageBox.Show($"Branch '{name}' created.", "Git", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Branch failed: {ex.Message}", "Git Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Push_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProjectPath == null) return;
            string? remoteUrl = InputDialog.Show(
                "Enter remote URL (e.g. https://github.com/user/repo.git):",
                "Git Push", string.Empty);
            if (string.IsNullOrWhiteSpace(remoteUrl)) return;
            try
            {
                _gitManager.InitRepo(_currentProjectPath);
                _gitManager.SetRemote(remoteUrl);
                _gitManager.Push();
                MessageBox.Show("Pushed successfully.", "Git Push", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Push failed: {ex.Message}", "Git Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Pull_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProjectPath == null) return;
            try
            {
                _gitManager.InitRepo(_currentProjectPath);
                _gitManager.Pull();
                LoadProjectFiles();
                MessageBox.Show("Pulled successfully.", "Git Pull", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Pull failed: {ex.Message}", "Git Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Log_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProjectPath == null) return;
            try
            {
                _gitManager.InitRepo(_currentProjectPath);
                var entries = _gitManager.GetLog(20).ToList();
                string logText = entries.Count > 0
                    ? string.Join("\n", entries.Select(c =>
                        $"{c.Sha}  {c.When:yyyy-MM-dd HH:mm}  {c.Author}: {c.Message}"))
                    : "No commits yet.";
                MessageBox.Show(logText, "Git Log", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Log failed: {ex.Message}", "Git Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  BUILD / RUN / TEST
        // ════════════════════════════════════════════════════════════════════

        private async void Build_Click(object sender, RoutedEventArgs e) =>
            await ExecuteBuildActionAsync(BuildBtn, BuildManager.BuildAction.Build, "Build");

        private async void Run_Click(object sender, RoutedEventArgs e) =>
            await ExecuteBuildActionAsync(RunBtn, BuildManager.BuildAction.Run, "Run");

        private async void Test_Click(object sender, RoutedEventArgs e) =>
            await ExecuteBuildActionAsync(TestBtn, BuildManager.BuildAction.Test, "Test");

        private const int BuildOutputTabIndex = 1;

        private async Task ExecuteBuildActionAsync(Button btn, BuildManager.BuildAction action, string label)
        {
            if (_buildManager == null) return;

            string command = _buildManager.AutoDetectCommand(action);
            if (string.IsNullOrEmpty(command))
            {
                command = InputDialog.Show(
                    $"No {label} command detected. Enter command to run in the project folder:",
                    $"Custom {label}", string.Empty) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(command)) return;
            }

            btn.IsEnabled = false;
            btn.Content   = "...";
            BottomTabControl.SelectedIndex = BuildOutputTabIndex;
            BuildOutputBox.Text = $"> {label}: {command}\n\n";

            TextBox? popOutBox = _buildOutputWindow?.Content as TextBox;

            try
            {
                var result = await _buildManager.RunAsync(command);
                BuildOutputBox.AppendText(result.Output);
                if (popOutBox != null) popOutBox.Text = BuildOutputBox.Text;

                string statusLine = result.Success
                    ? $"\n✓ {label} succeeded (exit 0)"
                    : $"\n✗ {label} failed (exit {result.ExitCode})";

                BuildOutputBox.AppendText(statusLine);
                if (popOutBox != null) popOutBox.AppendText(statusLine);

                ShowToast(result.Success ? $"{label} succeeded" : $"{label} failed", result.Success ? "✓" : "✗");

                if (!result.Success)
                    ChatDisplay.Items.Add($"[Build] {label} failed — see Build Output tab for details.");
            }
            catch (Exception ex)
            {
                BuildOutputBox.AppendText($"\n[Error] {ex.Message}");
                if (popOutBox != null) popOutBox.AppendText($"\n[Error] {ex.Message}");
            }
            finally
            {
                BuildOutputBox.ScrollToEnd();
                popOutBox?.ScrollToEnd();
                btn.IsEnabled = true;
                btn.Content   = label;
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  POP-OUT BUILD OUTPUT
        // ════════════════════════════════════════════════════════════════════

        private void PopOutBuildOutput_Click(object sender, RoutedEventArgs e)
        {
            if (_buildOutputWindow != null && _buildOutputWindow.IsLoaded)
            {
                _buildOutputWindow.Focus();
                return;
            }

            var outputBox = new TextBox
            {
                IsReadOnly                 = true,
                TextWrapping               = TextWrapping.Wrap,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily   = new FontFamily("Courier New"),
                FontSize     = 11,
                AcceptsReturn = true,
                Text         = BuildOutputBox.Text,
            };

            _buildOutputWindow = new Window
            {
                Title  = "Arbiter — Build Output",
                Width  = 800,
                Height = 500,
                Content = outputBox,
            };
            _buildOutputWindow.SourceInitialized += (_, _) => DarkTitleBar.Apply(_buildOutputWindow);
            _buildOutputWindow.Show();
        }
    }
}
