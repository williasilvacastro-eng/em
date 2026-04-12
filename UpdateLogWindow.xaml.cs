using System.Windows;

namespace emu2026
{
    public partial class UpdateLogWindow : Window
    {
        public UpdateLogWindow()
        {
            InitializeComponent();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
