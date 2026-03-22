using System.Windows;

namespace ArbiterHost
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void OpenWorkspace_Click(object sender, RoutedEventArgs e)
        {
            var workspace = new WorkspaceWindow();
            workspace.Show();
            this.Close();
        }
    }
}
