using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Win32;

namespace ArbiterHost
{
    /// <summary>
    /// Settings dialog — grouped by: Connection, LLM Backend, Voice, Projects.
    /// Reads and writes HostApp/Config/settings.json.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private static readonly string SettingsPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "Config", "settings.json");

        public SettingsWindow()
        {
            InitializeComponent();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            Utilities.DarkTitleBar.Apply(this);
        }

        // ── Load ──────────────────────────────────────────────────────────────

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            PopulateFromSettings();
            UpdateBackendPanels();
        }

        private void PopulateFromSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                var root = doc.RootElement;

                BridgeUrlBox.Text   = GetStr(root, "python_bridge_url", "http://127.0.0.1:8000");
                EnginePathBox.Text  = GetStr(root, "arbiterEnginePath", "AIEngine/ArbiterEngine");
                EnginePortBox.Text  = GetStr(root, "arbiterEnginePort", "8001");

                // LLM
                string backend = GetStr(root, "llm_backend", "ollama");
                SelectComboByTag(LlmBackendBox, backend);
                ModelPathBox.Text   = GetStr(root, "llm_model_path", "");
                NCtxBox.Text        = GetStr(root, "embedded_n_ctx", "4096");
                NGpuLayersBox.Text  = GetStr(root, "embedded_n_gpu_layers", "-1");

                // Voice
                bool ttsOn = GetBool(root, "tts_enabled", true);
                TtsEnabledBox.IsChecked = ttsOn;
                SelectComboByContent(VoiceBox, GetStr(root, "default_voice", "British_Female"));

                // Projects
                ProjectsPathBox.Text = GetStr(root, "projects_path", "Projects");
                MemoryPathBox.Text   = GetStr(root, "memory_path", "Memory/ConversationLogs");
                GitNameBox.Text      = GetStr(root, "git_author_name", "ArbiterUser");
                GitEmailBox.Text     = GetStr(root, "git_author_email", "arbiter@local");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not read settings:\n{ex.Message}",
                    "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ── Save ──────────────────────────────────────────────────────────────

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Load existing JSON or start fresh
                JsonObject root;
                if (File.Exists(SettingsPath))
                {
                    root = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
                }
                else
                {
                    root = [];
                    Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                }

                // Connection
                root["python_bridge_url"]  = BridgeUrlBox.Text.Trim();
                root["arbiterEnginePath"]  = EnginePathBox.Text.Trim();
                if (int.TryParse(EnginePortBox.Text.Trim(), out int port))
                    root["arbiterEnginePort"] = port;

                // LLM
                string backend = GetSelectedTag(LlmBackendBox) ?? "ollama";
                root["llm_backend"]           = backend;
                root["llm_model_path"]        = ModelPathBox.Text.Trim();
                if (int.TryParse(NCtxBox.Text.Trim(), out int nCtx))
                    root["embedded_n_ctx"] = nCtx;
                if (int.TryParse(NGpuLayersBox.Text.Trim(), out int nGpu))
                    root["embedded_n_gpu_layers"] = nGpu;

                // Voice
                root["tts_enabled"]  = TtsEnabledBox.IsChecked == true;
                root["default_voice"] = (VoiceBox.SelectedItem as System.Windows.Controls.ComboBoxItem)
                                        ?.Content?.ToString() ?? "British_Female";

                // Projects
                root["projects_path"]   = ProjectsPathBox.Text.Trim();
                root["memory_path"]     = MemoryPathBox.Text.Trim();
                root["git_author_name"] = GitNameBox.Text.Trim();
                root["git_author_email"]= GitEmailBox.Text.Trim();

                string json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsPath, json);

                // Push changes into live AppConfig
                AppConfig.ApiBaseUrl        = BridgeUrlBox.Text.Trim();
                AppConfig.ArbiterEnginePath = EnginePathBox.Text.Trim();
                if (int.TryParse(EnginePortBox.Text.Trim(), out int p))
                    AppConfig.ArbiterEnginePort = p;
                AppConfig.ProjectsPath      = ProjectsPathBox.Text.Trim();
                AppConfig.MemoryPath        = MemoryPathBox.Text.Trim();
                AppConfig.LlmBackend        = backend;
                AppConfig.LlmModelPath      = ModelPathBox.Text.Trim();
                AppConfig.DefaultVoice      = root["default_voice"]!.ToString();
                AppConfig.TtsEnabled        = TtsEnabledBox.IsChecked == true;
                AppConfig.GitAuthorName     = GitNameBox.Text.Trim();
                AppConfig.GitAuthorEmail    = GitEmailBox.Text.Trim();

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not save settings:\n{ex.Message}",
                    "Settings", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        // ── LLM backend selection ─────────────────────────────────────────────

        private void LlmBackend_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            UpdateBackendPanels();
        }

        private void UpdateBackendPanels()
        {
            if (EmbeddedPanel == null || OllamaPanel == null) return;
            string tag = GetSelectedTag(LlmBackendBox) ?? "ollama";
            EmbeddedPanel.Visibility = tag == "embedded" ? Visibility.Visible : Visibility.Collapsed;
            OllamaPanel.Visibility   = tag == "ollama"   ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── Browse buttons ────────────────────────────────────────────────────

        private void BrowseEnginePath_Click(object sender, RoutedEventArgs e)
        {
            string? path = BrowseFolder(EnginePathBox.Text, "Select Arbiter Engine directory");
            if (path != null) EnginePathBox.Text = path;
        }

        private void BrowseModelPath_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title  = "Select GGUF model file",
                Filter = "GGUF models (*.gguf)|*.gguf|All files (*.*)|*.*",
            };
            if (!string.IsNullOrWhiteSpace(ModelPathBox.Text) &&
                File.Exists(ModelPathBox.Text))
                dlg.InitialDirectory = Path.GetDirectoryName(ModelPathBox.Text);

            if (dlg.ShowDialog() == true)
                ModelPathBox.Text = dlg.FileName;
        }

        private void BrowseProjectsPath_Click(object sender, RoutedEventArgs e)
        {
            string? path = BrowseFolder(ProjectsPathBox.Text, "Select Projects folder");
            if (path != null) ProjectsPathBox.Text = path;
        }

        private void BrowseMemoryPath_Click(object sender, RoutedEventArgs e)
        {
            string? path = BrowseFolder(MemoryPathBox.Text, "Select Memory / logs folder");
            if (path != null) MemoryPathBox.Text = path;
        }

        private static string? BrowseFolder(string current, string description)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = description,
            };
            if (Directory.Exists(current)) dlg.InitialDirectory = current;
            return dlg.ShowDialog() == true ? dlg.FolderName : null;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string GetStr(JsonElement root, string key, string fallback)
        {
            if (root.TryGetProperty(key, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number) return v.GetRawText();
                return v.GetString() ?? fallback;
            }
            return fallback;
        }

        private static bool GetBool(JsonElement root, string key, bool fallback)
        {
            if (root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True)
                return true;
            if (root.TryGetProperty(key, out var v2) && v2.ValueKind == JsonValueKind.False)
                return false;
            return fallback;
        }

        private static void SelectComboByTag(System.Windows.Controls.ComboBox cb, string tag)
        {
            foreach (System.Windows.Controls.ComboBoxItem item in cb.Items)
            {
                if (item.Tag?.ToString() == tag)
                {
                    cb.SelectedItem = item;
                    return;
                }
            }
            if (cb.Items.Count > 0) cb.SelectedIndex = 0;
        }

        private static void SelectComboByContent(System.Windows.Controls.ComboBox cb, string content)
        {
            foreach (System.Windows.Controls.ComboBoxItem item in cb.Items)
            {
                if (item.Content?.ToString() == content)
                {
                    cb.SelectedItem = item;
                    return;
                }
            }
            if (cb.Items.Count > 0) cb.SelectedIndex = 0;
        }

        private static string? GetSelectedTag(System.Windows.Controls.ComboBox cb)
        {
            return (cb.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString();
        }
    }
}
