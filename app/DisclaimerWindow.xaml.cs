using System.Windows;
using System.Windows.Input;

namespace TalonForge
{
    public partial class DisclaimerWindow : Window
    {
        public bool DontShowAgain => ChkHide.IsChecked == true;

        public DisclaimerWindow()
        {
            InitializeComponent();
            MouseLeftButtonDown += (s, e) => DragMove();
        }

        void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
        void Exit_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
