using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using ArbiterHost.Utilities;

namespace ArbiterHost
{
    public partial class WorkspaceWindow : Window
    {
        private readonly string projectsRoot = Path.Combine(
            Directory.GetCurrentDirectory(), "Projects");

        public WorkspaceWindow()
        {
            InitializeComponent();
            LoadProjects();
        }

        private void LoadProjects()
        {
            ProjectListBox.Items.Clear();
            if (Directory.Exists(projectsRoot))
            {
                foreach (var dir in Directory.GetDirectories(projectsRoot))
                    ProjectListBox.Items.Add(Path.GetFileName(dir));
            }
        }

        private void CreateProject_Click(object sender, RoutedEventArgs e)
        {
            string? name = InputDialog.Show("Enter project name:", "Create Project", "NewProject");
            if (string.IsNullOrWhiteSpace(name)) return;

            // Sanitize: allow only alphanumeric, dash, underscore, and space
            name = System.Text.RegularExpressions.Regex.Replace(name.Trim(), @"[^\w\s\-]", "");
            name = name.Replace(' ', '_');
            if (string.IsNullOrWhiteSpace(name)) return;

            string newProjectPath = Path.Combine(projectsRoot, name);
            Directory.CreateDirectory(newProjectPath);
            File.WriteAllText(Path.Combine(newProjectPath, "roadmap.json"),
                "{ \"phases\": [], \"tasks\": [] }");
            ProjectListBox.Items.Add(name);
        }

        private void OpenProject_Click(object sender, RoutedEventArgs e)
        {
            if (ProjectListBox.SelectedItem == null) return;
            string projectName = ProjectListBox.SelectedItem.ToString() ?? string.Empty;
            var projectWindow = new ProjectWindow(projectName, projectsRoot);
            projectWindow.Show();
        }

        private void ProjectListBox_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            foreach (var file in files)
            {
                string folderName = Path.GetFileNameWithoutExtension(file);
                string destPath = Path.Combine(projectsRoot, folderName);
                Directory.CreateDirectory(destPath);
                string dest = Path.Combine(destPath, Path.GetFileName(file));
                File.Copy(file, dest, true);
                if (!ProjectListBox.Items.Contains(folderName))
                    ProjectListBox.Items.Add(folderName);
            }
        }
    }
}
